using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace RemoteExecution
{
    internal sealed class RemoteExecutionPlayerCommandHost : IDisposable
    {
        private readonly object m_CommandLock = new object();
        private readonly object m_LifecycleLock = new object();
        private readonly HashSet<Guid> m_ActiveRequestIds = new HashSet<Guid>();
        private readonly RemotePayloadReceiver m_InputReceiver = new RemotePayloadReceiver(
            RemoteExecutionProtocol.MaxCommandRequestBytes,
            RemoteExecutionProtocol.DefaultMaxCommandRequestBytes);
        private bool m_Disposed;
        private RemoteCommandCatalog m_Catalog;
        private long m_Generation;
        private RemoteExecutionPlayerConfiguration m_Configuration;
        private Action<RemoteMessageKind, Guid, byte[]> m_Send;
        private IncomingCommandInput m_IncomingCommandInput;
        private RunningCommand m_RunningCommand;

        internal void Initialize()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(RemoteExecutionPlayerCommandHost));
            m_Catalog = DiscoverCommands();
        }

        internal void BeginConnection(long generation,
            RemoteExecutionPlayerConfiguration configuration,
            Action<RemoteMessageKind, Guid, byte[]> send)
        {
            if (m_Catalog == null) throw new InvalidOperationException("Command host is not initialized.");
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (send == null) throw new ArgumentNullException(nameof(send));
            lock (m_LifecycleLock)
            {
                m_Generation = generation;
                m_Configuration = configuration;
                m_Send = send;
                m_InputReceiver.Reset();
            }
        }

        internal void CancelConnection(long generation)
        {
            lock (m_LifecycleLock)
            {
                if (generation != m_Generation) return;
                m_Send = null;
                m_Configuration = null;
            }
            RunningCommand command;
            lock (m_CommandLock)
            {
                command = m_RunningCommand?.Generation == generation ? m_RunningCommand : null;
                m_ActiveRequestIds.Clear();
            }
            command?.Cancel();
            ResetCommandInput();
            m_Catalog = null;
        }

        internal void HandleFrame(long generation, RemoteFrame frame)
        {
            lock (m_LifecycleLock)
                if (generation != m_Generation || m_Send == null) return;
            try
            {
                if (frame.RequestId == Guid.Empty &&
                    frame.Kind != RemoteMessageKind.Ping && frame.Kind != RemoteMessageKind.Pong)
                    throw new InvalidDataException("Request ID is required.");
                switch (frame.Kind)
                {
                    case RemoteMessageKind.ListCommands:
                        Send(generation, RemoteMessageKind.Commands, frame.RequestId,
                            EncodeCommands());
                        break;
                    case RemoteMessageKind.CommandInputBegin:
                        BeginCommandInput(generation, frame);
                        break;
                    case RemoteMessageKind.CommandInputChunk:
                        WriteCommandChunk(frame);
                        break;
                    case RemoteMessageKind.CommandInputEnd:
                        EndCommandInput(generation, frame);
                        break;
                    case RemoteMessageKind.CancelCommand:
                        CancelCommand(generation, frame);
                        break;
                    case RemoteMessageKind.Ping:
                        Send(generation, RemoteMessageKind.Pong, frame.RequestId,
                            Array.Empty<byte>());
                        break;
                    default:
                        Send(generation, RemoteMessageKind.Error, frame.RequestId,
                            RemoteExecutionProtocol.EncodeError("UNKNOWN_MESSAGE", frame.Kind.ToString()));
                        break;
                }
            }
            catch (Exception exception)
            {
                Guid inputRequestId = m_IncomingCommandInput?.RequestId ?? Guid.Empty;
                ResetCommandInput();
                if (inputRequestId != Guid.Empty) EndRequest(generation, inputRequestId);
                bool isCommand = IsCommandMessage(frame.Kind);
                Send(generation,
                    isCommand ? RemoteMessageKind.CommandResult : RemoteMessageKind.Error,
                    frame.RequestId,
                    isCommand
                        ? RemoteExecutionProtocol.EncodeCommandResult(false, "PROTOCOL_ERROR",
                            exception.Message, string.Empty, null)
                        : RemoteExecutionProtocol.EncodeError("PROTOCOL_ERROR", exception.Message));
            }
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            CancelConnection(m_Generation);
            m_Catalog = null;
            m_InputReceiver.Dispose();
        }

        private static RemoteCommandCatalog DiscoverCommands()
        {
#if UNITY_EDITOR
            return RemoteCommandCatalog.Discover(Array.Empty<Type>());
#else
            var commandTypes = new List<Type>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (Type type in RemoteCommandCatalog.GetLoadableTypes(assembly))
                {
                    if (RemoteCommandCatalog.IsCommandType(type)) commandTypes.Add(type);
                }
            }
            return RemoteCommandCatalog.Discover(commandTypes);
