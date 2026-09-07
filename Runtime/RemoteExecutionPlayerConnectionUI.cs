using System;
using UnityEngine;

namespace RemoteExecution
{
    public sealed class RemoteExecutionPlayerConnectionUI
    {
        private string m_EditorHost = "127.0.0.1";
        private int m_EditorPort = 38421;
        private string m_ClientId;
        private IRemoteExecutionPlayerOptionsProvider m_OptionsProvider;
        private bool m_ShowUI = true;
        private Rect m_Area = new Rect(20f, 20f, 360f, 240f);
        private string m_LocalError;

        public string EditorHost
        {
            get => m_EditorHost;
            set => m_EditorHost = value;
        }

        public int EditorPort
        {
            get => m_EditorPort;
            set => m_EditorPort = value;
        }

        public string ClientId
        {
            get => m_ClientId;
            set => m_ClientId = value;
        }

        public IRemoteExecutionPlayerOptionsProvider OptionsProvider
        {
            get => m_OptionsProvider;
            set => m_OptionsProvider = value;
        }

        public bool ShowUI
        {
            get => m_ShowUI;
            set => m_ShowUI = value;
        }

        public Rect Area
        {
            get => m_Area;
            set => m_Area = value;
        }

        public void Connect()
        {
            m_LocalError = null;
            try
            {
                RemoteExecutionPlayerOptions options = CreateOptions();
                if (options == null)
                    throw new InvalidOperationException("Options provider returned no options.");
                RemoteExecutionPlayerApi.Start(options);
            }
            catch (Exception exception)
            {
                m_LocalError = exception.GetBaseException().Message;
            }
        }

        public void Connect(RemoteExecutionPlayerOptions options)
        {
            m_LocalError = null;
            try
            {
                if (options == null)
                    throw new ArgumentNullException(nameof(options));
                RemoteExecutionPlayerApi.Start(options);
            }
            catch (Exception exception)
            {
                m_LocalError = exception.GetBaseException().Message;
            }
        }

        public void Disconnect()
        {
            m_LocalError = null;
            try { RemoteExecutionPlayerApi.Stop(); }
            catch (Exception exception)
            {
                m_LocalError = exception.GetBaseException().Message;
            }
        }

        public void OnGUI()
        {
            if (!m_ShowUI) return;
            RemoteExecutionConnectionState state = RemoteExecutionPlayerApi.ConnectionState;
            GUILayout.BeginArea(m_Area, "Remote Execution", GUI.skin.window);
            try
            {
                GUILayout.Label($"State: {state}");
                if (state == RemoteExecutionConnectionState.Disconnected ||
                    state == RemoteExecutionConnectionState.Faulted)
                {
                    if (m_OptionsProvider == null)
                    {
                        GUILayout.Label("Editor Host");
                        m_EditorHost = GUILayout.TextField(m_EditorHost ?? string.Empty);
                        GUILayout.Label("Editor Port");
                        string port = GUILayout.TextField(m_EditorPort.ToString());
                        if (int.TryParse(port, out int parsedPort)) m_EditorPort = parsedPort;
                        GUILayout.Label("Client ID");
                        m_ClientId = GUILayout.TextField(m_ClientId ?? string.Empty);
                    }
                    if (GUILayout.Button(state == RemoteExecutionConnectionState.Faulted
                        ? "Retry" : "Connect")) Connect();
                }
                else if (GUILayout.Button(state == RemoteExecutionConnectionState.Connected
                    ? "Disconnect" : "Stop")) Disconnect();

                RemoteExecutionConnectionError error = RemoteExecutionPlayerApi.LastError;
                if (state == RemoteExecutionConnectionState.Faulted && error != null)
                    GUILayout.Label($"[{error.Code}] {error.Message}");
                if (!string.IsNullOrEmpty(m_LocalError))
                    GUILayout.Label(m_LocalError);
            }
            finally { GUILayout.EndArea(); }
        }

        private RemoteExecutionPlayerOptions CreateOptions()
        {
            if (m_OptionsProvider != null) return m_OptionsProvider.CreateOptions();
            return new RemoteExecutionPlayerOptions(
                RemoteExecutionTcpTransport.CreateClient(m_EditorHost, m_EditorPort), m_ClientId);
        }
    }
}
