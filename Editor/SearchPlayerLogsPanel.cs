using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace RemoteExecution
{
    internal sealed class SearchPlayerLogsPanel : IRemoteExecutionEditorPanel
    {
        private static readonly UTF8Encoding s_Utf8 = new UTF8Encoding(false, true);
        private string m_Query = string.Empty;

        public string Id => "remote-execution.logs";
        public string DisplayName => "日志";
        public int Order => 10;

        public bool IsAvailable(RemoteExecutionEditorContext context,
            out string unavailableReason)
        {
            unavailableReason = string.Empty;
            return true;
        }

        public void DrawGUI(RemoteExecutionEditorContext context)
        {
            EditorGUILayout.LabelField("搜索 Player 日志", EditorStyles.boldLabel);
            m_Query = EditorGUILayout.TextField("关键字", m_Query);
            RemoteExecutionClientInfo player = context.SelectedPlayer;
            string capabilityProblem = GetCapabilityProblem(player);
            using (new EditorGUI.DisabledScope(context.IsOperationRunning ||
                capabilityProblem != null))
            {
                if (GUILayout.Button("搜索")) StartSearch(context, player);
            }
            if (capabilityProblem != null)
                EditorGUILayout.HelpBox(capabilityProblem, MessageType.Info);
        }

        private void StartSearch(RemoteExecutionEditorContext context,
            RemoteExecutionClientInfo player)
        {
            int sessionId = player.Id;
            string query = m_Query;
            context.TryStartOperation("正在搜索 Player 日志...",
                token => SearchAsync(sessionId, query, token));
        }

        private static async Task<string> SearchAsync(int sessionId, string query,
            CancellationToken cancellationToken)
        {
            byte[] request = s_Utf8.GetBytes(query ?? string.Empty);
            RemoteExecutionResult result = await RemoteExecutionEditorApi
                .ExecuteCommandAsync<SearchPlayerLogsCommand>(sessionId, request,
                    cancellationToken);
            if (!result.Succeeded)
                throw new InvalidOperationException($"[{result.Code}] {result.Message}");
            return s_Utf8.GetString(result.Payload);
        }

        private static string GetCapabilityProblem(RemoteExecutionClientInfo player)
        {
            if (player == null) return "请选择已连接的 Player。";
            if (!player.IsReady) return "所选 Player 尚未就绪。";
            RemoteCommandSnapshot command = player.Commands.FirstOrDefault(item =>
                string.Equals(item.TypeName, typeof(SearchPlayerLogsCommand).FullName,
                    StringComparison.Ordinal));
            if (command == null) return "Player 不包含日志搜索命令。";
            if (!command.Executable) return "Player 日志搜索命令不可用。";
            if (!string.Equals(command.RequestContentType,
                    SearchPlayerLogsCommand.ContentType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(command.ResponseContentType,
                    SearchPlayerLogsCommand.ContentType, StringComparison.OrdinalIgnoreCase))
                return "Player 日志搜索命令的内容类型不兼容。";
            return null;
        }
    }
}
