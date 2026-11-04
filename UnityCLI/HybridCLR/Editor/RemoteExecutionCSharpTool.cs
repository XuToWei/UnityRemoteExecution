using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using io.github.hatayama.UnityCliLoop.ToolContracts;
using RemoteExecution.HybridCLR;

namespace RemoteExecution.UnityCLI
{
    public sealed class RemoteExecutionCSharpSchema : UnityCliLoopToolSchema
    {
        [Description("Positive numeric sessionId returned by remote-execution --action players.")]
        public int SessionId { get; set; }

        [Description("Full C# source implementing IHybridCLRRemoteExecutionEntry; at most 512 KiB UTF-8.")]
        public string Source { get; set; }
    }

    [UnityCliLoopTool]
    public sealed class RemoteExecutionCSharpTool : UnityCliLoopTool<RemoteExecutionCSharpSchema, RemoteExecutionResponse>
    {
        public override string ToolName => "remote-execution-csharp";

        protected override async Task<RemoteExecutionResponse> ExecuteAsync(
            RemoteExecutionCSharpSchema parameters, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                RemoteExecutionTool.RequireReadyPlayer(parameters.SessionId);
                if (string.IsNullOrWhiteSpace(parameters.Source) ||
                    Encoding.UTF8.GetByteCount(parameters.Source) > 512 * 1024)
                    return RemoteExecutionResponse.Failure("INVALID_PARAMS", "source is required and must not exceed 512 KiB UTF-8.");
                // Enter on Unity's main thread: the shared compiler uses Unity Editor APIs.
                RemoteExecutionResult result = await HybridCLRRemoteExecutionEditorApi.ExecuteSourceAsync(
                    parameters.SessionId, parameters.Source, ct).ConfigureAwait(false);
                return RemoteExecutionResponse.FromResult(parameters.SessionId, result);
            }
            catch (RemoteExecutionCliException exception)
            {
                return RemoteExecutionResponse.Failure(exception.Code, exception.Message);
            }
        }
    }
}
