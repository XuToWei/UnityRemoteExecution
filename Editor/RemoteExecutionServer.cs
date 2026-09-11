using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace RemoteExecution
{
    [InitializeOnLoad]
    internal static class RemoteExecutionServer
    {
        private const int CommandResponseGraceSeconds = 5;
        private const int MaxTransportKindLength = 64;
        private static readonly object s_Lock = new object();
        private static readonly Dictionary<int, ClientSession> s_Sessions =
            new Dictionary<int, ClientSession>();
        private static IRemoteExecutionTransport s_Transport;
        private static CancellationTokenSource s_Cancellation;
        private static int s_NextSessionId;
        private static int s_MaxClients;
        private static TimeSpan s_HandshakeTimeout;
        private static string s_TransportKind = string.Empty;
        private static string s_TransportDescription = string.Empty;
        private static long s_Generation;

        static RemoteExecutionServer()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        internal static bool IsRunning
        {
            get { lock (s_Lock) return s_Transport != null; }
        }

        internal static string TransportKind
        {
            get { lock (s_Lock) return s_TransportKind; }
        }

        internal static string TransportDescription
        {
            get { lock (s_Lock) return s_TransportDescription; }
        }

        internal static RemoteExecutionServerStatus GetStatus()
        {
            lock (s_Lock)
            {
                return new RemoteExecutionServerStatus
                {
                    IsRunning = s_Transport != null,
                    TransportKind = s_TransportKind,
                    TransportDescription = s_TransportDescription,
                    IsLoopbackListener = s_Transport is RemoteExecutionTcpTransport tcp &&
                        tcp.IsLoopbackListener
                };
            }
        }

        internal static IReadOnlyList<RemoteExecutionClientInfo> GetClients()
        {
            lock (s_Lock)
            {
                return s_Sessions.Values
                    .OrderBy(session => session.Id)
                    .Select(session => session.CreateInfo())
                    .ToArray();
            }
        }

        internal static void Start(RemoteExecutionServerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            IRemoteExecutionTransport transport = options.Transport;
            lock (s_Lock)
            {
                if (ReferenceEquals(transport, s_Transport))
                    throw new InvalidOperationException(
                        "The active transport instance cannot be started again.");
            }
            string kind;
            string description;
            try
            {
                kind = ValidateTransportKind(transport.Kind);
                Stop();
                RemoteExecutionEditorCommandCatalog.Discover();
                transport.StartListening();
                description = (transport.Description ?? string.Empty).Trim();
                if (description.Length == 0)
                    throw new InvalidOperationException(
                        "Transport description is required after listening starts.");
            }
            catch
            {
                DisposeTransport(transport);
                throw;
            }

            var cancellation = new CancellationTokenSource();
            long generation;
            lock (s_Lock)
            {
                s_Transport = transport;
                s_Cancellation = cancellation;
                s_MaxClients = options.MaxClients;
                s_HandshakeTimeout = options.HandshakeTimeout;
                s_TransportKind = kind;
                s_TransportDescription = description;
                generation = ++s_Generation;
            }
            AcceptLoopAsync(transport, generation, cancellation.Token).Forget();
            Debug.Log($"[Unity.RemoteExecution] listening on {description}");
        }

        internal static void Stop()
        {
            IRemoteExecutionTransport transport;
            CancellationTokenSource cancellation;
            lock (s_Lock)
            {
                transport = s_Transport;
                cancellation = s_Cancellation;
                s_Transport = null;
                s_Cancellation = null;
                s_TransportKind = string.Empty;
                s_TransportDescription = string.Empty;
                ++s_Generation;
            }
            cancellation?.Cancel();
            DisposeTransport(transport);
            ClientSession[] sessions;
            lock (s_Lock)
            {
                sessions = s_Sessions.Values.ToArray();
                s_Sessions.Clear();
            }
            foreach (ClientSession session in sessions) session.Dispose();
            cancellation?.Dispose();
            RemoteExecutionEditorCommandCatalog.Clear();
        }

        internal static Task<RemoteExecutionResult> ExecuteCommandAsync(int sessionId,
            string commandType, byte[] payload, string contentType,
            CancellationToken cancellationToken)
        {
            if (!TryGetSession(sessionId, out ClientSession session))
                throw new InvalidOperationException("Client is no longer connected.");
            return session.ExecuteCommandAsync(commandType, payload ?? Array.Empty<byte>(),
                contentType ?? string.Empty, cancellationToken);
        }

        internal static Task RefreshCommandsAsync(int sessionId,
            CancellationToken cancellationToken)
        {
            if (!TryGetSession(sessionId, out ClientSession session))
                throw new InvalidOperationException("Client is no longer connected.");
            return session.RefreshCommandsAsync(cancellationToken);
        }

        private static bool TryGetSession(int id, out ClientSession session)
        {
            lock (s_Lock) return s_Sessions.TryGetValue(id, out session);
        }

        private static async Task AcceptLoopAsync(IRemoteExecutionTransport transport,
            long generation, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    IRemoteExecutionChannel channel = await transport.AcceptAsync(
                        cancellationToken).ConfigureAwait(false);
                    if (channel == null)
                        throw new InvalidOperationException(
                            "The transport returned no channel.");
                    ClientSession session = null;
                    lock (s_Lock)
                    {
                        if (generation == s_Generation &&
                            ReferenceEquals(transport, s_Transport) &&
                            s_Sessions.Count < s_MaxClients)
                        {
                            session = new ClientSession(++s_NextSessionId, channel,
                                s_HandshakeTimeout);
                            s_Sessions.Add(session.Id, session);
                        }
                    }
                    if (session == null)
                    {
                        DisposeChannel(channel);
                        continue;
                    }
                    session.RunAsync(cancellationToken).Forget();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Debug.LogWarning(
                        $"[Unity.RemoteExecution] transport stopped: {exception.Message}");
                    StopFailedTransport(transport, generation);
                }
            }
        }

        private static void StopFailedTransport(IRemoteExecutionTransport transport,
            long generation)
        {
            CancellationTokenSource cancellation;
            ClientSession[] sessions;
            lock (s_Lock)
            {
                if (generation != s_Generation ||
                    !ReferenceEquals(transport, s_Transport)) return;
                cancellation = s_Cancellation;
                s_Transport = null;
                s_Cancellation = null;
                s_TransportKind = string.Empty;
                s_TransportDescription = string.Empty;
                ++s_Generation;
                sessions = s_Sessions.Values.ToArray();
                s_Sessions.Clear();
            }
            cancellation?.Cancel();
            DisposeTransport(transport);
            foreach (ClientSession session in sessions) session.Dispose();
            cancellation?.Dispose();
            RemoteExecutionEditorCommandCatalog.Clear();
        }

        private static string ValidateTransportKind(string kind)
        {
            string value = (kind ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > MaxTransportKindLength ||
                value.Any(char.IsControl))
                throw new InvalidOperationException(
                    $"Transport kind must contain 1..{MaxTransportKindLength} printable characters.");
            return value;
        }

        private static void DisposeTransport(IRemoteExecutionTransport transport)
        {
            if (transport == null) return;
            try { transport.Dispose(); }
            catch (Exception) { }
        }

        private static void DisposeChannel(IRemoteExecutionChannel channel)
        {
            if (channel == null) return;
            try { channel.Abort(); }
            catch (Exception) { }
            try { channel.Dispose(); }
            catch (Exception) { }
        }

        private sealed class ClientSession : IDisposable
        {
            private readonly IRemoteExecutionChannel m_Channel;
            private readonly TimeSpan m_HandshakeTimeout;
            private readonly object m_StateLock = new object();
            private readonly CancellationTokenSource m_Cancellation = new CancellationTokenSource();
            private readonly SemaphoreSlim m_SendLock = new SemaphoreSlim(1, 1);
            private readonly SemaphoreSlim m_OperationLock = new SemaphoreSlim(1, 1);
            private readonly SemaphoreSlim m_CatalogLock = new SemaphoreSlim(1, 1);
            private readonly Dictionary<Guid, PendingOperation> m_Pending =
                new Dictionary<Guid, PendingOperation>();
            private readonly object m_PendingLock = new object();
            private readonly object m_CatalogStateLock = new object();
            private readonly object m_ResponseLock = new object();
            private readonly RemotePayloadReceiver m_ResponseReceiver = new RemotePayloadReceiver(
                RemoteExecutionProtocol.MaxCommandResponseBytes,
                RemoteExecutionProtocol.DefaultMaxCommandResponseBytes);
            private bool m_IsReady;
            private volatile bool m_Disposed;
            private string m_Status = "Connecting";
            private string m_ClientId = "Unknown";
            private string m_Target = string.Empty;
            private RemoteCommandInfo[] m_Commands = Array.Empty<RemoteCommandInfo>();
            private DateTime m_CommandsUpdatedAt;

            internal ClientSession(int id, IRemoteExecutionChannel channel,
                TimeSpan handshakeTimeout)
            {
                Id = id;
                m_Channel = channel ?? throw new ArgumentNullException(nameof(channel));
                m_HandshakeTimeout = handshakeTimeout;
            }

            internal int Id { get; }

            internal RemoteExecutionClientInfo CreateInfo()
            {
                RemoteCommandSnapshot[] commands;
                DateTime updatedAt;
                string clientId;
                string target;
                string status;
                bool isReady;
                lock (m_StateLock)
                {
                    clientId = m_ClientId;
                    target = m_Target;
                    status = m_Status;
                    isReady = m_IsReady;
                }
                lock (m_CatalogStateLock)
                {
                    commands = m_Commands.Select(command =>
                        new RemoteCommandSnapshot(command)).ToArray();
                    updatedAt = m_CommandsUpdatedAt;
                }
                return new RemoteExecutionClientInfo(Id, clientId, target, status,
                    isReady, updatedAt, commands);
            }

            internal async Task RunAsync(CancellationToken serverToken)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    serverToken, m_Cancellation.Token))
                {
                    try
                    {
                        RemoteFrame hello = await ReadInitialHelloAsync(linked.Token)
                            .ConfigureAwait(false);
                        if (hello.Kind != RemoteMessageKind.Hello || hello.RequestId == Guid.Empty)
                            throw new InvalidDataException("Hello with a request ID is required.");
                        RemoteHello data = RemoteExecutionProtocol.DecodeHello(hello.Payload);
                        lock (m_StateLock)
                        {
                            m_ClientId = data.ClientId;
                            m_Target = data.Target;
                        }
                        await SendControlFrameAsync(new RemoteFrame(RemoteMessageKind.Ready,
                            hello.RequestId, Array.Empty<byte>()), linked.Token)
                            .ConfigureAwait(false);
                        lock (m_StateLock)
                        {
                            m_IsReady = true;
                            m_Status = "Ready";
                        }
                        RequestCommandsInBackground();
                        while (!linked.IsCancellationRequested)
                        {
                            RemoteFrame frame = await m_Channel.ReceiveAsync(linked.Token)
                                .ConfigureAwait(false);
                            RemoteExecutionProtocol.ValidateFrame(frame);
                            HandleResponse(frame);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception exception)
                    {
                        lock (m_StateLock) m_Status = $"Error: {exception.Message}";
                        FailPending(exception);
                        Debug.LogWarning(
                            $"[Unity.RemoteExecution] client {Id} stopped: {exception.Message}");
                    }
                }
                Dispose();
                lock (s_Lock) s_Sessions.Remove(Id);
            }

            internal async Task RefreshCommandsAsync(CancellationToken cancellationToken)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    m_Cancellation.Token, cancellationToken))
                {
                    await m_CatalogLock.WaitAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        lock (m_StateLock)
                            if (!m_IsReady)
                                throw new InvalidOperationException("Client is not ready.");
                        await RequestCommandsAsync(linked.Token).ConfigureAwait(false);
                    }
                    finally { m_CatalogLock.Release(); }
                }
            }

            internal async Task<RemoteExecutionResult> ExecuteCommandAsync(string commandType,
                byte[] payload, string contentType, CancellationToken cancellationToken)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    m_Cancellation.Token, cancellationToken))
                {
                    await m_OperationLock.WaitAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        lock (m_StateLock)
                            if (!m_IsReady)
                                throw new InvalidOperationException("Client is not ready.");
                        RemoteCommandInfo command = FindCommand(commandType);
                        if (command == null)
                            throw new InvalidOperationException(
                                $"Remote command type was not found: {commandType}");
                        if (!command.Executable)
                            throw new InvalidOperationException("Remote command is unavailable.");
                        if (payload.Length > RemoteExecutionProtocol.MaxCommandRequestBytes)
                            throw new InvalidDataException("Command payload exceeds the advertised limit.");
                        if (!ContentTypeMatches(command.RequestContentType, contentType))
                            throw new InvalidDataException("Command payload content type does not match the command.");

                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(
                            command.TimeoutSeconds + CommandResponseGraceSeconds)))
                        using (var operation = CancellationTokenSource.CreateLinkedTokenSource(
                            linked.Token, deadline.Token))
                        {
                            Guid requestId = Guid.NewGuid();
                            var pending = new PendingOperation(command.ResponseContentType);
                            AddPending(requestId, pending);
                            try
                            {
                                byte[] hash = ComputeHash(payload);
                                await SendAsync(
                                    new RemoteFrame(RemoteMessageKind.CommandInputBegin, requestId,
                                        RemoteExecutionProtocol.EncodeCommandInputBegin(command.TypeName,
                                            contentType, payload.LongLength, hash)),
                                    operation.Token).ConfigureAwait(false);
                                for (int offset = 0; offset < payload.Length;
                                    offset += RemoteExecutionProtocol.MaxChunkBytes)
                                {
                                    int count = Math.Min(RemoteExecutionProtocol.MaxChunkBytes,
                                        payload.Length - offset);
                                    await SendAsync(
                                        new RemoteFrame(RemoteMessageKind.CommandInputChunk, requestId,
                                            RemoteExecutionProtocol.EncodeCommandChunk(offset, payload,
                                                offset, count)),
                                        operation.Token).ConfigureAwait(false);
                                }
                                await SendAsync(
                                    new RemoteFrame(RemoteMessageKind.CommandInputEnd, requestId,
                                        RemoteExecutionProtocol.EncodeCommandEnd()),
                                    operation.Token).ConfigureAwait(false);
                                return await WaitForResponseAsync(
                                    pending.CommandCompletion.Task, operation.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                SendCancelCommand(requestId);
                                cancellationToken.ThrowIfCancellationRequested();
                                if (deadline.IsCancellationRequested)
                                    throw new TimeoutException(
                                        $"Timed out sending the command or waiting for its response ({command.TimeoutSeconds} seconds plus response grace).");
                                throw;
                            }
                            finally { RemovePending(requestId); }
                        }
                    }
                    finally { m_OperationLock.Release(); }
                }
            }

            private static async Task<T> WaitForResponseAsync<T>(Task<T> response,
                CancellationToken cancellationToken)
            {
                var cancelled = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                {
                    await Task.WhenAny(response, cancelled.Task).ConfigureAwait(false);
                    if (!response.IsCompleted) cancellationToken.ThrowIfCancellationRequested();
                    return await response.ConfigureAwait(false);
                }
            }

            private void SendCancelCommand(Guid requestId)
            {
                if (m_Disposed || m_Cancellation.IsCancellationRequested) return;
                SendControlFrameAsync(
                    new RemoteFrame(RemoteMessageKind.CancelCommand, requestId,
                        Array.Empty<byte>()),
                    m_Cancellation.Token).Forget();
            }

            private async Task RequestCommandsAsync(CancellationToken cancellationToken)
            {
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var operation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, deadline.Token))
                {
                    Guid requestId = Guid.NewGuid();
                    var pending = new PendingOperation();
                    AddPending(requestId, pending);
                    try
                    {
                        await SendAsync(
                            new RemoteFrame(RemoteMessageKind.ListCommands, requestId,
                                Array.Empty<byte>()),
                            operation.Token).ConfigureAwait(false);
                        UpdateCommands(await WaitForResponseAsync(
                            pending.CatalogCompletion.Task, operation.Token).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                    {
                        throw new TimeoutException("Timed out requesting the Player command catalog.");
                    }
                    finally { RemovePending(requestId); }
                }
            }

            private void RequestCommandsInBackground()
            {
                RefreshCommandsAsync(m_Cancellation.Token).ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        lock (m_StateLock)
                            m_Status = "Ready (command catalog unavailable)";
                }, TaskScheduler.Default);
            }

            private void UpdateCommands(RemoteCommandInfo[] commands)
            {
                lock (m_CatalogStateLock)
                {
                    m_Commands = (commands ?? Array.Empty<RemoteCommandInfo>())
                        .OrderBy(command => command.TypeName, StringComparer.Ordinal)
                        .ToArray();
                    m_CommandsUpdatedAt = DateTime.UtcNow;
                }
                lock (m_StateLock) m_Status = "Ready";
            }

            private RemoteCommandInfo FindCommand(string commandType)
            {
                lock (m_CatalogStateLock)
                {
                    return m_Commands.FirstOrDefault(command =>
                        string.Equals(command.TypeName, commandType, StringComparison.Ordinal));
                }
            }

            private void HandleResponse(RemoteFrame frame)
            {
                if (frame.Kind == RemoteMessageKind.Hello || frame.Kind == RemoteMessageKind.Ready)
                    throw new InvalidDataException("Unexpected handshake frame.");
                if (frame.Kind == RemoteMessageKind.Ping)
                {
                    SendControlFrameAsync(
                        new RemoteFrame(RemoteMessageKind.Pong, frame.RequestId,
                            Array.Empty<byte>()),
                        m_Cancellation.Token).Forget();
                    return;
                }
                lock (m_ResponseLock)
                {
                    HandleResponseLocked(frame);
                }
            }

            private void HandleResponseLocked(RemoteFrame frame)
            {
                PendingOperation pending = GetPending(frame.RequestId);
                if (pending == null) return;
                try
                {
                    switch (frame.Kind)
                    {
                        case RemoteMessageKind.Commands:
                            if (pending.IsCommand)
                                throw new InvalidDataException(
                                    "Command operation received a catalog response.");
                            pending.CatalogCompletion.TrySetResult(
                                RemoteExecutionProtocol.DecodeCommands(frame.Payload));
                            break;
                        case RemoteMessageKind.CommandResult:
                            if (!pending.IsCommand)
                                throw new InvalidDataException(
                                    "Catalog operation received a command response.");
                            HandleCommandResult(frame.RequestId, pending, frame.Payload);
                            break;
                        case RemoteMessageKind.CommandResultChunk:
                            if (!pending.IsCommand)
                                throw new InvalidDataException(
                                    "Catalog operation received a command chunk.");
                            HandleCommandResultChunk(pending, frame.Payload);
                            break;
                        case RemoteMessageKind.CommandResultEnd:
                            if (!pending.IsCommand)
                                throw new InvalidDataException(
                                    "Catalog operation received a command end frame.");
                            HandleCommandResultEnd(frame.RequestId, pending, frame.Payload);
                            break;
                        case RemoteMessageKind.Error:
                            RemoteError error = RemoteExecutionProtocol.DecodeError(frame.Payload);
                            var remoteException = new InvalidDataException(
                                $"[{error.Code}] {error.Message}");
                            RemovePending(frame.RequestId);
                            if (pending.IsCommand)
                                pending.CommandCompletion.TrySetException(remoteException);
                            else
                                pending.CatalogCompletion.TrySetException(remoteException);
                            break;
                        default:
                            throw new InvalidDataException(
                                $"Unexpected response kind: {frame.Kind}");
                    }
                }
                catch (Exception exception)
                {
                    RemovePending(frame.RequestId);
                    if (pending.IsCommand)
                        pending.CommandCompletion.TrySetException(exception);
                    else
                        pending.CatalogCompletion.TrySetException(exception);
                }
            }

            private void HandleCommandResult(Guid requestId,
                PendingOperation pending, byte[] payload)
            {
                if (!pending.IsCommand || pending.ResultStarted)
                    throw new InvalidDataException("Unexpected or duplicate command result metadata.");
                RemoteExecutionProtocol.DecodeCommandResult(payload, out bool succeeded,
                    out string code, out string message, out string contentType,
                    out long length, out byte[] hash);
                if ((succeeded || length > 0 || !string.IsNullOrEmpty(contentType)) &&
                    !ContentTypeMatches(pending.ExpectedContentType, contentType))
                    throw new InvalidDataException(
                        "Command result content type does not match the command.");
                pending.ResultStarted = true;
                pending.ResultSucceeded = succeeded;
                pending.ResultCode = code;
                pending.ResultMessage = message;
                pending.ResultContentType = contentType;
                if (length > 0)
                {
                    m_ResponseReceiver.Begin(length, hash);
                }
                else
                {
                    RemovePending(requestId);
                    pending.CommandCompletion.TrySetResult(new RemoteExecutionResult(
                        succeeded, code, message, Array.Empty<byte>(), contentType));
                }
            }

            private void HandleCommandResultChunk(PendingOperation pending, byte[] payload)
            {
                if (!pending.IsCommand || !pending.ResultStarted)
                    throw new InvalidDataException("Command result metadata is missing.");
                RemoteExecutionProtocol.DecodeCommandResultChunk(payload, out long offset,
                    out byte[] data);
                m_ResponseReceiver.Append(offset, data);
            }

            private void HandleCommandResultEnd(Guid requestId,
                PendingOperation pending, byte[] payload)
            {
                RemoteExecutionProtocol.DecodeCommandResultEnd(payload);
                if (!pending.IsCommand || !pending.ResultStarted)
                    throw new InvalidDataException("Command result metadata is missing.");
                byte[] result = m_ResponseReceiver.Complete();
                RemovePending(requestId);
                pending.CommandCompletion.TrySetResult(new RemoteExecutionResult(
                    pending.ResultSucceeded, pending.ResultCode, pending.ResultMessage,
                    result, pending.ResultContentType, true));
            }

            private void AddPending(Guid requestId, PendingOperation pending)
            {
                if (requestId == Guid.Empty)
                    throw new ArgumentException("Request ID is required.",
                        nameof(requestId));
                lock (m_PendingLock)
                {
                    if (m_Disposed) throw new ObjectDisposedException(nameof(ClientSession));
                    if (m_Pending.ContainsKey(requestId))
                        throw new InvalidOperationException("Duplicate remote request ID.");
                    m_Pending.Add(requestId, pending);
                }
            }

            private PendingOperation GetPending(Guid requestId)
            {
                lock (m_PendingLock)
                    return m_Pending.TryGetValue(requestId, out PendingOperation pending)
                        ? pending : null;
            }

            private void RemovePending(Guid requestId)
            {
                lock (m_ResponseLock)
                {
                    PendingOperation pending;
                    lock (m_PendingLock)
                    {
                        if (!m_Pending.TryGetValue(requestId, out pending)) return;
                        m_Pending.Remove(requestId);
                    }
                    if (pending.ResultStarted) m_ResponseReceiver.Reset();
                }
            }

            private static byte[] ComputeHash(byte[] bytes)
            {
                using (var sha = SHA256.Create()) return sha.ComputeHash(bytes);
            }

            private static bool ContentTypeMatches(string expected, string actual)
            {
                return string.IsNullOrEmpty(expected) ||
                    string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
            }

            private async Task<RemoteFrame> ReadInitialHelloAsync(
                CancellationToken cancellationToken)
            {
                Task<RemoteFrame> read = m_Channel.ReceiveAsync(cancellationToken);
                if (read == null)
                    throw new InvalidOperationException(
                        "The transport channel returned no receive task.");
                Task timeout = Task.Delay(m_HandshakeTimeout, CancellationToken.None);
                Task cancelled = Task.Delay(Timeout.Infinite, cancellationToken);
                Task completed = await Task.WhenAny(read, timeout, cancelled)
                    .ConfigureAwait(false);
                if (completed == read)
                {
                    RemoteFrame frame = await read.ConfigureAwait(false);
                    RemoteExecutionProtocol.ValidateFrame(frame);
                    return frame;
                }

                try { m_Channel.Abort(); }
                catch (Exception) { }
                try { await read.ConfigureAwait(false); }
                catch (Exception) { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Timed out waiting for the Player Hello.");
            }

            private async Task SendControlFrameAsync(RemoteFrame frame,
                CancellationToken cancellationToken)
            {
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (var operation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, deadline.Token))
                    await SendAsync(frame, operation.Token).ConfigureAwait(false);
            }

            private async Task SendAsync(RemoteFrame frame,
                CancellationToken cancellationToken)
            {
                RemoteExecutionProtocol.ValidateFrame(frame);
                await m_SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A cancelled write may leave half a frame on the wire.
                    using (cancellationToken.Register(() =>
                    {
                        try { m_Channel.Abort(); }
                        catch (Exception) { }
                    }))
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            Task send = m_Channel.SendAsync(frame, cancellationToken);
                            if (send == null)
                                throw new InvalidOperationException(
                                    "The transport channel returned no send task.");
                            await send.ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        catch
                        {
                            bool cancelled = cancellationToken.IsCancellationRequested;
                            Dispose();
                            if (cancelled) throw new OperationCanceledException(cancellationToken);
                            throw;
                        }
                    }
                }
                finally { m_SendLock.Release(); }
            }

            private void FailPending(Exception exception)
            {
                PendingOperation[] pending;
                lock (m_ResponseLock)
                {
                    lock (m_PendingLock)
                    {
                        pending = m_Pending.Values.ToArray();
                        m_Pending.Clear();
                    }
                    m_ResponseReceiver.Reset();
                }
                foreach (PendingOperation item in pending)
                {
                    if (item.IsCommand)
                        item.CommandCompletion.TrySetException(exception);
                    else
                        item.CatalogCompletion.TrySetException(exception);
                }
            }

            public void Dispose()
            {
                lock (m_StateLock)
                {
                    if (m_Disposed) return;
                    m_Disposed = true;
                    m_IsReady = false;
                    if (!m_Status.StartsWith("Error", StringComparison.Ordinal))
                        m_Status = "Disconnected";
                }
                m_Cancellation.Cancel();
                try { m_Channel.Abort(); }
                catch (Exception) { }
                try { m_Channel.Dispose(); }
                catch (Exception) { }
                FailPending(new IOException("Remote execution client disconnected."));
                lock (m_ResponseLock) m_ResponseReceiver.Dispose();
            }

            private sealed class PendingOperation
            {
                internal PendingOperation()
                {
                    CatalogCompletion = new TaskCompletionSource<RemoteCommandInfo[]>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    CommandCompletion = new TaskCompletionSource<RemoteExecutionResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    ExpectedContentType = string.Empty;
                }

                internal PendingOperation(string expectedContentType)
                    : this()
                {
                    IsCommand = true;
                    ExpectedContentType = expectedContentType ?? string.Empty;
                }

                internal bool IsCommand { get; }
                internal string ExpectedContentType { get; }
                internal TaskCompletionSource<RemoteCommandInfo[]> CatalogCompletion { get; }
                internal TaskCompletionSource<RemoteExecutionResult> CommandCompletion { get; }
                internal bool ResultSucceeded;
                internal string ResultCode;
                internal string ResultMessage;
                internal string ResultContentType;
                internal bool ResultStarted;
            }
        }
    }
}
