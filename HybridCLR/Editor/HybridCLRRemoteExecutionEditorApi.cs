using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteExecution.HybridCLR
{
    /// <summary>Compiles and executes Player source using the window's shared compiler lock.</summary>
    public static class HybridCLRRemoteExecutionEditorApi
    {
        private static readonly SemaphoreSlim s_BuildLock = new SemaphoreSlim(1, 1);

        internal static string GetCapabilityProblem(RemoteExecutionClientInfo player)
        {
            if (player == null) return "Select a connected Player.";
            if (!player.IsReady) return "The selected Player is not ready.";
            RemoteCommandSnapshot applyCommand = player.Commands.FirstOrDefault(command =>
                string.Equals(command.TypeName, HybridCLRBundleCodec.TypeName,
                    StringComparison.Ordinal));
            if (applyCommand == null)
                return "Refresh the command catalog or use a Player containing the HybridCLR command.";
            if (!applyCommand.Executable) return "The Player HybridCLR command is unavailable.";
            if (!string.Equals(applyCommand.RequestContentType,
                HybridCLRBundleCodec.ContentType, StringComparison.OrdinalIgnoreCase))
                return "The Player HybridCLR command uses an incompatible content type.";
            return null;
        }

        public static async Task<RemoteExecutionResult> ExecuteSourceAsync(int sessionId,
            string source, CancellationToken cancellationToken = default)
        {
            if (!global::HybridCLR.Settings.HybridCLRSettings.instance.enable)
                throw new InvalidOperationException("HybridCLR is disabled in project settings.");
            await RemoteExecutionEditorApi.RefreshCommandsAsync(sessionId, cancellationToken);
            RemoteExecutionClientInfo client = FindReadyClient(sessionId);
            string capabilityProblem = GetCapabilityProblem(client);
            if (capabilityProblem != null) throw new InvalidOperationException(capabilityProblem);

            await s_BuildLock.WaitAsync(cancellationToken);
            HybridCLRRemoteBuildOutput output;
            try
            {
                output = await HybridCLRRemoteExecutionCompiler.BuildAsync(
                    new HybridCLRRemoteBuildRequest(client.Target, source), cancellationToken);
            }
            finally { s_BuildLock.Release(); }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] envelope = HybridCLRBundleCodec.Encode(new HybridCLRBundle(
                Guid.NewGuid(), output.Target, output.Artifacts));
            if (envelope.Length > RemoteExecutionProtocol.MaxCommandRequestBytes)
                throw new InvalidOperationException(
                    $"HybridCLR bundle exceeds the global Player limit of {RemoteExecutionProtocol.MaxCommandRequestBytes} bytes.");
            RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync<
                HybridCLRRemoteExecutionCommand>(
                sessionId, envelope, cancellationToken);
            return result;
        }

        private static RemoteExecutionClientInfo FindReadyClient(int sessionId)
        {
            return RemoteExecutionEditorApi.GetClients().FirstOrDefault(client =>
                       client.Id == sessionId && client.IsReady)
                   ?? throw new InvalidOperationException("Player is no longer connected.");
        }
    }
}
