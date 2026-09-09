using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteExecution
{
    public interface IRemoteExecutionEditorPanel
    {
        string Id { get; }
        string DisplayName { get; }
        int Order { get; }
        bool IsAvailable(RemoteExecutionEditorContext context, out string unavailableReason);
        void DrawGUI(RemoteExecutionEditorContext context);
    }

    public sealed class RemoteExecutionEditorContext
    {
        private readonly Func<string, Func<CancellationToken, Task<string>>, bool> m_StartOperation;
        private bool m_IsValid = true;

        internal RemoteExecutionEditorContext(RemoteExecutionClientInfo selectedPlayer,
            bool isOperationRunning, string operationStatus,
            Func<string, Func<CancellationToken, Task<string>>, bool> startOperation)
        {
            SelectedPlayer = selectedPlayer;
            IsOperationRunning = isOperationRunning;
            OperationStatus = operationStatus ?? string.Empty;
            m_StartOperation = startOperation;
        }

        public RemoteExecutionClientInfo SelectedPlayer { get; }
        public bool IsOperationRunning { get; }
        public string OperationStatus { get; }

        public bool TryStartOperation(string runningStatus,
            Func<CancellationToken, Task<string>> operation)
        {
            if (!m_IsValid) throw new InvalidOperationException(
                "Remote execution editor context is no longer valid.");
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return m_StartOperation != null && m_StartOperation(
                string.IsNullOrWhiteSpace(runningStatus) ? "Running..." : runningStatus,
                operation);
        }

        internal void Invalidate()
        {
            m_IsValid = false;
        }
    }

    public sealed class RemoteExecutionResult
    {
        internal RemoteExecutionResult(bool succeeded, string code, string message,
            byte[] payload, string contentType)
            : this(succeeded, code, message, payload, contentType, false)
        {
        }

        internal RemoteExecutionResult(bool succeeded, string code, string message,
            byte[] payload, string contentType, bool takeOwnership)
        {
            Succeeded = succeeded;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Payload = payload == null || payload.Length == 0
                ? Array.Empty<byte>()
                : takeOwnership ? payload : (byte[])payload.Clone();
            ContentType = contentType ?? string.Empty;
        }

        public bool Succeeded { get; }
        public string Code { get; }
        public string Message { get; }
        public byte[] Payload { get; }
        public string ContentType { get; }
    }

    public sealed class RemoteCommandSnapshot
    {
        internal RemoteCommandSnapshot(RemoteCommandInfo command)
        {
            TypeName = command.TypeName;
            Name = command.Name;
            Description = command.Description;
            Category = command.Category;
            TimeoutSeconds = command.TimeoutSeconds;
            RequestContentType = command.RequestContentType;
            ResponseContentType = command.ResponseContentType;
            Executable = command.Executable;
        }

        public string TypeName { get; }
        public string Name { get; }
        public string Description { get; }
        public string Category { get; }
        public int TimeoutSeconds { get; }
        public string RequestContentType { get; }
        public string ResponseContentType { get; }
        public bool Executable { get; }
    }

    public sealed class RemoteExecutionClientInfo
    {
        internal RemoteExecutionClientInfo(int id, string clientId, string target, string status,
            bool isReady, DateTime commandsUpdatedAt,
            IReadOnlyList<RemoteCommandSnapshot> commands)
        {
            Id = id;
            ClientId = clientId ?? string.Empty;
            Target = target ?? string.Empty;
            Status = status ?? string.Empty;
            IsReady = isReady;
            CommandsUpdatedAt = commandsUpdatedAt;
            Commands = commands ?? Array.Empty<RemoteCommandSnapshot>();
        }

        public int Id { get; }
        public string ClientId { get; }
        public string Target { get; }
        public string Status { get; }
        public string Description => $"{ClientId} ({Target})";
        public bool IsReady { get; }
        public DateTime CommandsUpdatedAt { get; }
        public IReadOnlyList<RemoteCommandSnapshot> Commands { get; }
    }

    public sealed class RemoteExecutionServerOptions
    {
        public RemoteExecutionServerOptions(IRemoteExecutionTransport transport,
            int maxClients = 4, TimeSpan? handshakeTimeout = null)
        {
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));

            if (maxClients < 1 || maxClients > 1024)
                throw new ArgumentOutOfRangeException(nameof(maxClients));
            MaxClients = maxClients;
            HandshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(15);
            if (HandshakeTimeout <= TimeSpan.Zero ||
                HandshakeTimeout > TimeSpan.FromHours(1))
                throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        }

        public IRemoteExecutionTransport Transport { get; }
        public int MaxClients { get; }
        public TimeSpan HandshakeTimeout { get; }
    }

    internal struct RemoteExecutionServerStatus
    {
        internal bool IsRunning;
        internal string TransportKind;
        internal string TransportDescription;
    }

    public static class RemoteExecutionEditorApi
    {
        private static readonly object s_SelectionLock = new object();
        private static int s_SelectedSessionId;
        private static int s_SelectionOwnerId;

        public static bool IsServerRunning => RemoteExecutionServer.IsRunning;
        public static string TransportKind => RemoteExecutionServer.TransportKind;
        public static string TransportDescription =>
            RemoteExecutionServer.TransportDescription;

        internal static RemoteExecutionServerStatus GetServerStatus()
        {
            return RemoteExecutionServer.GetStatus();
        }

        public static void StartServer(string bindAddress = "127.0.0.1",
            int port = 38421, int maxClients = 4,
            TimeSpan? handshakeTimeout = null)
        {
            var transport = RemoteExecutionTcpTransport.CreateServer(bindAddress, port);
            StartServer(new RemoteExecutionServerOptions(transport, maxClients,
                handshakeTimeout));
        }

        public static void StartServer(RemoteExecutionServerOptions options)
        {
            RemoteExecutionServer.Start(options ??
                throw new ArgumentNullException(nameof(options)));
        }

        public static void StopServer()
        {
            RemoteExecutionServer.Stop();
        }

        public static IReadOnlyList<RemoteExecutionClientInfo> GetClients()
        {
            return RemoteExecutionServer.GetClients();
        }

        internal static void SetSelectedSession(int ownerId, int sessionId)
        {
            lock (s_SelectionLock)
            {
                s_SelectionOwnerId = ownerId;
                s_SelectedSessionId = sessionId;
            }
        }

        internal static void ClearSelectedSession(int ownerId)
        {
            lock (s_SelectionLock)
            {
                if (s_SelectionOwnerId != ownerId) return;
                s_SelectionOwnerId = 0;
                s_SelectedSessionId = 0;
            }
        }

        public static Task<RemoteExecutionResult> ExecuteCommandAsync(
            string commandId)
        {
            int sessionId;
            lock (s_SelectionLock) sessionId = s_SelectedSessionId;
            if (sessionId == 0)
                throw new InvalidOperationException(
                    "No Player is selected in the Remote Execution window.");
            return ExecuteCommandAsync(sessionId, commandId);
        }

        public static Task<RemoteExecutionResult> ExecuteCommandAsync<TCommand>(
            byte[] payload = null, CancellationToken cancellationToken = default)
            where TCommand : class, IRemoteCommand
        {
            int sessionId;
            lock (s_SelectionLock) sessionId = s_SelectedSessionId;
            if (sessionId == 0)
                throw new InvalidOperationException(
                    "No Player is selected in the Remote Execution window.");
            return ExecuteCommandAsync<TCommand>(sessionId, payload, cancellationToken);
        }

        public static Task<RemoteExecutionResult> ExecuteCommandAsync<TCommand>(
            int sessionId, byte[] payload = null,
            CancellationToken cancellationToken = default)
            where TCommand : class, IRemoteCommand
        {
            string typeName = RemoteExecutionEditorCommandCatalog.GetTypeName<TCommand>();
            string contentType = RemoteExecutionEditorCommandCatalog
                .GetRequestContentType<TCommand>();
            return ExecuteCommandAsync(sessionId, typeName, payload,
                contentType, cancellationToken);
        }

        public static Task<RemoteExecutionResult> ExecuteCommandAsync(
            int sessionId, string commandId, byte[] payload = null,
            string contentType = "")
        {
            return ExecuteCommandAsync(sessionId, commandId, payload, contentType,
                CancellationToken.None);
        }

        public static Task<RemoteExecutionResult> ExecuteCommandAsync(
            int sessionId, string commandId, byte[] payload, string contentType,
            CancellationToken cancellationToken)
        {
            return RemoteExecutionServer.ExecuteCommandAsync(sessionId, commandId, payload,
                contentType, cancellationToken);
        }

        public static Task RefreshCommandsAsync(int sessionId)
        {
            return RefreshCommandsAsync(sessionId, CancellationToken.None);
        }

        public static Task RefreshCommandsAsync(int sessionId,
            CancellationToken cancellationToken)
        {
            return RemoteExecutionServer.RefreshCommandsAsync(sessionId, cancellationToken);
        }
    }
}
