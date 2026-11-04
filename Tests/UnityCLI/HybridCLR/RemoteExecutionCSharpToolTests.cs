using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using io.github.hatayama.UnityCliLoop.ToolContracts;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RemoteExecution.UnityCLI;
using UnityEngine;
using UnityEngine.TestTools;

namespace RemoteExecution.Tests
{
    public sealed class RemoteExecutionCSharpToolTests
    {
        [UnityTest]
        public IEnumerator CliCompilesExecutesAndReadsPlayerFindings()
        {
            // The optional tool compiles source and reaches the Player without AgentBridge.
            Assert.That(UnityEditor.TypeCache.GetTypesWithAttribute<UnityCliLoopToolAttribute>(),
                Does.Contain(typeof(RemoteExecutionCSharpTool)));
            Assert.That(UnityCliLoopToolRegistrar.IsCustomToolRegistered("remote-execution-csharp"), Is.True);
            Assert.That(typeof(RemoteExecutionCSharpTool).Assembly.GetReferencedAssemblies()
                .Any(assembly => assembly.Name.Contains("AgentBridge")), Is.False);
            yield return new EnterPlayMode();
            yield return ExercisePlayer();
            yield return new ExitPlayMode();
        }

        private static IEnumerator ExercisePlayer()
        {
            var transport = RemoteExecutionTcpTransport.CreateServer("127.0.0.1", 0);
            RemoteExecutionEditorApi.StartServer(new RemoteExecutionServerOptions(transport));
            RemoteExecutionPlayerApi.Start("127.0.0.1", transport.Port, "CLI C# Player");
            yield return WaitFor(() => RemoteExecutionPlayerApi.IsConnected &&
                RemoteExecutionEditorApi.GetClients().Any(player => player.IsReady));
            int id = RemoteExecutionEditorApi.GetClients().Single(player => player.IsReady).Id;

            foreach (string invalidSource in new[] { " ", new string('中', 175000) })
            {
                Task<UnityCliLoopToolResponse> invalid = new RemoteExecutionCSharpTool().ExecuteAsync(
                    new JObject { ["sessionId"] = id, ["source"] = invalidSource }, CancellationToken.None);
                yield return WaitFor(() => invalid.IsCompleted);
                Assert.That(((RemoteExecutionResponse)invalid.GetAwaiter().GetResult()).Code, Is.EqualTo("INVALID_PARAMS"));
            }

            string marker = "CliSourceProbe_" + Guid.NewGuid().ToString("N");
            string source = @"using System.Threading;
using System.Threading.Tasks;
using RemoteExecution.HybridCLR;
public sealed class CliEntry : IHybridCLRRemoteExecutionEntry
{
    public Task ExecuteAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        UnityEngine.Debug.Log(""" + marker + @""");
        return Task.CompletedTask;
    }
}";
            Task<UnityCliLoopToolResponse> run = UnityCliLoopToolRegistrar.ExecuteToolAsync("remote-execution-csharp",
                new JObject { ["sessionId"] = id, ["source"] = source }, CancellationToken.None);
            yield return WaitFor(() => run.IsCompleted);
            var result = (RemoteExecutionResponse)run.GetAwaiter().GetResult();
            Assert.That(result.Success, Is.True, result.Result?.ToString() ?? result.Message);
            Task<UnityCliLoopToolResponse> logs = UnityCliLoopToolRegistrar.ExecuteToolAsync("remote-execution", new JObject
            {
                ["action"] = "search_logs", ["sessionId"] = id, ["query"] = marker
            }, CancellationToken.None);
            yield return WaitFor(() => logs.IsCompleted);
            Assert.That(((RemoteExecutionResponse)logs.GetAwaiter().GetResult()).Result.Value<string>("payloadText"),
                Does.Contain(marker));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // Release the shared transport and leave the Editor in Edit Mode.
            RemoteExecutionPlayerApi.Stop();
            RemoteExecutionEditorApi.StopServer();
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        private static IEnumerator WaitFor(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(150)) yield return null;
            Assert.That(condition(), Is.True, "CLI source operation timed out.");
        }
    }
}
