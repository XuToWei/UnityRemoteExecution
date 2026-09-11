using System;
using System.IO;
using System.Security.Cryptography;

namespace RemoteExecution
{
    internal sealed class RemotePayloadReceiver : IDisposable
    {
        private readonly int m_MaxPayloadBytes;
        private readonly int m_MaxRetainedBytes;
        private MemoryStream m_Buffer = new MemoryStream();
        private long m_ExpectedLength;
        private byte[] m_ExpectedHash;
        private bool m_Receiving;

        internal RemotePayloadReceiver(int maxPayloadBytes, int maxRetainedBytes)
        {
            if (maxPayloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
            if (maxRetainedBytes < 0 || maxRetainedBytes > maxPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(maxRetainedBytes));
            m_MaxPayloadBytes = maxPayloadBytes;
            m_MaxRetainedBytes = maxRetainedBytes;
        }

        internal void Begin(long length, byte[] hash)
        {
            if (m_Receiving) throw new InvalidDataException("Another payload is being received.");
            if (length < 0 || length > m_MaxPayloadBytes || hash == null ||
                (hash.Length != 32 && (length != 0 || hash.Length != 0)))
                throw new InvalidDataException("Invalid payload length or hash.");
            Reset();
            if (m_Buffer.Capacity < length) m_Buffer.Capacity = checked((int)length);
            m_ExpectedLength = length;
            m_ExpectedHash = (byte[])hash.Clone();
            m_Receiving = true;
        }

        internal void Append(long offset, byte[] data)
        {
            if (!m_Receiving) throw new InvalidDataException("Payload metadata is missing.");
            if (data == null || offset != m_Buffer.Position ||
                m_Buffer.Length + data.Length > m_ExpectedLength)
                throw new InvalidDataException("Payload chunk is out of order or too large.");
            m_Buffer.Write(data, 0, data.Length);
        }

        internal byte[] Complete()
        {
            try
            {
                if (!m_Receiving || m_Buffer.Length != m_ExpectedLength)
                    throw new InvalidDataException("Payload length does not match metadata.");
                byte[] payload = m_Buffer.ToArray();
                if (m_ExpectedHash.Length != 0)
                {
                    using (var sha = SHA256.Create())
                        if (!RemoteExecutionProtocol.FixedTimeEquals(
                            sha.ComputeHash(payload), m_ExpectedHash))
                            throw new InvalidDataException("Payload hash does not match metadata.");
                }
                return payload;
            }
            finally { Reset(); }
        }

        internal void Reset()
        {
            if (m_Buffer == null) throw new ObjectDisposedException(nameof(RemotePayloadReceiver));
            m_Receiving = false;
            m_ExpectedLength = 0;
            m_ExpectedHash = null;
            if (m_Buffer.Capacity > m_MaxRetainedBytes)
            {
                m_Buffer.Dispose();
                m_Buffer = new MemoryStream();
            }
            else
            {
                m_Buffer.SetLength(0);
                m_Buffer.Position = 0;
            }
        }

        public void Dispose()
        {
            m_Buffer?.Dispose();
            m_Buffer = null;
            m_ExpectedHash = null;
            m_Receiving = false;
        }
    }
}
