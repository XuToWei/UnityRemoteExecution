using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentBridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RemoteExecution.AgentBridge;
using UnityEngine;
using UnityEngine.TestTools;

namespace RemoteExecution.Tests
{
    public sealed class RemoteExecutionHandlerTests
    {
        [Test]
        public void BridgeDiscoversHandlerWithSchema()
        {
            Assert.That(UnityEditor.TypeCache.GetTypesDerivedFrom<ICommandHandler>(),
                Does.Contain(typeof(RemoteExecutionHandler)));
            var handler = new RemoteExecutionHandler();
            Assert.That(handler.ParamsSchema["required"].Values<string>(), Does.Contain("action"));
            Assert.That(handler.BatchMode, Is.EqualTo(CommandBatchMode.NotAllowed));
        }

        [Test]
        public void InvalidActionsAndSessionsHaveStableErrors()
        {
            var handler = new RemoteExecutionHandler();
            Assert.That(Assert.ThrowsAsync<CommandException>(async () =>
                await handler.ExecuteAsync(new JObject { ["action"] = "unknown" })).Code,
                Is.EqualTo("INVALID_PARAMS"));
            foreach (JToken id in new JToken[] { null, new JValue(0), new JValue("1"), new JValue(long.MaxValue) })
            {
                var args = new JObject { ["action"] = "commands", ["sessionId"] = id };
                Assert.That(Assert.ThrowsAsync<CommandException>(async () =>
                    await handler.ExecuteAsync(args)).Code, Is.EqualTo("INVALID_PARAMS"));
            }
            Assert.That(Assert.ThrowsAsync<CommandException>(async () => await handler.ExecuteAsync(
                new JObject { ["action"] = "commands", ["sessionId"] = int.MaxValue })).Code,
                Is.EqualTo("PLAYER_NOT_CONNECTED"));
        }

        [Test]
        public void StartingAnActiveListenerDoesNotReplaceIt()
        {
            var handler = new RemoteExecutionHandler();
            try
            {
                JObject started = Call(handler, new JObject { ["action"] = "start", ["port"] = 0 });
                Assert.That(started.Value<bool>("isRunning"), Is.True);
                string endpoint = started.Value<string>("transportDescription");
                Assert.That(Assert.ThrowsAsync<CommandException>(async () =>
                    await handler.ExecuteAsync(new JObject { ["action"] = "start", ["port"] = 0 })).Code,
                    Is.EqualTo("SERVER_ALREADY_RUNNING"));
                Assert.That(RemoteExecutionEditorApi.TransportDescription, Is.EqualTo(endpoint));
                Assert.That(Call(handler, new JObject { ["action"] = "stop" }).Value<bool>("isRunning"), Is.False);
            }
            finally { RemoteExecutionEditorApi.StopServer(); }
        }

        [UnityTest]
        public IEnumerator CommandsRunOnExplicitPlayerAndPreserveResults()
        {
            yield return new EnterPlayMode();
            yield return ExercisePlayer();
            yield return new ExitPlayMode();
        }

