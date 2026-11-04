using System.ComponentModel;
using io.github.hatayama.UnityCliLoop.ToolContracts;

namespace RemoteExecution.UnityCLI
{
    public enum RemoteExecutionAction
    {
        status, start, stop, players, commands, execute, search_logs
    }

    public sealed class RemoteExecutionSchema : UnityCliLoopToolSchema
    {
        [Description("Listener status/start/stop, players, commands, execute, or search_logs.")]
        public RemoteExecutionAction Action { get; set; } = RemoteExecutionAction.status;

        [Description("TCP bind address for start. Use a reachable address for remote devices.")]
        public string BindAddress { get; set; } = "127.0.0.1";

        [Description("TCP port for start (0 chooses an available port).")]
        public int Port { get; set; } = 38421;

        [Description("Maximum connected Players, between 1 and 1024.")]
        public int MaxClients { get; set; } = 4;

        [Description("Positive numeric sessionId returned by players; not clientId.")]
        public int SessionId { get; set; }

        [Description("Exact executable typeName returned by commands.")]
        public string CommandType { get; set; }

        [Description("UTF-8 request for execute; mutually exclusive with PayloadBase64.")]
        public string PayloadText { get; set; }

        [Description("Base64 binary request for execute; mutually exclusive with PayloadText.")]
        public string PayloadBase64 { get; set; }

        [Description("Player log query for search_logs; at most 4096 UTF-8 bytes.")]
        public string Query { get; set; } = string.Empty;
    }
}
