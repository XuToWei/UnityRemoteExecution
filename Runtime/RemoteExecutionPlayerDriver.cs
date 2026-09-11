using System;
using System.Collections.Generic;
using UnityEngine;

namespace RemoteExecution
{
    [AddComponentMenu("")]
    internal sealed class RemoteExecutionPlayerDriver : MonoBehaviour
    {
        private readonly Queue<QueuedAction> m_MainThreadActions = new Queue<QueuedAction>();
        private readonly object m_ActionLock = new object();
        private RemoteExecutionPlayerCommandHost m_CommandHost;
        private RemoteExecutionPlayerConnection m_Connection;
        private RemoteExecutionPlayerConfiguration m_Configuration;
        private long m_Generation;
        private bool m_Destroying;

        internal long Generation => m_Generation;

        internal void Initialize()
        {
            DontDestroyOnLoad(gameObject);
            m_CommandHost = new RemoteExecutionPlayerCommandHost();
        }

        internal void StartConnection(RemoteExecutionPlayerConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (m_Destroying)
            {
                DisposeTransport(configuration.Transport);
                return;
            }
            RemoteExecutionConnectionState state = RemoteExecutionPlayerApi.ConnectionState;
            bool isActive = state == RemoteExecutionConnectionState.Connecting ||
                state == RemoteExecutionConnectionState.Handshaking ||
                state == RemoteExecutionConnectionState.Connected;
            RemoteExecutionPlayerConfiguration previousConfiguration = m_Configuration;
            bool sameConfiguration = previousConfiguration != null &&
                previousConfiguration.Equals(configuration);
            bool sameTransport = previousConfiguration != null &&
                ReferenceEquals(previousConfiguration.Transport, configuration.Transport);
            if (sameConfiguration && isActive)
            {
                if (!sameTransport) DisposeTransport(configuration.Transport);
                return;
            }
            if (sameTransport && !sameConfiguration)
                throw new InvalidOperationException(
                    "Changed Player settings require a new transport instance.");

            long previousGeneration = m_Generation;
            RetireConnection();
            m_CommandHost.CancelConnection(previousGeneration);
            if (previousConfiguration != null &&
                !ReferenceEquals(previousConfiguration.Transport, configuration.Transport))
                DisposeTransport(previousConfiguration.Transport);
            long generation = ++m_Generation;
            m_Configuration = configuration;
            try
            {
                m_CommandHost.Initialize();
            }
            catch (Exception exception)
            {
                RemoteExecutionPlayerApi.SetState(this, generation,
                    RemoteExecutionConnectionState.Faulted,
                    new RemoteExecutionConnectionError("COMMAND_DISCOVERY_FAILED",
                        exception.Message));
                return;
            }

            var connection = new RemoteExecutionPlayerConnection(this, generation,
                configuration);
            m_Connection = connection;
            m_CommandHost.BeginConnection(generation, configuration, connection.Send);
            RemoteExecutionPlayerApi.SetState(this, generation,
                RemoteExecutionConnectionState.Connecting, null);
            if (m_Generation == generation && ReferenceEquals(m_Connection, connection))
                connection.Start();
        }

        internal void StopConnection()
        {
            RemoteExecutionPlayerConfiguration configuration = m_Configuration;
            long previousGeneration = m_Generation;
            RetireConnection();
            m_CommandHost?.CancelConnection(previousGeneration);
            long generation = ++m_Generation;
            m_Configuration = null;
            DisposeTransport(configuration?.Transport);
            ClearMainThreadActions();
            RemoteExecutionPlayerApi.SetState(this, generation,
                RemoteExecutionConnectionState.Disconnected, null);
        }

        internal void PostHandshaking(long generation)
        {
            Enqueue(generation, () => RemoteExecutionPlayerApi.SetState(this, generation,
                RemoteExecutionConnectionState.Handshaking, null));
        }

        internal void PostConnected(long generation)
        {
            Enqueue(generation, () => RemoteExecutionPlayerApi.SetState(this, generation,
                RemoteExecutionConnectionState.Connected, null));
        }

        internal void PostFrame(long generation, RemoteFrame frame)
        {
            Enqueue(generation, () => m_CommandHost.HandleFrame(generation, frame));
        }

        internal void PostFault(long generation, RemoteExecutionPlayerConnection connection,
            RemoteExecutionConnectionError error)
        {
            Enqueue(generation, () =>
            {
                if (!ReferenceEquals(m_Connection, connection)) return;
                m_Connection = null;
                m_CommandHost.CancelConnection(generation);
                RemoteExecutionPlayerApi.SetState(this, generation,
                    RemoteExecutionConnectionState.Faulted, error);
            });
        }

        private void Update()
        {
            while (true)
            {
                QueuedAction queued;
                lock (m_ActionLock)
                {
                    if (m_MainThreadActions.Count == 0) break;
                    queued = m_MainThreadActions.Dequeue();
                }
                if (queued.Generation != m_Generation) continue;
                try { queued.Action(); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }

        private void OnApplicationQuit()
        {
            m_Destroying = true;
            StopConnection();
        }

        private void OnDestroy()
        {
            m_Destroying = true;
            long previousGeneration = m_Generation;
            RetireConnection();
            ++m_Generation;
            ClearMainThreadActions();
            m_CommandHost?.CancelConnection(previousGeneration);
            m_CommandHost?.Dispose();
            m_CommandHost = null;
            DisposeTransport(m_Configuration?.Transport);
            m_Configuration = null;
            RemoteExecutionPlayerApi.DriverDestroyed(this);
        }

        private void Enqueue(long generation, Action action)
        {
            if (action == null || m_Destroying) return;
            lock (m_ActionLock)
                m_MainThreadActions.Enqueue(new QueuedAction(generation, action));
        }

        private void ClearMainThreadActions()
        {
            lock (m_ActionLock) m_MainThreadActions.Clear();
        }

        private void RetireConnection()
        {
            RemoteExecutionPlayerConnection connection = m_Connection;
            m_Connection = null;
            connection?.Stop();
        }

        private static void DisposeTransport(IRemoteExecutionTransport transport)
        {
            if (transport == null) return;
            try { transport.Dispose(); }
            catch (Exception) { }
        }

        private struct QueuedAction
        {
            internal QueuedAction(long generation, Action action)
            {
                Generation = generation;
                Action = action;
            }

            internal long Generation { get; }
            internal Action Action { get; }
        }
    }
}