        private static IEnumerator ExercisePlayer()
        {
            var transport = RemoteExecutionTcpTransport.CreateServer("127.0.0.1", 0);
            RemoteExecutionEditorApi.StartServer(new RemoteExecutionServerOptions(transport));
            RemoteExecutionPlayerApi.Start("127.0.0.1", transport.Port, "Bridge test Player");
            yield return WaitFor(() => RemoteExecutionPlayerApi.IsConnected &&
                RemoteExecutionEditorApi.GetClients().Any(p => p.IsReady));
            int id = RemoteExecutionEditorApi.GetClients().Single(p => p.IsReady).Id;
            var handler = new RemoteExecutionHandler();
            JObject players = Call(handler, new JObject { ["action"] = "players" });
            Assert.That(players["players"][0].Value<int>("sessionId"), Is.EqualTo(id));
            Task<object> catalog = handler.ExecuteAsync(new JObject { ["action"] = "commands", ["sessionId"] = id });
            yield return WaitFor(() => catalog.IsCompleted);
            Assert.That(JObject.FromObject(catalog.GetAwaiter().GetResult())["commands"]
                .Any(c => c.Value<string>("typeName") == typeof(BridgeEchoCommand).FullName), Is.True);

            foreach (string mode in new[] { "text", "binary", "failure", "invalid-utf8" })
            {
                BridgeEchoCommand.Mode = mode;
                byte[] bytes = mode == "text" ? Encoding.UTF8.GetBytes("Player 诊断") : new byte[] { 0, 255, 42 };
                var args = new JObject { ["action"] = "execute", ["sessionId"] = id,
                    ["commandType"] = (mode == "text" || mode == "invalid-utf8"
                        ? typeof(BridgeTextEchoCommand) : typeof(BridgeEchoCommand)).FullName };
                if (mode == "text") args["payloadText"] = Encoding.UTF8.GetString(bytes);
                else args["payloadBase64"] = Convert.ToBase64String(bytes);
                Task<object> task = handler.ExecuteAsync(args);
                yield return WaitFor(() => task.IsCompleted);
                JObject result = JObject.FromObject(task.GetAwaiter().GetResult());
                Assert.That(result.Value<int>("sessionId"), Is.EqualTo(id));
                Assert.That(result.Value<bool>("succeeded"), Is.EqualTo(mode != "failure"));
                if (mode == "failure")
                {
                    Assert.That(result.Value<string>("code"), Is.EqualTo("PROBE_FAILURE"));
                    Assert.That(result.Value<string>("message"), Is.EqualTo("Player rejected probe."));
                }
                else if (mode == "text")
                    Assert.That(result.Value<string>("payloadText"), Is.EqualTo("Player 诊断"));
                else
                    Assert.That(Convert.FromBase64String(result.Value<string>("payloadBase64")), Is.EqualTo(bytes));
            }

            BridgeEchoCommand.Mode = "binary";
            byte[] large = Enumerable.Range(0, 70000).Select(i => (byte)i).ToArray();
            Task<object> artifact = handler.ExecuteAsync(new JObject
            {
                ["action"] = "execute", ["sessionId"] = id,
                ["commandType"] = typeof(BridgeEchoCommand).FullName,
                ["payloadBase64"] = Convert.ToBase64String(large)
            });
            yield return WaitFor(() => artifact.IsCompleted);
            JObject artifactResult = JObject.FromObject(artifact.GetAwaiter().GetResult());
            string artifactPath = artifactResult.Value<string>("payloadFile");
            try
            {
                Assert.That(File.ReadAllBytes(artifactPath), Is.EqualTo(large));
                Assert.That(artifactResult.Value<int>("payloadBytes"), Is.EqualTo(large.Length));
                Assert.That(artifactResult["payloadBase64"], Is.Null);
            }
            finally { if (artifactPath != null) File.Delete(artifactPath); }

            foreach (JObject extra in new[] {
                new JObject { ["payloadText"] = "", ["payloadBase64"] = "" },
                new JObject { ["payloadBase64"] = "invalid!" } })
            {
                extra["action"] = "execute";
                extra["sessionId"] = id;
                extra["commandType"] = typeof(BridgeEchoCommand).FullName;
                Task<object> invalid = handler.ExecuteAsync(extra);
                yield return WaitFor(() => invalid.IsCompleted);
                Assert.That(invalid.Exception?.GetBaseException(), Is.TypeOf<CommandException>());
                Assert.That(((CommandException)invalid.Exception.GetBaseException()).Code, Is.EqualTo("INVALID_PARAMS"));
            }

            string marker = "Bridge log probe " + Guid.NewGuid().ToString("N");
            UnityEngine.Debug.Log(marker);
            Task<object> logs = handler.ExecuteAsync(new JObject { ["action"] = "search_logs",
                ["sessionId"] = id, ["query"] = marker });
            yield return WaitFor(() => logs.IsCompleted);
            Assert.That(JObject.FromObject(logs.GetAwaiter().GetResult()).Value<string>("payloadText"),
                Does.Contain(marker));
            RemoteExecutionPlayerApi.Stop();
            RemoteExecutionEditorApi.StopServer();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            RemoteExecutionPlayerApi.Stop();
            RemoteExecutionEditorApi.StopServer();
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        private static JObject Call(RemoteExecutionHandler handler, JObject args) =>
            JObject.FromObject(handler.ExecuteAsync(args).GetAwaiter().GetResult());

        private static IEnumerator WaitFor(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) yield return null;
            Assert.That(condition(), Is.True, "Bridge Player operation timed out.");
        }
    }

    public class BridgeEchoCommand : IRemoteCommand
    {
        public static string Mode = "text";
        public string Name => "Bridge echo";
        public string Description => "Bridge transport regression probe.";
        public string Category => "Tests";
        public int TimeoutSeconds => 10;
        public string RequestContentType => "application/octet-stream";
        public virtual string ResponseContentType => "application/octet-stream";

        public async Task<RemoteCommandResult> ExecuteAsync(RemoteCommandContext context, CancellationToken token)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            return Mode == "failure" ? RemoteCommandResult.Failure("PROBE_FAILURE", "Player rejected probe.") :
                RemoteCommandResult.Success("Probe completed.", context.Payload, ResponseContentType);
        }
    }
    public sealed class BridgeTextEchoCommand : BridgeEchoCommand
    {
        public override string ResponseContentType => "text/plain; charset=utf-8";
    }
}
