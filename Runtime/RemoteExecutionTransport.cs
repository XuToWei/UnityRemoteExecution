using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteExecution
{
    public interface IRemoteExecutionChannel : IDisposable
    {
        Task SendAsync(RemoteFrame frame, CancellationToken cancellationToken);
        Task<RemoteFrame> ReceiveAsync(CancellationToken cancellationToken);
        void Abort();
    }

    public interface IRemoteExecutionTransport : IDisposable
    {
        string Kind { get; }
        string ConfigurationKey { get; }
        string Description { get; }
        Task<IRemoteExecutionChannel> ConnectAsync(CancellationToken cancellationToken);
        void StartListening();
        Task<IRemoteExecutionChannel> AcceptAsync(CancellationToken cancellationToken);
    }

    public sealed class RemoteExecutionTcpTransport : IRemoteExecutionTransport
    {
        private readonly object m_Lock = new object();
        private readonly TransportRole m_Role;
        private readonly string m_Address;
        private readonly int m_ConfiguredPort;
        private TcpListener m_Listener;
        private TcpClient m_PendingClient;
        private bool m_Accepting;
        private bool m_Disposed;
        private string m_Description;
        private int m_Port;

        private RemoteExecutionTcpTransport(TransportRole role, string address, int port)
        {
            m_Role = role;
            m_Address = address;
            m_ConfiguredPort = port;
            m_Port = port;
            string authority = FormatAuthority(address, port);
            ConfigurationKey = role == TransportRole.Client
                ? $"tcp-client://{authority}"
                : $"tcp-server://{authority}";
            m_Description = $"TCP {authority}";
        }

        public static RemoteExecutionTcpTransport CreateClient(
            string host = "127.0.0.1", int port = 38421)
        {
            string validatedHost = RemoteExecutionTransportValidation.ValidateHost(
                host, nameof(host));
            int validatedPort = RemoteExecutionTransportValidation.ValidatePort(
                port, false, nameof(port));
            return new RemoteExecutionTcpTransport(TransportRole.Client,
                validatedHost, validatedPort);
        }

        public static int FindRandomAvailablePort(string bindAddress)
        {
            if (!IPAddress.TryParse(bindAddress, out IPAddress address))
                throw new InvalidOperationException("Invalid bind address.");
            byte[] randomBytes = new byte[2];
            using (var random = RandomNumberGenerator.Create())
            {
                for (int attempt = 0; attempt < 128; attempt++)
                {
                    random.GetBytes(randomBytes);
                    int port = 49152 +
                        ((randomBytes[0] | randomBytes[1] << 8) & 0x3FFF);
                    var listener = new TcpListener(address, port);
                    try
                    {
                        listener.Start();
                        return port;
                    }
                    catch (SocketException exception) when (
                        exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    {
                    }
                    finally { listener.Stop(); }
                }
            }
            throw new InvalidOperationException(
                "Could not find an available random port.");
        }

        public static RemoteExecutionTcpTransport CreateServer(
            string bindAddress = "127.0.0.1", int port = 38421)
        {
            if (!IPAddress.TryParse(bindAddress, out IPAddress address))
                throw new ArgumentException("Bind address must be an IP address.",
                    nameof(bindAddress));
            int validatedPort = RemoteExecutionTransportValidation.ValidatePort(
                port, true, nameof(port));
            return new RemoteExecutionTcpTransport(TransportRole.Server,
                address.ToString(), validatedPort);
        }

        public const string DefaultKind = "TCP";
        public string Kind => DefaultKind;
        public string ConfigurationKey { get; }

        public string Description
        {
            get { lock (m_Lock) return m_Description; }
        }

        public int Port
        {
            get { lock (m_Lock) return m_Port; }
        }

        public async Task<IRemoteExecutionChannel> ConnectAsync(
            CancellationToken cancellationToken)
        {
            EnsureRole(TransportRole.Client,
                "A server TCP transport cannot initiate a connection.");
            cancellationToken.ThrowIfCancellationRequested();
            TcpClient client;
            lock (m_Lock)
            {
                ThrowIfDisposed();
                if (m_PendingClient != null)
                    throw new InvalidOperationException(
                        "Only one pending connection is supported.");
                client = new TcpClient { NoDelay = true };
                m_PendingClient = client;
            }
            try
            {
                Task connect = client.ConnectAsync(m_Address, m_ConfiguredPort);
                using (cancellationToken.Register(client.Close))
                    await connect.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (m_Lock)
                {
                    ThrowIfDisposed();
                    if (!ReferenceEquals(m_PendingClient, client))
                        throw new OperationCanceledException(cancellationToken);
                    m_PendingClient = null;
                }
                return new RemoteExecutionStreamChannel(client.GetStream(), client);
            }
            catch
            {
                lock (m_Lock)
                {
                    if (ReferenceEquals(m_PendingClient, client))
                        m_PendingClient = null;
                }
                client.Close();
                throw;
            }
        }

        public void StartListening()
        {
            EnsureRole(TransportRole.Server,
                "A client TCP transport cannot start listening.");
            lock (m_Lock)
            {
                ThrowIfDisposed();
                if (m_Listener != null)
                    throw new InvalidOperationException(
                        "The TCP transport is already listening.");
                var listener = new TcpListener(IPAddress.Parse(m_Address),
                    m_ConfiguredPort);
                try { listener.Start(); }
                catch
                {
                    listener.Stop();
                    throw;
                }
                m_Listener = listener;
                IPEndPoint endpoint = (IPEndPoint)listener.LocalEndpoint;
                m_Port = endpoint.Port;
                m_Description = $"TCP {endpoint}";
            }
        }

        public async Task<IRemoteExecutionChannel> AcceptAsync(
            CancellationToken cancellationToken)
        {
            EnsureRole(TransportRole.Server,
                "A client TCP transport cannot accept connections.");
            cancellationToken.ThrowIfCancellationRequested();
            TcpListener listener;
            lock (m_Lock)
            {
                ThrowIfDisposed();
                if (m_Listener == null)
                    throw new InvalidOperationException(
                        "The TCP transport is not listening.");
                if (m_Accepting)
                    throw new InvalidOperationException(
                        "Only one pending accept is supported.");
                m_Accepting = true;
                listener = m_Listener;
            }
            TcpClient client = null;
            try
            {
                Task<TcpClient> accept = listener.AcceptTcpClientAsync();
                using (cancellationToken.Register(Dispose))
                    client = await accept.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                client.NoDelay = true;
                return new RemoteExecutionStreamChannel(client.GetStream(), client);
            }
            catch
            {
                client?.Close();
                throw;
            }
            finally
            {
                lock (m_Lock) m_Accepting = false;
            }
        }

        public void Dispose()
        {
            TcpListener listener;
            TcpClient pendingClient;
            lock (m_Lock)
            {
                if (m_Disposed) return;
                m_Disposed = true;
                listener = m_Listener;
                pendingClient = m_PendingClient;
                m_Listener = null;
                m_PendingClient = null;
            }
            try { listener?.Stop(); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            try { pendingClient?.Close(); }
            catch (ObjectDisposedException) { }
        }

        private void EnsureRole(TransportRole role, string message)
        {
            if (m_Role != role) throw new InvalidOperationException(message);
        }

        private void ThrowIfDisposed()
        {
            if (m_Disposed)
                throw new ObjectDisposedException(nameof(RemoteExecutionTcpTransport));
        }

        private static string FormatAuthority(string address, int port)
        {
            string normalized = address.Trim().ToLowerInvariant();
            if (IPAddress.TryParse(normalized, out IPAddress parsed))
            {
                normalized = parsed.ToString().ToLowerInvariant();
                if (parsed.AddressFamily == AddressFamily.InterNetworkV6)
                    normalized = $"[{normalized}]";
            }
            return $"{normalized}:{port}";
        }

        private enum TransportRole
        {
            Client,
            Server
        }
    }

    internal sealed class RemoteExecutionStreamChannel : IRemoteExecutionChannel
    {
        private readonly Stream m_Stream;
        private readonly IDisposable m_Owner;
        private readonly object m_Lock = new object();
        private bool m_Disposed;

        internal RemoteExecutionStreamChannel(Stream stream, IDisposable owner = null)
        {
            m_Stream = stream ?? throw new ArgumentNullException(nameof(stream));
            m_Owner = owner;
        }

        public Task SendAsync(RemoteFrame frame, CancellationToken cancellationToken)
        {
            lock (m_Lock)
            {
                if (m_Disposed)
                    throw new ObjectDisposedException(nameof(RemoteExecutionStreamChannel));
            }
            return RemoteExecutionProtocol.WriteFrameAsync(m_Stream, frame,
                cancellationToken);
        }

        public Task<RemoteFrame> ReceiveAsync(CancellationToken cancellationToken)
        {
            lock (m_Lock)
            {
                if (m_Disposed)
                    throw new ObjectDisposedException(nameof(RemoteExecutionStreamChannel));
            }
            return RemoteExecutionProtocol.ReadFrameAsync(m_Stream, cancellationToken);
        }

        public void Abort()
        {
            lock (m_Lock)
            {
                if (m_Disposed) return;
                try { m_Stream.Close(); }
                catch (ObjectDisposedException) { }
                try { m_Owner?.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            lock (m_Lock)
            {
                if (m_Disposed) return;
                m_Disposed = true;
                try { m_Stream.Dispose(); }
                finally { m_Owner?.Dispose(); }
            }
        }
    }

    internal static class RemoteExecutionTransportValidation
    {
        internal static string ValidateHost(string host, string parameterName)
        {
            string value = (host ?? string.Empty).Trim();
            if (value.Length == 0)
                throw new ArgumentException("Host is required.", parameterName);
            if (value == "*" || IsWildcardAddress(value))
                throw new ArgumentException(
                    "A wildcard address cannot be used as a destination.", parameterName);
            return value;
        }

        internal static int ValidatePort(int port, bool allowZero,
            string parameterName)
        {
            int minimum = allowZero ? 0 : 1;
            if (port < minimum || port > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(parameterName,
                    $"Port must be in range {minimum}..65535.");
            return port;
        }

        private static bool IsWildcardAddress(string host)
        {
            if (!IPAddress.TryParse(host.Trim('[', ']'), out IPAddress address))
                return false;
            return address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
        }
    }
}
