using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace RemoteExecution.Tests
{
    public sealed class RemoteExecutionEditorPlayerTests
    {
        [Test]
        public void EditModeDoesNotCreateARuntimeClient()
        {
            Assert.That(Application.isPlaying, Is.False);
            using (var transport = RemoteExecutionTcpTransport.CreateClient())
                Assert.Throws<InvalidOperationException>(() =>
                    RemoteExecutionPlayerApi.Start(new RemoteExecutionPlayerOptions(transport)));
            Assert.That(DriverCount(), Is.Zero);
        }

        [Test]
        public void DefaultClientIdIsAnEightCharacterHash()
        {
            string clientId = (string)typeof(RemoteExecutionPlayerApi)
                .GetMethod("ResolveClientId", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, null);
            Assert.That(clientId, Does.Match("^[0-9a-f]{8}$"));
        }

        [UnityTest]
        public IEnumerator EditorPlayerConnectsExecutesAndCleansUpAcrossReconnects()
        {
            bool reloadDomain = !EditorSettings.enterPlayModeOptionsEnabled ||
                (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0;
            yield return new EnterPlayMode(reloadDomain);
            yield return ExerciseEditorPlayer();
            yield return new ExitPlayMode();
            Assert.That(RemoteExecutionPlayerApi.IsConnected, Is.False);
            Assert.That(DriverCount(), Is.Zero, "Exiting Play Mode must not leave a hidden Player driver.");
        }

        private static IEnumerator ExerciseEditorPlayer()
        {
            var transport = RemoteExecutionTcpTransport.CreateServer("127.0.0.1", 0);
            RemoteExecutionEditorApi.StartServer(new RemoteExecutionServerOptions(transport));
            int mainThreadId = Thread.CurrentThread.ManagedThreadId;
            bool wrongCallbackThread = false;
            Action<RemoteExecutionConnectionState> stateChanged = _ =>
                wrongCallbackThread |= Thread.CurrentThread.ManagedThreadId != mainThreadId;
            RemoteExecutionPlayerApi.ConnectionStateChanged += stateChanged;
            try
            {
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    string clientId = "Editor Play Mode Probe " + cycle;
                    RemoteExecutionPlayerApi.Start("127.0.0.1", transport.Port, clientId);
                    yield return WaitFor(() => RemoteExecutionPlayerApi.IsConnected ||
                        RemoteExecutionPlayerApi.ConnectionState == RemoteExecutionConnectionState.Faulted,
                        "The Editor Player did not complete its handshake.");
                    Assert.That(RemoteExecutionPlayerApi.IsConnected, Is.True,
                        RemoteExecutionPlayerApi.LastError?.Message);
                    yield return WaitFor(() => RemoteExecutionEditorApi.GetClients().Any(client =>
                        client.ClientId == clientId && client.IsReady &&
                        client.Commands.Any(command => command.TypeName == typeof(EditorPlayerEchoCommand).FullName)),
                        "The Editor Player did not publish its command catalog.");
                    RemoteExecutionClientInfo player = RemoteExecutionEditorApi.GetClients()
                        .Single(client => client.ClientId == clientId && client.IsReady);
                    Assert.That(player.Commands.Any(command =>
                        command.TypeName == typeof(SearchPlayerLogsCommand).FullName), Is.True);
                    Assert.That(player.Target, Is.EqualTo(Application.platform.ToString()));
                    Assert.That(DriverCount(), Is.EqualTo(1));

                    byte[] payload = Enumerable.Range(0, RemoteExecutionProtocol.MaxChunkBytes + 7)
                        .Select(value => (byte)value).ToArray();
                    Task<RemoteExecutionResult> execution =
                        RemoteExecutionEditorApi.ExecuteCommandAsync<EditorPlayerEchoCommand>(player.Id, payload);
                    yield return WaitFor(() => execution.IsCompleted, "The loopback command did not finish.");
                    RemoteExecutionResult result = execution.GetAwaiter().GetResult();
                    Assert.That(result.Succeeded, Is.True, result.Message);
                    Assert.That(result.Payload, Is.EqualTo(payload));
                    Assert.That(result.Message, Is.EqualTo($"{mainThreadId}:{mainThreadId}"));

                    if (cycle == 0)
                    {
                        RemoteExecutionPlayerApi.Stop();
                        Assert.That(DriverCount(), Is.EqualTo(1), "Manual Stop keeps the driver available for reconnect.");
                    }
                    else if (cycle == 1)
                    {
                        ShutdownEditorPlayer();
                        Assert.That(DriverCount(), Is.Zero, "Editor shutdown must remove the old driver before script reload.");
                    }
                    if (cycle < 2)
                    {
                        Assert.That(RemoteExecutionPlayerApi.ConnectionState,
                            Is.EqualTo(RemoteExecutionConnectionState.Disconnected));
                        yield return WaitFor(() => RemoteExecutionEditorApi.GetClients()
                            .All(client => client.ClientId != clientId), "The disconnected session was not retired.");
                    }
                }
                Assert.That(wrongCallbackThread, Is.False);
                Assert.That(RemoteExecutionPlayerApi.IsConnected, Is.True);
            }
            finally { RemoteExecutionPlayerApi.ConnectionStateChanged -= stateChanged; }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            ShutdownEditorPlayer();
            RemoteExecutionEditorApi.StopServer();
            if (Application.isPlaying) yield return new ExitPlayMode();
        }

        private static void ShutdownEditorPlayer() => typeof(RemoteExecutionPlayerApi)
            .GetMethod("ShutdownEditorPlayer", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);

        private static int DriverCount() => Resources.FindObjectsOfTypeAll(
            TestReflection.RuntimeType("RemoteExecutionPlayerDriver")).Length;

        private static IEnumerator WaitFor(Func<bool> condition, string message)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(10)) yield return null;
            Assert.That(condition(), Is.True, message);
        }
    }

    public sealed class EditorPlayerEchoCommand : IRemoteCommand
    {
        public string Name => "Editor Player echo";
        public string Description => "Checks asynchronous command execution over a real Editor loopback connection.";
        public string Category => "Tests";
        public int TimeoutSeconds => 10;
        public string RequestContentType => "application/octet-stream";
        public string ResponseContentType => "application/octet-stream";

        public async Task<RemoteCommandResult> ExecuteAsync(RemoteCommandContext context,
            CancellationToken cancellationToken)
        {
            int startedOnThread = Thread.CurrentThread.ManagedThreadId;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return RemoteCommandResult.Success($"{startedOnThread}:{Thread.CurrentThread.ManagedThreadId}",
                context.Payload, ResponseContentType);
        }
    }
}
