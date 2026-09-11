using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteExecution
{
    internal sealed class RemoteExecutionPlayerCommandHost : IDisposable
    {
        // The driver dispatches lifecycle calls and frames on Unity's main thread.
        // Command continuations also retain that context, so this state needs no locks.
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
            m_Generation = generation;
            m_Configuration = configuration;
            m_Send = send;
            ResetCommandInput();
        }

        internal void CancelConnection(long generation)
        {
            if (generation != m_Generation) return;
            m_Send = null;
            m_Configuration = null;
            ResetCommandInput();
            m_Catalog = null;
            // Keep the execution slot until the cancelled command actually finishes.
            if (m_RunningCommand?.Generation == generation) m_RunningCommand.Cancel();
        }

        internal void HandleFrame(long generation, RemoteFrame frame)
        {
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
                            RemoteExecutionProtocol.EncodeCommands(m_Catalog.EncodeInfos()));
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
                ResetCommandInput();
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
            if (m_RunningCommand?.Generation == generation &&
                m_RunningCommand.RequestId == frame.RequestId)
                throw new InvalidDataException("Duplicate request ID.");
            RemoteExecutionProtocol.DecodeCommandInputBegin(frame.Payload,
                out string commandType, out string contentType, out long length, out byte[] hash);
            if (!m_Catalog.TryGet(commandType, out RemoteCommandDescriptor descriptor))
                throw new InvalidOperationException("Command is not executable.");
            if (length > m_Configuration.MaxCommandRequestBytes ||
                !ContentTypeMatches(descriptor.RequestContentType, contentType))
                throw new InvalidDataException("Command input exceeds the command limits.");
            m_InputReceiver.Begin(length, hash);
            m_IncomingCommandInput = new IncomingCommandInput(
                frame.RequestId, descriptor, contentType);
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
            byte[] bytes = m_InputReceiver.Complete();
            ExecuteCommand(generation, frame.RequestId, input.Descriptor, bytes,
                input.ContentType).Forget();
        }

        private async Task ExecuteCommand(long generation, Guid requestId,
            RemoteCommandDescriptor descriptor, byte[] payload, string contentType)
        {
            if (m_RunningCommand != null)
            {
                Send(generation, RemoteMessageKind.CommandResult, requestId,
                    RemoteExecutionProtocol.EncodeCommandResult(false, "COMMAND_BUSY",
                        "Another command is running.", string.Empty, null));
                return;
            }
            var command = new RunningCommand(generation, requestId, descriptor.TimeoutSeconds);
            m_RunningCommand = command;
            try
            {
                var context = new RemoteCommandContext(descriptor.TypeName, "Editor", payload,
                    contentType, command.Token);
                // Preserve Unity's synchronization context for result handling and cleanup.
                RemoteCommandResult result = await m_Catalog.ExecuteAsync(
                    descriptor, context, command.Token);
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
                m_RunningCommand = null;
                command.Dispose();
            }
        }

        private void SendCommandResult(long generation, Guid requestId,
            RemoteCommandResult result)
        {
            if (generation != m_Generation || m_Send == null) return;
            byte[] payload = result.Payload;
            if (payload.Length > m_Configuration.MaxCommandResponseBytes)
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
            if (m_IncomingCommandInput?.RequestId == frame.RequestId)
                ResetCommandInput();
            if (m_RunningCommand?.Generation == generation &&
                m_RunningCommand.RequestId == frame.RequestId)
                m_RunningCommand.Cancel(remotely: true);
        }

        private void EnsureCommandInput(Guid requestId)
        {
            if (m_IncomingCommandInput?.RequestId != requestId)
                throw new InvalidDataException("No matching command input.");
        }

        private void ResetCommandInput()
        {
            m_IncomingCommandInput = null;
            m_InputReceiver.Reset();
        }

        private void Send(long generation, RemoteMessageKind kind, Guid requestId,
            byte[] payload)
        {
            if (generation == m_Generation) m_Send?.Invoke(kind, requestId, payload);
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
            internal IncomingCommandInput(Guid requestId,
                RemoteCommandDescriptor descriptor, string contentType)
            {
                RequestId = requestId;
                Descriptor = descriptor;
                ContentType = contentType;
            }

            internal Guid RequestId { get; }
            internal RemoteCommandDescriptor Descriptor { get; }
            internal string ContentType { get; }
        }

        private sealed class RunningCommand : IDisposable
        {
            private readonly CancellationTokenSource m_Cancellation = new CancellationTokenSource();

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
            internal bool CancelledRemotely { get; private set; }

            internal void Cancel(bool remotely = false)
            {
                if (remotely) CancelledRemotely = true;
                m_Cancellation.Cancel();
            }

            public void Dispose() => m_Cancellation.Dispose();
        }
    }
}
