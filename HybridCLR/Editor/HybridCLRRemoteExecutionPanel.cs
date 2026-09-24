using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace RemoteExecution.HybridCLR
{
    internal sealed class HybridCLRRemoteExecutionPanel : IRemoteExecutionEditorPanel
    {
        private const string DefaultSource = @"using System.Threading;
using System.Threading.Tasks;
using RemoteExecution.HybridCLR;
using UnityEngine;

public sealed class RemoteExecutionEntry : IHybridCLRRemoteExecutionEntry
{
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Debug.Log(""Remote execution entry completed."");
        return Task.CompletedTask;
    }
}";
        private Vector2 m_SourceScroll;
        private string m_Source = DefaultSource;
        public string Id => "remote-execution.hybridclr";
        public string DisplayName => "HybridCLR";
        public int Order => 100;

        public bool IsAvailable(RemoteExecutionEditorContext context,
            out string unavailableReason)
        {
            bool enabled = global::HybridCLR.Editor.Settings.HybridCLRSettings.Instance.enable;
            unavailableReason = enabled ? string.Empty : "HybridCLR is disabled in project settings.";
            return enabled;
        }

        public void DrawGUI(RemoteExecutionEditorContext context)
        {
            DrawSource();
            RemoteExecutionClientInfo player = context.SelectedPlayer;
            string capabilityProblem = GetCapabilityProblem(player);
            using (new EditorGUI.DisabledScope(context.IsOperationRunning ||
                capabilityProblem != null))
            {
                if (GUILayout.Button("Compile, Apply & Execute"))
                    StartOperation(context, player);
            }
            if (capabilityProblem != null)
                EditorGUILayout.HelpBox(capabilityProblem, MessageType.Info);
            EditorGUILayout.HelpBox(
                "Source must contain exactly one public IHybridCLRRemoteExecutionEntry implementation with a public parameterless constructor. Each execution loads a uniquely named assembly that remains loaded; a partial load requires restarting the Player.",
                MessageType.Warning);
        }

        private void DrawSource()
        {
            EditorGUILayout.LabelField("Source Code", EditorStyles.boldLabel);
            m_SourceScroll = EditorGUILayout.BeginScrollView(m_SourceScroll,
                GUILayout.MinHeight(180), GUILayout.MaxHeight(360));
            m_Source = EditorGUILayout.TextArea(m_Source, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private static string GetCapabilityProblem(RemoteExecutionClientInfo player) =>
            HybridCLRRemoteExecutionEditorApi.GetCapabilityProblem(player);

        private void StartOperation(RemoteExecutionEditorContext context,
            RemoteExecutionClientInfo player)
        {
            int sessionId = player.Id;
            var request = new HybridCLRRemoteBuildRequest(player.Target, m_Source);
            context.TryStartOperation("Compiling, applying and executing...",
                token => RunAsync(sessionId, request, token));
        }

        private static async Task<string> RunAsync(int sessionId,
            HybridCLRRemoteBuildRequest request, CancellationToken cancellationToken)
        {
            RemoteExecutionResult result = await HybridCLRRemoteExecutionEditorApi.ExecuteSourceAsync(
                sessionId, request.Source, cancellationToken);
            if (!result.Succeeded)
                throw new InvalidOperationException(
                    $"Player apply or execution failed [{result.Code}]: {result.Message}");
            return string.IsNullOrWhiteSpace(result.Message)
                ? "Compile, apply and execution completed." : result.Message;
        }
    }
}
