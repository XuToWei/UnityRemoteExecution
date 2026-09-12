using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace RemoteExecution
{
    [Preserve]
    public sealed class SearchPlayerLogsCommand : IRemoteCommand
    {
        public const string ContentType = "text/plain; charset=utf-8";
        public const int MaxQueryBytes = 4 * 1024;
        private static readonly UTF8Encoding s_Utf8 = new UTF8Encoding(false, true);

        public string Name => "Search Player logs";
        public string Description => "Searches recent log messages and stack traces in the Player.";
        public string Category => "Diagnostics";
        public int TimeoutSeconds => 10;
        public string RequestContentType => ContentType;
        public string ResponseContentType => ContentType;

        public Task<RemoteCommandResult> ExecuteAsync(RemoteCommandContext context,
            CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            cancellationToken.ThrowIfCancellationRequested();
            string query;
            try
            {
                if (context.Payload.Length > MaxQueryBytes)
                    throw new ArgumentException(
                        $"Log search query exceeds {MaxQueryBytes} UTF-8 bytes.");
                query = s_Utf8.GetString(context.Payload);
            }
            catch (ArgumentException exception)
            {
                return Task.FromResult(RemoteCommandResult.Failure(
                    "INVALID_LOG_SEARCH", exception.Message));
            }

            RemoteLogBuffer.EnsureSubscribed();
            string output = RemoteLogBuffer.Search(query, cancellationToken,
                out int totalMatches, out int returnedCount);
            return Task.FromResult(RemoteCommandResult.Success(
                $"Found {totalMatches} matching log entries; returned {returnedCount}.",
                s_Utf8.GetBytes(output), ContentType));
        }
    }

    internal static class RemoteLogBuffer
    {
        private const int MaxEntries = 2000;
        private const int MaxResults = 100;
        private const int MaxBufferedCharacters = 512 * 1024;
        private const int MaxEntryCharacters = 32 * 1024;
        private const int MaxResultBytes = 1024 * 1024;
        private const string TruncatedSuffix = "\n...[truncated]";
        private static readonly UTF8Encoding s_Utf8 = new UTF8Encoding(false, true);
        private static readonly object s_Lock = new object();
        private static readonly Queue<LogEntry> s_Entries = new Queue<LogEntry>();
        private static bool s_Subscribed;
        private static int s_BufferedCharacters;
        private static long s_DroppedCount;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            Application.logMessageReceivedThreaded -= Capture;
            lock (s_Lock)
            {
                s_Entries.Clear();
                s_BufferedCharacters = 0;
                s_DroppedCount = 0;
                s_Subscribed = true;
            }
            Application.logMessageReceivedThreaded += Capture;
        }

        internal static void EnsureSubscribed()
        {
            lock (s_Lock)
            {
                if (s_Subscribed) return;
                s_Subscribed = true;
            }
            Application.logMessageReceivedThreaded += Capture;
        }

        internal static string Search(string query, CancellationToken cancellationToken,
            out int totalMatches, out int returnedCount)
        {
            LogEntry[] snapshot;
            long droppedCount;
            lock (s_Lock)
            {
                snapshot = s_Entries.ToArray();
                droppedCount = s_DroppedCount;
            }

            query = string.IsNullOrWhiteSpace(query) ? string.Empty : query;
            var matches = new List<LogEntry>(Math.Min(MaxResults, snapshot.Length));
            totalMatches = 0;
            for (int i = snapshot.Length - 1; i >= 0; i--)
            {
                if ((i & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                LogEntry entry = snapshot[i];
                if (!Contains(entry, query)) continue;
                totalMatches++;
                if (matches.Count < MaxResults) matches.Add(entry);
            }

            var body = new StringBuilder();
            int bodyBytes = 0;
            returnedCount = 0;
            foreach (LogEntry entry in matches)
            {
                string formatted = Format(entry);
                int formattedBytes = s_Utf8.GetByteCount(formatted);
                if (bodyBytes + formattedBytes > MaxResultBytes) break;
                body.Append(formatted);
                bodyBytes += formattedBytes;
                returnedCount++;
            }

            var result = new StringBuilder();
            result.Append("Matched ").Append(totalMatches).Append(" logs, showing newest ")
                .Append(returnedCount).Append('.');
            if (droppedCount > 0)
                result.Append(" The buffer dropped ").Append(droppedCount)
                    .Append(" older logs.");
            result.Append(body);
            return result.ToString();
        }

        private static void Capture(string message, string stackTrace, LogType type)
        {
            message = Limit(message ?? string.Empty, MaxEntryCharacters);
            stackTrace = Limit(stackTrace ?? string.Empty,
                Math.Max(0, MaxEntryCharacters - message.Length));
            var entry = new LogEntry(DateTime.UtcNow.Ticks, type, message, stackTrace);
            lock (s_Lock)
            {
                s_Entries.Enqueue(entry);
                s_BufferedCharacters += entry.CharacterCount;
                while (s_Entries.Count > MaxEntries ||
                    s_BufferedCharacters > MaxBufferedCharacters)
                {
                    s_BufferedCharacters -= s_Entries.Dequeue().CharacterCount;
                    s_DroppedCount++;
                }
            }
        }

        private static bool Contains(LogEntry entry, string query)
        {
            return query.Length == 0 ||
                entry.Message.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                entry.StackTrace.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Format(LogEntry entry)
        {
            var builder = new StringBuilder();
            builder.AppendLine().AppendLine()
                .Append('[').Append(new DateTime(entry.TimestampUtcTicks,
                    DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'"))
                .Append("] [").Append(entry.Type).Append("] ")
                .Append(entry.Message);
            if (!string.IsNullOrWhiteSpace(entry.StackTrace))
                builder.AppendLine().Append(entry.StackTrace.TrimEnd());
            return builder.ToString();
        }

        private static string Limit(string value, int maxCharacters)
        {
            if (value.Length <= maxCharacters) return value;
            if (maxCharacters <= TruncatedSuffix.Length)
                return value.Substring(0, maxCharacters);
            return value.Substring(0, maxCharacters - TruncatedSuffix.Length) +
                TruncatedSuffix;
        }

        private sealed class LogEntry
        {
            internal LogEntry(long timestampUtcTicks, LogType type, string message,
                string stackTrace)
            {
                TimestampUtcTicks = timestampUtcTicks;
                Type = type;
                Message = message;
                StackTrace = stackTrace;
            }

            internal long TimestampUtcTicks { get; }
            internal LogType Type { get; }
            internal string Message { get; }
            internal string StackTrace { get; }
            internal int CharacterCount => Message.Length + StackTrace.Length;
        }
    }
}
