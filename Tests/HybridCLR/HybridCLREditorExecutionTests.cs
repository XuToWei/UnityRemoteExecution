using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RemoteExecution.HybridCLR;
using UnityEditor;
using UnityEngine;
using UnityEngine.Assemblies;
using UnityEngine.TestTools;

namespace RemoteExecution.Tests
{
    public sealed class HybridCLREditorExecutionTests
    {
        [UnityTest]
        public IEnumerator ConsecutiveBuildsLoadUniqueAssembliesWithAndWithoutSymbols()
        {
            Assert.That(Application.isPlaying, Is.False);
            Assembly editor = typeof(HybridCLRRemoteExecutionEditorApi).Assembly;
            Type panel = editor.GetType("RemoteExecution.HybridCLR.HybridCLRRemoteExecutionPanel", true);
            string source = (string)panel.GetField("DefaultSource",
                BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
            Type requestType = editor.GetType("RemoteExecution.HybridCLR.HybridCLRRemoteBuildRequest", true);
            object request = Activator.CreateInstance(requestType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { Application.platform.ToString(), source }, null);
            Type compiler = editor.GetType("RemoteExecution.HybridCLR.HybridCLRRemoteExecutionCompiler", true);
            string[] names = new string[2];
            for (int i = 0; i < names.Length; i++)
            {
                var task = (Task)compiler.GetMethod("BuildAsync", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new[] { request, (object)CancellationToken.None });
                yield return WaitFor(() => task.IsCompleted,
                    "Default source compilation did not finish.");
                Assert.That(task.IsFaulted, Is.False,
                    task.Exception?.GetBaseException().Message);
                task.GetAwaiter().GetResult();
                object output = task.GetType().GetProperty("Result").GetValue(task);
                var artifacts = (System.Collections.IList)output.GetType()
                    .GetProperty("Artifacts", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(output);
                var artifact = (HybridCLRBundleArtifact)artifacts[0];
                names[i] = artifact.Name;
                Assert.That(names[i], Does.StartWith("RemoteExecution.Dynamic."));
                if (i == 0) Assert.That(artifact.Pdb, Is.Not.Empty);
                Assembly loaded = i == 0
                    ? CurrentAssemblies.LoadFromBytes(artifact.Dll, artifact.Pdb)
                    : CurrentAssemblies.LoadFromBytes(artifact.Dll);
                Assert.That(loaded.GetName().Name, Is.EqualTo(artifact.Name));
                Assert.That(loaded.GetTypes().Any(type =>
                    typeof(IHybridCLRRemoteExecutionEntry).IsAssignableFrom(type)), Is.True,
                    "Dynamic entries must share the project's runtime types.");
                Assert.That(CurrentAssemblies.GetLoadedAssemblies(), Does.Contain(loaded),
                    "Dynamic assemblies must be visible in the current Unity execution context.");
            }
            Assert.That(names[1], Is.Not.EqualTo(names[0]));
        }

        [UnityTest]
        public IEnumerator DefaultSourceRunsRepeatedlyInEditor()
        {
            bool reloadDomain = !EditorSettings.enterPlayModeOptionsEnabled ||
                (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
            yield return new EnterPlayMode(reloadDomain);
            yield return ExecuteDefaultSource();
            yield return new ExitPlayMode();
        }

        private static IEnumerator ExecuteDefaultSource()
        {
            int entryLogCount = 0;
            Application.LogCallback observe = (message, trace, type) =>
            {
                if (type == LogType.Log && message == "Remote execution entry completed.")
                    entryLogCount++;
            };
            using (var cancellation = new CancellationTokenSource())
            {
                try
                {
                    Application.logMessageReceived += observe;
                    var transport = RemoteExecutionTcpTransport.CreateServer("127.0.0.1", 0);
                    RemoteExecutionEditorApi.StartServer(new RemoteExecutionServerOptions(transport));
                    RemoteExecutionPlayerApi.Start("127.0.0.1", transport.Port, "HybridCLR Editor regression");
                    yield return WaitFor(() => RemoteExecutionPlayerApi.IsConnected &&
                        RemoteExecutionEditorApi.GetClients().Any(player => player.IsReady &&
                            player.Commands.Any(command => command.TypeName == HybridCLRBundleCodec.TypeName)),
                        "The Editor Player did not publish the HybridCLR command.");
                    RemoteExecutionClientInfo player = RemoteExecutionEditorApi.GetClients().Single(item => item.IsReady);

                    Assembly editor = typeof(HybridCLRRemoteExecutionEditorApi).Assembly;
                    Type panel = editor.GetType("RemoteExecution.HybridCLR.HybridCLRRemoteExecutionPanel", true);
                    string source = (string)panel.GetField("DefaultSource",
                        BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
                    Type requestType = editor.GetType("RemoteExecution.HybridCLR.HybridCLRRemoteBuildRequest", true);
                    object request = Activator.CreateInstance(requestType, BindingFlags.Instance | BindingFlags.NonPublic,
                        null, new object[] { player.Target, source }, null);
                    for (int i = 0; i < 2; i++)
                    {
                        var execution = (Task<string>)panel.GetMethod("RunAsync",
                                BindingFlags.Static | BindingFlags.NonPublic)
                            .Invoke(null, new[] { (object)player.Id, request,
                                cancellation.Token });
                        yield return WaitFor(() => execution.IsCompleted,
                            "Default source execution did not complete.");
                        Assert.That(execution.IsFaulted, Is.False,
                            execution.Exception?.GetBaseException().Message);
                        Assert.That(execution.GetAwaiter().GetResult(),
                            Does.Contain("entry executed"));
                    }
                    Assert.That(entryLogCount, Is.EqualTo(2),
                        "Each execution must load and invoke a new dynamic assembly.");
                }
                finally
                {
                    cancellation.Cancel();
                    Application.logMessageReceived -= observe;
                    RemoteExecutionPlayerApi.Stop();
                    RemoteExecutionEditorApi.StopServer();
                }
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            RemoteExecutionPlayerApi.Stop();
            RemoteExecutionEditorApi.StopServer();
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        private static IEnumerator WaitFor(Func<bool> condition, string message)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(150)) yield return null;
            Assert.That(condition(), Is.True, message);
        }
    }
}