#endif
        }

        private void BeginCommandInput(long generation, RemoteFrame frame)
        {
            if (m_IncomingCommandInput != null)
                throw new InvalidOperationException("Another transfer is active.");
            RemoteExecutionProtocol.DecodeCommandInputBegin(frame.Payload,
                out string commandType, out string contentType, out long length, out byte[] hash);
            if (!m_Catalog.TryGet(commandType, out RemoteCommandDescriptor descriptor))
                throw new InvalidOperationException("Command is not executable.");
            RemoteExecutionPlayerConfiguration configuration;
            lock (m_LifecycleLock) configuration = m_Configuration;
            if (configuration == null || length > configuration.MaxCommandRequestBytes ||
                !ContentTypeMatches(descriptor.RequestContentType, contentType))
                throw new InvalidDataException("Command input exceeds the command limits.");
            BeginRequest(generation, frame.RequestId);
            try
            {
                m_InputReceiver.Begin(length, hash);
                m_IncomingCommandInput = new IncomingCommandInput(generation,
                    frame.RequestId, commandType, contentType);
            }
            catch
            {
                EndRequest(generation, frame.RequestId);
                throw;
            }
        }

        private void WriteCommandChunk(RemoteFrame frame)
        {
            EnsureCommandInput(frame.RequestId);
            RemoteExecutionProtocol.DecodeCommandChunk(frame.Payload, out long offset,
                out byte[] data);
            m_InputReceiver.Append(offset, data);
        }

        private void EndCommandInput(long generation, RemoteFrame frame)
        {
            EnsureCommandInput(frame.RequestId);
            RemoteExecutionProtocol.DecodeCommandEnd(frame.Payload);
            IncomingCommandInput input = m_IncomingCommandInput;
            m_IncomingCommandInput = null;
            byte[] bytes;
            try { bytes = m_InputReceiver.Complete(); }
            catch
            {
                EndRequest(generation, frame.RequestId);
                throw;
            }
            if (!m_Catalog.TryGet(input.TypeName, out RemoteCommandDescriptor descriptor))
            {
                EndRequest(generation, frame.RequestId);
                throw new InvalidOperationException("Command is no longer registered.");
            }
            ExecuteCommand(generation, frame.RequestId, descriptor, bytes,
                input.ContentType).Forget();
        }

        private async Task ExecuteCommand(long generation, Guid requestId,
            RemoteCommandDescriptor descriptor, byte[] payload, string contentType)
        {
            RemoteCommandCatalog catalog = m_Catalog;
            if (catalog == null)
            {
                EndRequest(generation, requestId);
                return;
            }
            RunningCommand command = null;
            lock (m_CommandLock)
            {
                if (m_RunningCommand == null)
                    m_RunningCommand = command = new RunningCommand(
                        generation, requestId, descriptor.TimeoutSeconds);
            }
            if (command == null)
            {
                Send(generation, RemoteMessageKind.CommandResult, requestId,
                    RemoteExecutionProtocol.EncodeCommandResult(false, "COMMAND_BUSY",
                        "Another command is running.", string.Empty, null));
                EndRequest(generation, requestId);
                return;
            }
            try
            {
                var context = new RemoteCommandContext(descriptor.TypeName, "Editor", payload,
                    contentType, command.Token);
                Task<RemoteCommandResult> execution = catalog.ExecuteAsync(
                    descriptor, context, command.Token);
                if (!execution.IsCompleted)
                    Debug.LogWarning($"[Unity.RemoteExecution] command '{descriptor.TypeName}' continued asynchronously; code after the first await is not guaranteed to run on the Unity main thread.");
                RemoteCommandResult result = await execution.ConfigureAwait(false);
                command.Token.ThrowIfCancellationRequested();
                SendCommandResult(generation, requestId, result);
            }
            catch (OperationCanceledException)
            {
                string code = command.CancelledRemotely ? "COMMAND_CANCELLED" : "COMMAND_TIMED_OUT";
                string message = command.CancelledRemotely ? "Command was cancelled." : "Command timed out.";
                Send(generation, RemoteMessageKind.CommandResult, requestId,
                    RemoteExecutionProtocol.EncodeCommandResult(false, code, message,
                        string.Empty, null));
            }
            catch (Exception exception)
            {
                Send(generation, RemoteMessageKind.CommandResult, requestId,
                    RemoteExecutionProtocol.EncodeCommandResult(false,
                        "COMMAND_EXECUTION_FAILED", exception.Message, string.Empty, null));
            }
            finally
            {
                lock (m_CommandLock)
                    if (ReferenceEquals(m_RunningCommand, command)) m_RunningCommand = null;
                command.Dispose();
                EndRequest(generation, requestId);
            }
        }

        private void SendCommandResult(long generation, Guid requestId,
            RemoteCommandResult result)
        {
            byte[] payload = result.Payload ?? Array.Empty<byte>();
            RemoteExecutionPlayerConfiguration configuration;
            lock (m_LifecycleLock)
            {
                if (generation != m_Generation) return;
                configuration = m_Configuration;
            }
            if (configuration == null) return;
            if (payload.Length > configuration.MaxCommandResponseBytes)
                throw new InvalidDataException(
                    "Command result exceeds the configured response limit.");
            Send(generation, RemoteMessageKind.CommandResult, requestId,
                RemoteExecutionProtocol.EncodeCommandResult(result.Succeeded, result.Code,
                    result.Message, result.ContentType, payload));
            if (payload.Length == 0) return;
            for (int offset = 0; offset < payload.Length;
                offset += RemoteExecutionProtocol.MaxChunkBytes)
            {
                int count = Math.Min(RemoteExecutionProtocol.MaxChunkBytes,
                    payload.Length - offset);
                Send(generation, RemoteMessageKind.CommandResultChunk, requestId,
                    RemoteExecutionProtocol.EncodeCommandResultChunk(offset, payload,
                        offset, count));
            }
            Send(generation, RemoteMessageKind.CommandResultEnd, requestId,
                RemoteExecutionProtocol.EncodeCommandResultEnd());
        }

        private void CancelCommand(long generation, RemoteFrame frame)
        {
            if (m_IncomingCommandInput != null &&
                m_IncomingCommandInput.Generation == generation &&
                m_IncomingCommandInput.RequestId == frame.RequestId)
            {
                ResetCommandInput();
                EndRequest(generation, frame.RequestId);
            }
            RunningCommand command;
            lock (m_CommandLock)
                command = m_RunningCommand?.Generation == generation &&
                    m_RunningCommand.RequestId == frame.RequestId ? m_RunningCommand : null;
            command?.Cancel(remotely: true);
        }

        private void EnsureCommandInput(Guid requestId)
        {
            if (m_IncomingCommandInput == null ||
                m_IncomingCommandInput.Generation != m_Generation ||
                m_IncomingCommandInput.RequestId != requestId)
                throw new InvalidDataException("No matching command input.");
        }

        private void ResetCommandInput()
        {
            m_IncomingCommandInput = null;
            m_InputReceiver.Reset();
        }

        private void BeginRequest(long generation, Guid requestId)
        {
            lock (m_LifecycleLock)
            lock (m_CommandLock)
            {
                if (generation != m_Generation || !m_ActiveRequestIds.Add(requestId))
                    throw new InvalidDataException("Duplicate or retired request ID.");
            }
        }

        private void EndRequest(long generation, Guid requestId)
        {
            lock (m_LifecycleLock)
            lock (m_CommandLock)
            {
                if (generation == m_Generation) m_ActiveRequestIds.Remove(requestId);
            }
        }

        private byte[] EncodeCommands()
        {
            RemoteExecutionPlayerConfiguration configuration;
            lock (m_LifecycleLock) configuration = m_Configuration;
            if (configuration == null) return RemoteExecutionProtocol.EncodeCommands(
                Array.Empty<RemoteCommandInfo>());
            RemoteCommandCatalog catalog = m_Catalog;
            if (catalog == null) return RemoteExecutionProtocol.EncodeCommands(
                Array.Empty<RemoteCommandInfo>());
            return RemoteExecutionProtocol.EncodeCommands(
                catalog.EncodeInfos());
        }

        private void Send(long generation, RemoteMessageKind kind, Guid requestId,
            byte[] payload)
        {
            Action<RemoteMessageKind, Guid, byte[]> send;
            lock (m_LifecycleLock)
            {
                if (generation != m_Generation) return;
                send = m_Send;
            }
            send?.Invoke(kind, requestId, payload);
        }


        private static bool IsCommandMessage(RemoteMessageKind kind)
        {
            return kind == RemoteMessageKind.CommandInputBegin ||
                kind == RemoteMessageKind.CommandInputChunk ||
                kind == RemoteMessageKind.CommandInputEnd ||
                kind == RemoteMessageKind.CancelCommand;
        }

        private static bool ContentTypeMatches(string expected, string actual)
        {
            return string.IsNullOrEmpty(expected) ||
                string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class IncomingCommandInput
        {
            internal IncomingCommandInput(long generation, Guid requestId,
                string typeName, string contentType)
            {
                Generation = generation;
                RequestId = requestId;
                TypeName = typeName;
                ContentType = contentType;
            }

            internal long Generation { get; }
            internal Guid RequestId { get; }
            internal string TypeName { get; }
            internal string ContentType { get; }
        }

        private sealed class RunningCommand : IDisposable
        {
            private readonly CancellationTokenSource m_Cancellation = new CancellationTokenSource();
            private int m_CancelledRemotely;

            internal RunningCommand(long generation, Guid requestId, int timeoutSeconds)
            {
                Generation = generation;
                RequestId = requestId;
                Token = m_Cancellation.Token;
                m_Cancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            }

            internal long Generation { get; }
            internal Guid RequestId { get; }
            internal CancellationToken Token { get; }
            internal bool CancelledRemotely => Volatile.Read(ref m_CancelledRemotely) != 0;

            internal void Cancel(bool remotely = false)
            {
                if (remotely) Interlocked.Exchange(ref m_CancelledRemotely, 1);
                try { m_Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            public void Dispose() => m_Cancellation.Dispose();
        }
    }
}
