using System;
using System.IO;
using System.Text;
using io.github.hatayama.UnityCliLoop.ToolContracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RemoteExecution.UnityCLI
{
    /// <summary>Success also reflects Player failures, not just successful tool dispatch.</summary>
    public sealed class RemoteExecutionResponse : UnityCliLoopToolResponse
    {
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public JObject Result { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Code { get; set; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string Message { get; set; }

        public static RemoteExecutionResponse Failure(string code, string message) =>
            new RemoteExecutionResponse { Success = false, Code = code, Message = message };

        public static RemoteExecutionResponse FromResult(int sessionId, RemoteExecutionResult result)
        {
            var data = new JObject
            {
                ["sessionId"] = sessionId, ["succeeded"] = result.Succeeded,
                ["code"] = result.Code, ["message"] = result.Message,
                ["contentType"] = result.ContentType, ["payloadBytes"] = result.Payload.Length
            };
            // Keep large results out of the CLI JSON envelope, including escaped text.
            if (result.Payload.Length > 64 * 1024)
            {
                string directory = Path.GetFullPath("Library/RemoteExecution/UnityCLI");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".bin");
                File.WriteAllBytes(path, result.Payload);
                data["payloadFile"] = path;
            }
            else
            {
                string mediaType = result.ContentType.Split(';')[0].Trim();
                bool isText = mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                    mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                    mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
                string text = null;
                if (isText)
                {
                    try { text = new UTF8Encoding(false, true).GetString(result.Payload); }
                    catch (DecoderFallbackException) { /* Preserve invalid UTF-8 as binary. */ }
                }
                if (text != null) data["payloadText"] = text;
                else data["payloadBase64"] = Convert.ToBase64String(result.Payload);
            }
            return new RemoteExecutionResponse { Success = result.Succeeded, Result = data };
        }
    }
}
