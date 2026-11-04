using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using io.github.hatayama.UnityCliLoop.ToolContracts;
using Newtonsoft.Json.Linq;

namespace RemoteExecution.UnityCLI
{
    /// <summary>Shares the Editor listener and Player sessions without an AgentBridge dependency.</summary>
    [UnityCliLoopTool]
    public sealed class RemoteExecutionTool : UnityCliLoopTool<RemoteExecutionSchema, RemoteExecutionResponse>
    {
        public override string ToolName => "remote-execution";

        protected override async Task<RemoteExecutionResponse> ExecuteAsync(
            RemoteExecutionSchema parameters, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await ExecuteCoreAsync(parameters, ct).ConfigureAwait(false);
            }
            catch (RemoteExecutionCliException exception)
            {
                return RemoteExecutionResponse.Failure(exception.Code, exception.Message);
            }
            // Cancellation must reach the CLI host so its timeout/cancellation policy applies.
        }

        private static async Task<RemoteExecutionResponse> ExecuteCoreAsync(
            RemoteExecutionSchema args, CancellationToken ct)
        {
            switch (args.Action)
            {
                case RemoteExecutionAction.status: return Status();
                case RemoteExecutionAction.players:
                    return Describe(new { players = RemoteExecutionEditorApi.GetClients().Select(DescribePlayer).ToArray() });
                case RemoteExecutionAction.start:
                    if (RemoteExecutionEditorApi.IsServerRunning)
                        throw new RemoteExecutionCliException("SERVER_ALREADY_RUNNING",
                            "Stop the active listener before starting another. Use status to inspect it.");
                    if (!IPAddress.TryParse(args.BindAddress, out _) || args.Port < 0 || args.Port > 65535 ||
                        args.MaxClients < 1 || args.MaxClients > 1024)
                        throw new RemoteExecutionCliException("INVALID_PARAMS",
                            "start requires an IP bindAddress, port between 0 and 65535, and maxClients between 1 and 1024.");
                    RemoteExecutionEditorApi.StartServer(args.BindAddress, args.Port, args.MaxClients);
                    return Status();
                case RemoteExecutionAction.stop:
                    RemoteExecutionEditorApi.StopServer();
                    return Status();
                case RemoteExecutionAction.commands:
                case RemoteExecutionAction.execute:
                case RemoteExecutionAction.search_logs:
                    break;
                default:
                    throw new RemoteExecutionCliException("INVALID_PARAMS", "Unknown action.");
            }

            RequireReadyPlayer(args.SessionId);
            string commandType = args.Action == RemoteExecutionAction.search_logs
                ? typeof(SearchPlayerLogsCommand).FullName : args.CommandType;
            byte[] payload = ReadPayload(args);
            if (args.Action == RemoteExecutionAction.execute && string.IsNullOrWhiteSpace(commandType))
                throw new RemoteExecutionCliException("INVALID_PARAMS", "execute requires commandType from commands.");

            await RemoteExecutionEditorApi.RefreshCommandsAsync(args.SessionId, ct).ConfigureAwait(false);
            RemoteExecutionClientInfo player = RequireReadyPlayer(args.SessionId);
            if (args.Action == RemoteExecutionAction.commands)
                return Describe(new { sessionId = args.SessionId, commands = player.Commands.Select(command => new
                {
                    typeName = command.TypeName, name = command.Name, description = command.Description,
                    category = command.Category, timeoutSeconds = command.TimeoutSeconds,
                    requestContentType = command.RequestContentType,
                    responseContentType = command.ResponseContentType, executable = command.Executable
                }).ToArray() });

            RemoteCommandSnapshot selected = player.Commands.FirstOrDefault(command => command.TypeName == commandType);
            if (selected == null || !selected.Executable)
                throw new RemoteExecutionCliException("PLAYER_COMMAND_UNAVAILABLE",
                    "Player does not advertise an executable command: " + commandType);
            RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync(
                args.SessionId, commandType, payload, selected.RequestContentType, ct).ConfigureAwait(false);
            return RemoteExecutionResponse.FromResult(args.SessionId, result);
        }

        public static RemoteExecutionClientInfo RequireReadyPlayer(int sessionId)
        {
            if (sessionId <= 0)
                throw new RemoteExecutionCliException("INVALID_PARAMS", "A positive sessionId from players is required.");
            RemoteExecutionClientInfo player = RemoteExecutionEditorApi.GetClients().FirstOrDefault(p => p.Id == sessionId);
            if (player == null)
                throw new RemoteExecutionCliException("PLAYER_NOT_CONNECTED", "Player session is no longer connected. Query players again.");
            if (!player.IsReady)
                throw new RemoteExecutionCliException("PLAYER_NOT_READY", "Player handshake is not complete.");
            return player;
        }

        private static byte[] ReadPayload(RemoteExecutionSchema args)
        {
            if (args.Action == RemoteExecutionAction.commands) return Array.Empty<byte>();
            if (args.Action == RemoteExecutionAction.search_logs)
            {
                string query = args.Query ?? string.Empty;
                if (Encoding.UTF8.GetByteCount(query) > SearchPlayerLogsCommand.MaxQueryBytes)
                    throw new RemoteExecutionCliException("INVALID_PARAMS", "Log query exceeds 4096 UTF-8 bytes.");
                return Encoding.UTF8.GetBytes(query);
            }
            string text = args.PayloadText;
            string base64 = args.PayloadBase64;
            if (text != null && base64 != null)
                throw new RemoteExecutionCliException("INVALID_PARAMS", "Provide only payloadText or payloadBase64.");
            if ((text != null && Encoding.UTF8.GetByteCount(text) > RemoteExecutionProtocol.MaxCommandRequestBytes) ||
                (base64 != null && base64.Length > ((long)RemoteExecutionProtocol.MaxCommandRequestBytes + 2) / 3 * 4))
                throw new RemoteExecutionCliException("INVALID_PARAMS", "Request exceeds the protocol payload limit.");
            try
            {
                byte[] payload = base64 != null ? Convert.FromBase64String(base64) : Encoding.UTF8.GetBytes(text ?? string.Empty);
                if (payload.Length > RemoteExecutionProtocol.MaxCommandRequestBytes)
                    throw new RemoteExecutionCliException("INVALID_PARAMS", "Request exceeds the protocol payload limit.");
                return payload;
            }
            catch (FormatException)
            {
                throw new RemoteExecutionCliException("INVALID_PARAMS", "payloadBase64 is not valid Base64.");
            }
        }

        private static RemoteExecutionResponse Status() => Describe(new
        {
            isRunning = RemoteExecutionEditorApi.IsServerRunning,
            transportKind = RemoteExecutionEditorApi.TransportKind,
            transportDescription = RemoteExecutionEditorApi.TransportDescription,
            players = RemoteExecutionEditorApi.GetClients().Select(DescribePlayer).ToArray()
        });

        private static object DescribePlayer(RemoteExecutionClientInfo player) => new
        {
            sessionId = player.Id, clientId = player.ClientId, target = player.Target,
            status = player.Status, isReady = player.IsReady,
            commandsUpdatedAt = player.CommandsUpdatedAt, commandCount = player.Commands.Count
        };

        private static RemoteExecutionResponse Describe(object value) =>
            new RemoteExecutionResponse { Result = JObject.FromObject(value) };
    }

    public sealed class RemoteExecutionCliException : Exception
    {
        public RemoteExecutionCliException(string code, string message) : base(message) { Code = code; }
        public string Code { get; }
    }
}
