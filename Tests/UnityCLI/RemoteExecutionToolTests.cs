using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using io.github.hatayama.UnityCliLoop.ToolContracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RemoteExecution.UnityCLI;
using UnityEngine;
using UnityEngine.TestTools;

namespace RemoteExecution.Tests
{
    public sealed class RemoteExecutionToolTests
    {
        [Test]
        public void ToolIsDiscoverableAndDocumentsActionsWithoutAgentBridge()
        {
            // The CLI discovers the tool and generates all action/parameter metadata.
            Assert.That(UnityEditor.TypeCache.GetTypesWithAttribute<UnityCliLoopToolAttribute>(),
                Does.Contain(typeof(RemoteExecutionTool)));
            var tool = new RemoteExecutionTool();
            Assert.That(tool.ToolName, Is.EqualTo("remote-execution"));
            Assert.That(UnityCliLoopToolRegistrar.IsCustomToolRegistered(tool.ToolName), Is.True);
            Assert.That(tool.ParameterSchema.Properties["Action"].Enum,
                Is.EquivalentTo(new[] { "status", "start", "stop", "players", "commands", "execute", "search_logs" }));
            Assert.That(tool.ParameterSchema.Properties["SessionId"].Description, Does.Contain("sessionId"));
            Assert.That(typeof(RemoteExecutionTool).Assembly.GetReferencedAssemblies()
                .Any(assembly => assembly.Name.Contains("AgentBridge")), Is.False);
        }

        [Test]
        public void ListenerLifecycleDoesNotReplaceAnActiveTransport()
        {
            // Repeated start must preserve the live listener and report a stable error.
            RemoteExecutionResponse started = Call(new JObject { ["action"] = "start", ["port"] = 0 });
            Assert.That(started.Success, Is.True);
            Assert.That(started.Result.Value<bool>("isRunning"), Is.True);
            string endpoint = started.Result.Value<string>("transportDescription");
            RemoteExecutionResponse repeated = Call(new JObject { ["action"] = "start", ["port"] = 0 });
            Assert.That(repeated.Success, Is.False);
            Assert.That(repeated.Code, Is.EqualTo("SERVER_ALREADY_RUNNING"));
            Assert.That(Call(new JObject()).Result.Value<string>("transportDescription"), Is.EqualTo(endpoint));
            Assert.That(Call(new JObject { ["action"] = "stop" }).Result.Value<bool>("isRunning"), Is.False);
        }

        [TestCase("bindAddress", "not-an-address")]
        [TestCase("port", -1)]
        [TestCase("port", 65536)]
        [TestCase("maxClients", 0)]
        [TestCase("maxClients", 1025)]
        public void InvalidListenerParametersHaveNoSideEffects(string key, object value)
        {
            // Invalid configuration cannot start a listener.
            RemoteExecutionResponse response = Call(new JObject { ["action"] = "start", [key] = JToken.FromObject(value) });
            Assert.That(response.Success, Is.False);
            Assert.That(response.Code, Is.EqualTo("INVALID_PARAMS"));
            Assert.That(RemoteExecutionEditorApi.IsServerRunning, Is.False);
        }

        [TestCase(0, "INVALID_PARAMS")]
        [TestCase(-1, "INVALID_PARAMS")]
        [TestCase(int.MaxValue, "PLAYER_NOT_CONNECTED")]
        public void InvalidSessionsReturnStructuredErrors(int sessionId, string code)
        {
            // Missing or expired sessions are actionable JSON failures.
            RemoteExecutionResponse response = Call(new JObject { ["action"] = "commands", ["sessionId"] = sessionId });
            Assert.That(response.Success, Is.False);
            Assert.That(response.Code, Is.EqualTo(code));
            Assert.That(JObject.Parse(JsonConvert.SerializeObject(response))["Result"], Is.Null);
        }

