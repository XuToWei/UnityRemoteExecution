using System.Threading.Tasks;
using AgentBridge;
using Newtonsoft.Json.Linq;
using RemoteExecution.HybridCLR;

namespace RemoteExecution.AgentBridge
{
    public sealed class RemoteExecutionCSharpHandler : ICommandHandler
    {
        public string Command => "remote_execution_csharp";
        public string Description => "Compile C# and execute it on a connected Player through HybridCLR. " +
            "Use sessionId from remote_execution players. Source must contain exactly one public " +
            "IHybridCLRRemoteExecutionEntry with a public parameterless constructor and " +
            "Task ExecuteAsync(CancellationToken). References must already exist on the Player. " +
            "Use Debug.Log for findings, then remote_execution search_logs to read them. " +
            "Each call loads a new assembly until Player exit; partial load failure requires restart. " +
            "Allow time for compilation (up to 120s) plus the advertised Player command timeout. " +
            "Check result.succeeded/code/message.";
        public string Group => "Remote Execution";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;
        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""required"": [""sessionId"", ""source""],
  ""properties"": {
    ""sessionId"": { ""type"": ""integer"", ""minimum"": 1 },
    ""source"": { ""type"": ""string"", ""description"": ""Full C# source implementing RemoteExecution.HybridCLR.IHybridCLRRemoteExecutionEntry; at most 512 KiB UTF-8."" }
  }
}");

        public async Task<object> ExecuteAsync(JObject args)
        {
            int sessionId = RemoteExecutionHandler.RequireSessionId(args);
            RemoteExecutionHandler.RequireReadyPlayer(sessionId);
            string source = args.Value<string>("source");
            if (string.IsNullOrWhiteSpace(source))
                throw new CommandException("INVALID_PARAMS", "source is required.");
            RemoteExecutionResult result = await HybridCLRRemoteExecutionEditorApi.ExecuteSourceAsync(sessionId, source);
            return RemoteExecutionHandler.DescribeResult(sessionId, result);
        }
    }
}
