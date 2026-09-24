using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AgentBridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RemoteExecution.AgentBridge;
using UnityEngine;
using UnityEngine.TestTools;

namespace RemoteExecution.Tests
{
    public sealed class RemoteExecutionCSharpHandlerTests
    {
        [UnityTest]
        public IEnumerator BridgeCompilesExecutesAndReadsPlayerFindings()
        {
            Assert.That(UnityEditor.TypeCache.GetTypesDerivedFrom<ICommandHandler>(),
                Does.Contain(typeof(RemoteExecutionCSharpHandler)));
            yield return new EnterPlayMode();
            yield return ExercisePlayer();
            yield return new ExitPlayMode();
        }

        private static IEnumerator ExercisePlayer()
        {
            var transport = RemoteExecutionTcpTransport.CreateServer("127.0.0.1", 0);
            RemoteExecutionEditorApi.StartServer(new RemoteExecutionServerOptions(transport));
            RemoteExecutionPlayerApi.Start("127.0.0.1", transport.Port, "Bridge C# Player");
            yield return WaitFor(() => RemoteExecutionPlayerApi.IsConnected &&
                RemoteExecutionEditorApi.GetClients().Any(p => p.IsReady));
            int id = RemoteExecutionEditorApi.GetClients().Single(p => p.IsReady).Id;
            string marker = "BridgeSourceProbe_" + Guid.NewGuid().ToString("N");
            string source = @"using System.Threading;
using System.Threading.Tasks;
using RemoteExecution.HybridCLR;
public sealed class BridgeEntry : IHybridCLRRemoteExecutionEntry
{
    public Task ExecuteAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        UnityEngine.Debug.Log(""" + marker + @""");
        return Task.CompletedTask;
    }
}";
            Task<object> run = new RemoteExecutionCSharpHandler().ExecuteAsync(
                new JObject { ["sessionId"] = id, ["source"] = source });
            yield return WaitFor(() => run.IsCompleted);
            JObject result = JObject.FromObject(run.GetAwaiter().GetResult());
            Assert.That(result.Value<bool>("succeeded"), Is.True, result.ToString());
            Task<object> logs = new RemoteExecutionHandler().ExecuteAsync(new JObject
            {
                ["action"] = "search_logs", ["sessionId"] = id, ["query"] = marker
            });
            yield return WaitFor(() => logs.IsCompleted);
            Assert.That(JObject.FromObject(logs.GetAwaiter().GetResult()).Value<string>("payloadText"),
                Does.Contain(marker));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            RemoteExecutionPlayerApi.Stop();
            RemoteExecutionEditorApi.StopServer();
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        private static IEnumerator WaitFor(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(150)) yield return null;
            Assert.That(condition(), Is.True, "Bridge source operation timed out.");
        }
    }
}