        [Test]
        public void CancelledRequestCannotStartListener()
        {
            // Cancellation propagates to the CLI host before a side effect occurs.
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () =>
                    await new RemoteExecutionTool().ExecuteAsync(new JObject { ["action"] = "start" }, cancellation.Token));
                Assert.That(RemoteExecutionEditorApi.IsServerRunning, Is.False);
            }
        }

        [UnityTest]
        public IEnumerator ExecutesPlayerCommandsPreservesPayloadsAndCancels()
        {
            // Exercise the real TCP transport, log capture and cancellation in Editor Play Mode.
            yield return new EnterPlayMode();
            yield return ExercisePlayer();
            yield return new ExitPlayMode();
        }

        private static IEnumerator ExercisePlayer()
        {
            var transport = RemoteExecutionTcpTransport.CreateServer("127.0.0.1", 0);
            RemoteExecutionEditorApi.StartServer(new RemoteExecutionServerOptions(transport));
            RemoteExecutionPlayerApi.Start("127.0.0.1", transport.Port, "CLI test Player");
            yield return WaitFor(() => RemoteExecutionPlayerApi.IsConnected &&
                RemoteExecutionEditorApi.GetClients().Any(player => player.IsReady));
            int id = RemoteExecutionEditorApi.GetClients().Single(player => player.IsReady).Id;
            Assert.That(Call(new JObject { ["action"] = "players" }).Result["players"][0].Value<int>("sessionId"), Is.EqualTo(id));

            Task<UnityCliLoopToolResponse> catalog = Run("commands", id);
            yield return WaitFor(() => catalog.IsCompleted);
            Assert.That(Result(catalog).Result["commands"].Values<JObject>()
                .Any(command => command.Value<string>("typeName") == typeof(CliEchoCommand).FullName), Is.True);

            foreach (string mode in new[] { "text", "binary", "failure", "invalid-utf8", "empty" })
            {
                CliEchoCommand.Mode = mode;
                byte[] payload = mode == "text" ? Encoding.UTF8.GetBytes("Player 诊断") :
                    mode == "empty" ? Array.Empty<byte>() : new byte[] { 0, 255, 42 };
                var args = new JObject { ["commandType"] = (mode == "text" || mode == "invalid-utf8"
                    ? typeof(CliTextEchoCommand) : typeof(CliEchoCommand)).FullName };
                if (mode == "text") args["payloadText"] = Encoding.UTF8.GetString(payload);
                else if (mode != "empty") args["payloadBase64"] = Convert.ToBase64String(payload);
                Task<UnityCliLoopToolResponse> run = Run("execute", id, args);
                yield return WaitFor(() => run.IsCompleted);
                RemoteExecutionResponse response = Result(run);
                Assert.That(response.Success, Is.EqualTo(mode != "failure"));
                JObject data = response.Result;
                Assert.That(data.Value<int>("sessionId"), Is.EqualTo(id));
                Assert.That(data.Value<bool>("succeeded"), Is.EqualTo(response.Success));
                if (mode == "failure")
                {
                    Assert.That(data.Value<string>("code"), Is.EqualTo("PROBE_FAILURE"));
                    Assert.That(data.Value<string>("message"), Is.EqualTo("Player rejected probe."));
                }
                else if (mode == "text") Assert.That(data.Value<string>("payloadText"), Is.EqualTo("Player 诊断"));
                else Assert.That(Convert.FromBase64String(data.Value<string>("payloadBase64")), Is.EqualTo(payload));
            }

            CliEchoCommand.Mode = "binary";
            byte[] large = Enumerable.Range(0, 70000).Select(index => (byte)index).ToArray();
            Task<UnityCliLoopToolResponse> artifact = Run("execute", id, new JObject
            {
                ["commandType"] = typeof(CliEchoCommand).FullName, ["payloadBase64"] = Convert.ToBase64String(large)
            });
            yield return WaitFor(() => artifact.IsCompleted);
            string path = Result(artifact).Result.Value<string>("payloadFile");
            try
            {
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(large));
                Assert.That(Path.GetDirectoryName(path), Is.EqualTo(Path.GetFullPath("Library/RemoteExecution/UnityCLI")));
                Assert.That(Result(artifact).Result["payloadBase64"], Is.Null);
            }
            finally { if (path != null) File.Delete(path); }

            foreach (JObject invalid in new[] {
                new JObject { ["payloadText"] = "", ["payloadBase64"] = "" },
                new JObject { ["payloadBase64"] = "invalid!" } })
            {
                invalid["commandType"] = typeof(CliEchoCommand).FullName;
                Task<UnityCliLoopToolResponse> run = Run("execute", id, invalid);
                yield return WaitFor(() => run.IsCompleted);
                Assert.That(Result(run).Code, Is.EqualTo("INVALID_PARAMS"));
            }
            Task<UnityCliLoopToolResponse> unavailable = Run("execute", id, new JObject { ["commandType"] = "Missing.Command" });
            yield return WaitFor(() => unavailable.IsCompleted);
            Assert.That(Result(unavailable).Code, Is.EqualTo("PLAYER_COMMAND_UNAVAILABLE"));
            Task<UnityCliLoopToolResponse> oversizedQuery = Run("search_logs", id, new JObject { ["query"] = new string('中', 1366) });
            yield return WaitFor(() => oversizedQuery.IsCompleted);
            Assert.That(Result(oversizedQuery).Code, Is.EqualTo("INVALID_PARAMS"));

            string marker = "CLI log probe " + Guid.NewGuid().ToString("N");
            UnityEngine.Debug.Log(marker);
            Task<UnityCliLoopToolResponse> logs = Run("search_logs", id, new JObject { ["query"] = marker });
            yield return WaitFor(() => logs.IsCompleted);
            Assert.That(Result(logs).Result.Value<string>("payloadText"), Does.Contain(marker));

            CliCancellationCommand.Started = CliCancellationCommand.Cancelled = false;
            using (var cancellation = new CancellationTokenSource())
            {
                Task<UnityCliLoopToolResponse> pending = Run("execute", id,
                    new JObject { ["commandType"] = typeof(CliCancellationCommand).FullName }, cancellation.Token);
                yield return WaitFor(() => CliCancellationCommand.Started);
                cancellation.Cancel();
                yield return WaitFor(() => pending.IsCompleted && CliCancellationCommand.Cancelled);
                Assert.That(pending.IsCanceled, Is.True);
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // Restore global listener and Play Mode state even after assertion failures.
            RemoteExecutionPlayerApi.Stop();
            RemoteExecutionEditorApi.StopServer();
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        private static Task<UnityCliLoopToolResponse> Run(string action, int id, JObject args = null,
            CancellationToken ct = default)
        {
            args = args ?? new JObject();
            args["action"] = action;
            args["sessionId"] = id;
            return UnityCliLoopToolRegistrar.ExecuteToolAsync("remote-execution", args, ct);
        }

        private static RemoteExecutionResponse Call(JObject args) =>
            Result(new RemoteExecutionTool().ExecuteAsync(args, CancellationToken.None));

        private static RemoteExecutionResponse Result(Task<UnityCliLoopToolResponse> task) =>
            (RemoteExecutionResponse)task.GetAwaiter().GetResult();

        private static IEnumerator WaitFor(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(25)) yield return null;
            Assert.That(condition(), Is.True, "CLI Player operation timed out.");
        }
    }

    public class CliEchoCommand : IRemoteCommand
    {
        public static string Mode = "binary";
        public string Name => "CLI echo";
        public string Description => "CLI transport regression probe.";
        public string Category => "Tests";
        public int TimeoutSeconds => 10;
        public string RequestContentType => "application/octet-stream";
        public virtual string ResponseContentType => "application/octet-stream";

        public async Task<RemoteCommandResult> ExecuteAsync(RemoteCommandContext context, CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return Mode == "failure" ? RemoteCommandResult.Failure("PROBE_FAILURE", "Player rejected probe.") :
                RemoteCommandResult.Success("Probe completed.", context.Payload, ResponseContentType);
        }
    }

    public sealed class CliTextEchoCommand : CliEchoCommand
    {
        public override string ResponseContentType => "text/plain; charset=utf-8";
    }

    public sealed class CliCancellationCommand : IRemoteCommand
    {
        public static bool Started;
        public static bool Cancelled;
        public string Name => "CLI cancellation";
        public string Description => "Cooperative cancellation regression probe.";
        public string Category => "Tests";
        public int TimeoutSeconds => 20;
        public string RequestContentType => "";
        public string ResponseContentType => "";

        public async Task<RemoteCommandResult> ExecuteAsync(RemoteCommandContext context, CancellationToken ct)
        {
            Started = true;
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return RemoteCommandResult.Success();
        }
    }
}
