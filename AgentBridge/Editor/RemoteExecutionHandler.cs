using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentBridge;
using Newtonsoft.Json.Linq;

namespace RemoteExecution.AgentBridge
{
    /// <summary>Uses the same listener and Player sessions as the Editor window.</summary>
    public sealed class RemoteExecutionHandler : ICommandHandler
    {
        public string Command => "remote_execution";
        public string Description => "Control Remote Execution: status/start/stop the Editor listener, " +
            "list players, refresh commands, execute a Player command, or search_logs. " +
            "Use numeric sessionId from players (not clientId). Player must connect itself. " +
            "execute uses commandType from commands and payloadText (UTF-8) or payloadBase64. " +
            "Check result.succeeded/code/message for Player failures. Large results return a local payloadFile. No window selection required.";
        public string Group => "Remote Execution";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""required"": [""action""],
  ""properties"": {
    ""action"": { ""type"": ""string"", ""enum"": [""status"", ""start"", ""stop"", ""players"", ""commands"", ""execute"", ""search_logs""] },
    ""bindAddress"": { ""type"": ""string"", ""default"": ""127.0.0.1"" },
    ""port"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 65535, ""default"": 38421 },
    ""maxClients"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1024, ""default"": 4 },
    ""sessionId"": { ""type"": ""integer"", ""minimum"": 1 },
    ""commandType"": { ""type"": ""string"", ""description"": ""Exact typeName returned by commands."" },
    ""payloadText"": { ""type"": ""string"", ""description"": ""UTF-8 request; mutually exclusive with payloadBase64."" },
    ""payloadBase64"": { ""type"": ""string"" },
    ""query"": { ""type"": ""string"", ""default"": """" }
  }
}");

        public async Task<object> ExecuteAsync(JObject args)
        {
            string action = args?["action"]?.Value<string>();
            switch (action)
            {
                case "status": return Status();
                case "players":
                    return new { players = RemoteExecutionEditorApi.GetClients().Select(DescribePlayer).ToArray() };
                case "start":
                    // StartServer replaces the transport; do not silently disconnect Players.
                    if (RemoteExecutionEditorApi.IsServerRunning)
                        throw new CommandException("SERVER_ALREADY_RUNNING", "Stop the active listener before starting another. Use status to inspect it.");
                    RemoteExecutionEditorApi.StartServer(args.Value<string>("bindAddress") ?? "127.0.0.1",
                        args.Value<int?>("port") ?? 38421, args.Value<int?>("maxClients") ?? 4);
                    return Status();
                case "stop":
                    RemoteExecutionEditorApi.StopServer();
                    return Status();
                case "commands":
                case "execute":
                case "search_logs":
                    break;
                default:
                    throw new CommandException("INVALID_PARAMS", "Unknown or missing action.");
            }

            int sessionId = RequireSessionId(args);
            RequireReadyPlayer(sessionId);
            await RemoteExecutionEditorApi.RefreshCommandsAsync(sessionId);
            RemoteExecutionClientInfo player = RequireReadyPlayer(sessionId);
            if (action == "commands")
                return new { sessionId, commands = player.Commands.Select(command => new
                {
                    typeName = command.TypeName, name = command.Name, description = command.Description,
                    category = command.Category, timeoutSeconds = command.TimeoutSeconds,
                    requestContentType = command.RequestContentType,
                    responseContentType = command.ResponseContentType, executable = command.Executable
                }).ToArray() };

            string commandType = action == "search_logs" ? typeof(SearchPlayerLogsCommand).FullName :
                args.Value<string>("commandType");
            if (string.IsNullOrWhiteSpace(commandType))
                throw new CommandException("INVALID_PARAMS", "execute requires commandType from commands.");
            RemoteCommandSnapshot selected = player.Commands.FirstOrDefault(command => command.TypeName == commandType);
            if (selected == null || !selected.Executable)
                throw new CommandException("PLAYER_COMMAND_UNAVAILABLE", "Player does not advertise an executable command: " + commandType);
            byte[] payload = action == "search_logs"
                ? Encoding.UTF8.GetBytes(args.Value<string>("query") ?? string.Empty) : ReadPayload(args);
            if (action == "search_logs" && payload.Length > SearchPlayerLogsCommand.MaxQueryBytes)
                throw new CommandException("INVALID_PARAMS", "Log query exceeds 4096 UTF-8 bytes.");
            RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync(
                sessionId, commandType, payload, selected.RequestContentType);
            return DescribeResult(sessionId, result);
        }

        public static int RequireSessionId(JObject args)
        {
            JToken token = args?["sessionId"];
            if (token == null || token.Type != JTokenType.Integer ||
                !int.TryParse(token.ToString(), out int id) || id <= 0)
                throw new CommandException("INVALID_PARAMS", "A positive sessionId from players is required.");
            return id;
        }

        public static RemoteExecutionClientInfo RequireReadyPlayer(int sessionId)
        {
            RemoteExecutionClientInfo player = RemoteExecutionEditorApi.GetClients().FirstOrDefault(p => p.Id == sessionId);
            if (player == null) throw new CommandException("PLAYER_NOT_CONNECTED", "Player session is no longer connected. Query players again.");
            if (!player.IsReady) throw new CommandException("PLAYER_NOT_READY", "Player handshake is not complete.");
            return player;
        }

        public static object DescribeResult(int sessionId, RemoteExecutionResult result)
        {
            var response = new JObject
            {
                ["sessionId"] = sessionId, ["succeeded"] = result.Succeeded,
                ["code"] = result.Code, ["message"] = result.Message,
                ["contentType"] = result.ContentType, ["payloadBytes"] = result.Payload.Length
            };
            // Bridge limits the complete JSON exchange to 1 MiB. Keep binary artifacts
            // outside that envelope, including text which JSON escaping can expand.
            if (result.Payload.Length > 64 * 1024)
            {
                string directory = Path.GetFullPath("Library/RemoteExecution/AgentBridge");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".bin");
                File.WriteAllBytes(path, result.Payload);
                response["payloadFile"] = path;
                return response;
            }
            string mediaType = result.ContentType.Split(';')[0].Trim();
            if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    response["payloadText"] = new UTF8Encoding(false, true).GetString(result.Payload);
                    return response;
                }
                catch (DecoderFallbackException) { /* Preserve non-UTF-8 bytes losslessly. */ }
            }
            response["payloadBase64"] = Convert.ToBase64String(result.Payload);
            return response;
        }

        private static byte[] ReadPayload(JObject args)
        {
            string text = args.Value<string>("payloadText");
            string base64 = args.Value<string>("payloadBase64");
            if (text != null && base64 != null)
                throw new CommandException("INVALID_PARAMS", "Provide only payloadText or payloadBase64.");
            try
            {
                if ((text != null && Encoding.UTF8.GetByteCount(text) > RemoteExecutionProtocol.MaxCommandRequestBytes) ||
                    (base64 != null && base64.Length > ((long)RemoteExecutionProtocol.MaxCommandRequestBytes + 2) / 3 * 4))
                    throw new CommandException("INVALID_PARAMS", "Request exceeds the protocol payload limit.");
                byte[] payload = base64 != null ? Convert.FromBase64String(base64) : Encoding.UTF8.GetBytes(text ?? string.Empty);
                if (payload.Length > RemoteExecutionProtocol.MaxCommandRequestBytes)
                    throw new CommandException("INVALID_PARAMS", "Request exceeds the protocol payload limit.");
                return payload;
            }
            catch (FormatException)
            {
                throw new CommandException("INVALID_PARAMS", "payloadBase64 is not valid Base64.");
            }
        }

        private static object Status() => new
        {
            isRunning = RemoteExecutionEditorApi.IsServerRunning,
            transportKind = RemoteExecutionEditorApi.TransportKind,
            transportDescription = RemoteExecutionEditorApi.TransportDescription,
            players = RemoteExecutionEditorApi.GetClients().Select(DescribePlayer).ToArray()
        };

        private static object DescribePlayer(RemoteExecutionClientInfo player) => new
        {
            sessionId = player.Id, clientId = player.ClientId, target = player.Target,
            status = player.Status, isReady = player.IsReady,
            commandsUpdatedAt = player.CommandsUpdatedAt, commandCount = player.Commands.Count
        };
    }
}
