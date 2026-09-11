using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace RemoteExecution.Tests
{
    public sealed class RemoteExecutionRegressionTests
    {
        [SetUp]
        public void SetUp()
        {
            TestCommand<int>.Mode = "echo";
            TestCommand<int>.CancellationObserved = false;
            TestCommand<int>.Deferred = null;
        }

        [TearDown]
        public void TearDown() => TestCommand<int>.Deferred?.TrySetCanceled();

        [Test]
        public void BinaryRequestAndResponseRoundTripAcrossMultipleChunks()
        {
            var payload = new byte[RemoteExecutionProtocol.MaxChunkBytes * 2 + 7];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;
            using (var session = new SessionHarness(true))
            {
                RemoteExecutionResult result = Result(session.Execute(payload));
                Assert.That(result.Succeeded, Is.True);
                Assert.That(result.Payload, Is.EqualTo(payload));
            }
        }

        [TestCase("exception", "COMMAND_EXECUTION_FAILED")]
        [TestCase("failure", "BUSINESS_FAILURE")]
        public void FailureWithoutPayloadPreservesRemoteError(string mode, string expectedCode)
        {
            TestCommand<int>.Mode = mode;
            using (var session = new SessionHarness(true))
            {
                RemoteExecutionResult result = Result(session.Execute());
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Code, Is.EqualTo(expectedCode));
                Assert.That(result.Message, Is.EqualTo("test failure"));
            }
        }

        [TestCase("wrongSuccessType")]
        [TestCase("wrongFailureType")]
        public void PayloadWithWrongResponseTypeIsStillRejected(string mode)
        {
            TestCommand<int>.Mode = mode;
            using (var session = new SessionHarness(true))
                Assert.Throws<InvalidDataException>(() => Result(session.Execute()));
        }

        [Test]
        public void RemoteCancellationPreservesItsErrorCode()
        {
            TestCommand<int>.Mode = "waitForCancellation";
            using (var session = new SessionHarness(true))
            {
                Task<RemoteExecutionResult> operation = session.Execute();
                session.Host.Handle(new RemoteFrame(RemoteMessageKind.CancelCommand,
                    session.Channel.RequestId, Array.Empty<byte>()));
                Assert.That(Result(operation).Code, Is.EqualTo("COMMAND_CANCELLED"));
            }
        }

        [Test]
        public void SynchronousCooperativeCommandTimesOutWithoutDriverUpdate()
        {
            TestCommand<int>.Mode = "synchronousTimeout";
            using (var session = new SessionHarness(true))
            {
                RemoteExecutionResult result = Result(session.Execute());
                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Code, Is.EqualTo("COMMAND_TIMED_OUT"));
                Assert.That(TestCommand<int>.CancellationObserved, Is.True);
            }
        }

        [Test]
        public void ReusedRequestBufferRejectsExcessBytesBeforeEnd()
        {
            using (var host = new HostHarness())
            {
                Guid first = Guid.NewGuid();
                host.Begin(first, new byte[256]);
                host.Chunk(first, 0, new byte[256]);
                host.End(first);
                host.ClearResponses();

                Guid second = Guid.NewGuid();
                host.Begin(second, new byte[1]);
                host.Chunk(second, 0, new byte[64]);

                Assert.That(host.Responses.Length, Is.EqualTo(1),
                    "The excess chunk must fail immediately, without waiting for End.");
                RemoteExecutionProtocol.DecodeCommandResult(host.Responses[0].Payload,
                    out bool succeeded, out string code, out _, out _, out _, out _);
                Assert.That(succeeded, Is.False);
                Assert.That(code, Is.EqualTo("PROTOCOL_ERROR"));
            }
        }

        [TestCase(RemoteMessageKind.CommandInputBegin)]
        [TestCase(RemoteMessageKind.CommandInputChunk)]
        [TestCase(RemoteMessageKind.CommandInputEnd)]
        public void CancellingBlockedSendAbortsPartialFrameAndReleasesQueuedOperation(
            RemoteMessageKind kind)
        {
            using (var session = new SessionHarness(false))
            using (var cancellation = new CancellationTokenSource())
            {
                session.Channel.BlockKind = kind;
                Task<RemoteExecutionResult> first = session.Execute(new byte[32], cancellation.Token);
                Task<RemoteExecutionResult> queued = session.Execute();
                cancellation.Cancel();

                Complete(first);
                Complete(queued);
                Assert.That(first.IsCanceled, Is.True);
                Assert.That(queued.IsCanceled || queued.IsFaulted, Is.True);
                Assert.That(session.Channel.Aborted, Is.True,
                    "A partially sent frame must not share its connection with later requests.");
            }
        }

        [Test]
        public void CancellingQueuedOperationDoesNotAbortCurrentSend()
        {
            using (var session = new SessionHarness(false))
            using (var cancellation = new CancellationTokenSource())
            {
                session.Channel.BlockKind = RemoteMessageKind.CommandInputBegin;
                Task<RemoteExecutionResult> first = session.Execute();
                Task<RemoteExecutionResult> queued = session.Execute(null, cancellation.Token);
                cancellation.Cancel();
                Complete(queued);
                Assert.That(queued.IsCanceled, Is.True);
                Assert.That(first.IsCompleted, Is.False);
                Assert.That(session.Channel.Aborted, Is.False);
            }
        }

        [Test]
        public void CommandDeadlineIncludesBlockedSend()
        {
            using (var session = new SessionHarness(false))
            {
                session.Channel.BlockKind = RemoteMessageKind.CommandInputBegin;
                Task<RemoteExecutionResult> operation = session.Execute();
                Complete(operation, 9);
                Assert.Throws<TimeoutException>(() => operation.GetAwaiter().GetResult());
                Assert.That(session.Channel.Aborted, Is.True);
            }
        }

        [Test]
        public void CancellingWhileWaitingForResponsePreservesConnection()
        {
            TestCommand<int>.Mode = "waitForCancellation";
            using (var session = new SessionHarness(true))
            using (var cancellation = new CancellationTokenSource())
            {
                Task<RemoteExecutionResult> operation = session.Execute(null, cancellation.Token);
                cancellation.Cancel();
                Complete(operation);
                Assert.That(operation.IsCanceled, Is.True);
                Assert.That(session.Channel.Aborted, Is.False,
                    "Completed frames do not require retiring the connection.");
            }
        }

        [Test]
        public void CancellingBlockedCatalogSendTerminatesRefresh()
        {
            using (var session = new SessionHarness(false))
            using (var cancellation = new CancellationTokenSource())
            {
                session.Channel.BlockKind = RemoteMessageKind.ListCommands;
                Task operation = session.Refresh(cancellation.Token);
                cancellation.Cancel();
                Complete(operation);
                Assert.That(operation.IsCanceled, Is.True);
                Assert.That(session.Channel.Aborted, Is.True);
            }
        }

        [Test]
        public void BlockedCancellationFrameHasItsOwnSendDeadline()
        {
            TestCommand<int>.Mode = "waitForCancellation";
            using (var session = new SessionHarness(true))
            using (var cancellation = new CancellationTokenSource())
            {
                session.Channel.BlockKind = RemoteMessageKind.CancelCommand;
                Task<RemoteExecutionResult> operation = session.Execute(null, cancellation.Token);
                cancellation.Cancel();
                Complete(operation);
                Assert.That(operation.IsCanceled, Is.True);
                Assert.That(SpinWait.SpinUntil(() => session.Channel.Aborted,
                    TimeSpan.FromSeconds(12)), Is.True,
                    "A control frame must not hold the send lock indefinitely.");
            }
        }

        [Test]
        public void AsynchronousCommandAlsoTimesOutWithoutDriverUpdate()
        {
            TestCommand<int>.Mode = "waitForCancellation";
            using (var session = new SessionHarness(true))
                Assert.That(Result(session.Execute()).Code, Is.EqualTo("COMMAND_TIMED_OUT"));
        }

        [TestCase("hash")]
        [TestCase("short")]
        [TestCase("offset")]
        public void InvalidRequestResetsReceiverForNextRequest(string corruption)
        {
            using (var host = new HostHarness())
            {
                Guid id = Guid.NewGuid();
                host.Begin(id, new byte[2]);
                host.Chunk(id, corruption == "offset" ? 1 : 0,
                    corruption == "short" ? new byte[1] : new byte[] { 1, 0 });
                if (corruption != "offset") host.End(id);
                Assert.That(ResponseCode(host.Responses[0]), Is.EqualTo("PROTOCOL_ERROR"));

                host.ClearResponses();
                id = Guid.NewGuid();
                host.Begin(id, new byte[2]);
                host.Chunk(id, 0, new byte[2]);
                host.End(id);
                Assert.That(ResponseCode(host.Responses[0]), Is.Empty);
            }
        }

        [TestCase("hash")]
        [TestCase("offset")]
        [TestCase("excess")]
        public void InvalidResponseResetsReceiverForNextOperation(string corruption)
        {
            using (var session = new SessionHarness(true))
            {
                session.TransformResponse = frame =>
                {
                    if (frame.Kind != RemoteMessageKind.CommandResultChunk) return frame;
                    var data = new byte[corruption == "excess" ? 33 : 32];
                    if (corruption == "hash") data[0] = 1;
                    return new RemoteFrame(frame.Kind, frame.RequestId,
                        RemoteExecutionProtocol.EncodeCommandResultChunk(
                            corruption == "offset" ? 1 : 0, data, 0, data.Length));
                };
                Assert.Throws<InvalidDataException>(() => Result(session.Execute(new byte[32])));
                session.TransformResponse = null;
                Assert.That(Result(session.Execute(new byte[32])).Succeeded, Is.True);
            }
        }

        [Test]
        public void OldCommandKeepsExecutionSlotUntilItFinishesAfterReconnect()
        {
            TestCommand<int>.Mode = "deferred";
            TestCommand<int>.Deferred = new TaskCompletionSource<RemoteCommandResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (var host = new HostHarness())
            {
                Guid oldId = Guid.NewGuid();
                host.Begin(oldId, Array.Empty<byte>());
                host.End(oldId);
                host.Reconnect();
                TestCommand<int>.Mode = "echo";
                Guid busyId = Guid.NewGuid();
                host.Begin(busyId, Array.Empty<byte>());
                host.End(busyId);
                Assert.That(ResponseCode(host.Responses[0]), Is.EqualTo("COMMAND_BUSY"));

                TestCommand<int>.Deferred.SetResult(RemoteCommandResult.Success());
                Assert.That(SpinWait.SpinUntil(() =>
                {
                    Guid nextId = Guid.NewGuid();
                    host.Begin(nextId, Array.Empty<byte>());
                    host.End(nextId);
                    return host.Responses.Any(frame => frame.RequestId == nextId &&
                        frame.Kind == RemoteMessageKind.CommandResult && ResponseCode(frame) == "");
                }, TimeSpan.FromSeconds(3)), Is.True);
                Assert.That(host.Responses.Any(frame => frame.RequestId == oldId), Is.False,
                    "An old generation must never publish a result on the replacement connection.");
            }
        }

        private static string ResponseCode(RemoteFrame frame)
        {
            RemoteExecutionProtocol.DecodeCommandResult(frame.Payload, out _, out string code,
                out _, out _, out _, out _);
            return code;
        }

        private static RemoteExecutionResult Result(Task<RemoteExecutionResult> task)
        {
            Complete(task);
            return task.GetAwaiter().GetResult();
        }

        private static void Complete(Task task, int seconds = 3)
        {
            Assert.That(SpinWait.SpinUntil(() => task.IsCompleted,
                TimeSpan.FromSeconds(seconds)), Is.True, "Operation did not terminate.");
        }
    }

    // Open generic types are excluded from production command discovery.
    public sealed class TestCommand<T> : IRemoteCommand
    {
        public static string Mode;
        public static bool CancellationObserved;
        public static TaskCompletionSource<RemoteCommandResult> Deferred;
        public string Name => "Test command";
        public string Description => "Isolated regression command";
        public string Category => "Tests";
        public int TimeoutSeconds => 1;
        public string RequestContentType => "";
        public string ResponseContentType => "application/octet-stream";

        public Task<RemoteCommandResult> ExecuteAsync(RemoteCommandContext context,
            CancellationToken cancellationToken)
        {
            if (Mode == "deferred") return Deferred.Task;
            if (Mode == "exception") throw new InvalidOperationException("test failure");
            if (Mode == "failure")
                return Task.FromResult(RemoteCommandResult.Failure("BUSINESS_FAILURE", "test failure"));
            if (Mode == "wrongFailureType")
                return Task.FromResult(RemoteCommandResult.Failure("BUSINESS_FAILURE",
                    "test failure", new byte[1], "text/plain"));
            if (Mode == "wrongSuccessType")
                return Task.FromResult(RemoteCommandResult.Success("", new byte[1], "text/plain"));
            if (Mode == "waitForCancellation") return WaitForCancellation(cancellationToken);
            if (Mode == "synchronousTimeout")
            {
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 1500)
                {
                    CancellationObserved |= cancellationToken.IsCancellationRequested;
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.SpinWait(128);
                }
            }
            return Task.FromResult(RemoteCommandResult.Success("", context.Payload,
                ResponseContentType));
        }

        private static async Task<RemoteCommandResult> WaitForCancellation(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            throw new InvalidOperationException("Cancellation should end the command.");
        }
    }

    internal static class TestReflection
    {
        internal const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic;
        internal static Type RuntimeType(string name) =>
            typeof(RemoteExecutionPlayerApi).Assembly.GetType("RemoteExecution." + name, true);

        internal static object Call(object target, string method, params object[] args)
        {
            try { return target.GetType().GetMethod(method, Instance).Invoke(target, args); }
            catch (TargetInvocationException exception)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        internal static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, Instance).SetValue(target, value);
    }

    internal sealed class HostHarness : IDisposable
    {
        private readonly object m_Host;
        private readonly object m_Catalog;
        private readonly object m_Configuration;
        private readonly Action<RemoteMessageKind, Guid, byte[]> m_Send;
        private long m_Generation = 1;
        private readonly List<RemoteFrame> m_Responses = new List<RemoteFrame>();
        internal Action<RemoteFrame> OnResponse;
        internal RemoteFrame[] Responses { get { lock (m_Responses) return m_Responses.ToArray(); } }

        internal HostHarness()
        {
            Type catalogType = TestReflection.RuntimeType("RemoteCommandCatalog");
            m_Catalog = catalogType.GetMethod("Discover", BindingFlags.Static |
                BindingFlags.NonPublic).Invoke(null, new object[] { new[] { typeof(TestCommand<int>) } });
            m_Configuration = Activator.CreateInstance(
                TestReflection.RuntimeType("RemoteExecutionPlayerConfiguration"),
                TestReflection.Instance, null, new object[] { null, "test", "test",
                    2 * 1024 * 1024, 2 * 1024 * 1024, TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1), "test", "test" }, null);
            m_Host = Activator.CreateInstance(TestReflection.RuntimeType("RemoteExecutionPlayerCommandHost"), true);
            TestReflection.Set(m_Host, "m_Catalog", m_Catalog);
            m_Send = (kind, id, payload) =>
            {
                var frame = new RemoteFrame(kind, id, payload);
                lock (m_Responses) m_Responses.Add(frame);
                OnResponse?.Invoke(frame);
            };
            TestReflection.Call(m_Host, "BeginConnection", m_Generation, m_Configuration, m_Send);
        }

        internal void Reconnect()
        {
            TestReflection.Call(m_Host, "CancelConnection", m_Generation);
            TestReflection.Set(m_Host, "m_Catalog", m_Catalog);
            TestReflection.Call(m_Host, "BeginConnection", ++m_Generation, m_Configuration, m_Send);
        }
        internal void Handle(RemoteFrame frame) => TestReflection.Call(m_Host, "HandleFrame", m_Generation, frame);
        internal void ClearResponses() { lock (m_Responses) m_Responses.Clear(); }
        internal void Begin(Guid id, byte[] payload)
        {
            using (var sha = SHA256.Create())
                Handle(new RemoteFrame(RemoteMessageKind.CommandInputBegin, id,
                    RemoteExecutionProtocol.EncodeCommandInputBegin(typeof(TestCommand<int>).FullName,
                        "", payload.Length, sha.ComputeHash(payload))));
        }
        internal void Chunk(Guid id, long offset, byte[] payload) =>
            Handle(new RemoteFrame(RemoteMessageKind.CommandInputChunk, id,
                RemoteExecutionProtocol.EncodeCommandChunk(offset, payload, 0, payload.Length)));
        internal void End(Guid id) => Handle(new RemoteFrame(RemoteMessageKind.CommandInputEnd,
            id, RemoteExecutionProtocol.EncodeCommandEnd()));
        public void Dispose() => ((IDisposable)m_Host).Dispose();
    }

    internal sealed class SessionHarness : IDisposable
    {
        private readonly object m_Session;
        private readonly List<Task> m_Tasks = new List<Task>();
        internal readonly TestChannel Channel = new TestChannel();
        internal readonly HostHarness Host;
        internal Func<RemoteFrame, RemoteFrame> TransformResponse;

        internal SessionHarness(bool connectHost)
        {
            Type type = typeof(RemoteExecutionEditorApi).Assembly.GetType(
                "RemoteExecution.RemoteExecutionServer+ClientSession", true);
            m_Session = Activator.CreateInstance(type, TestReflection.Instance, null,
                new object[] { -991, Channel, TimeSpan.FromSeconds(1) }, null);
            TestReflection.Set(m_Session, "m_IsReady", true);
            TestReflection.Call(m_Session, "UpdateCommands", new object[] { new[] {
                new RemoteCommandInfo { TypeName = typeof(TestCommand<int>).FullName,
                    Name = "Test", Description = "Test", Category = "Tests", TimeoutSeconds = 1,
                    RequestContentType = "", ResponseContentType = "application/octet-stream", Executable = true }
            } });
            if (connectHost)
            {
                Host = new HostHarness();
                Channel.OnSend = Host.Handle;
                Host.OnResponse = frame => TestReflection.Call(m_Session, "HandleResponse",
                    TransformResponse?.Invoke(frame) ?? frame);
            }
        }

        internal Task<RemoteExecutionResult> Execute(byte[] payload = null,
            CancellationToken token = default)
        {
            var task = (Task<RemoteExecutionResult>)TestReflection.Call(m_Session,
                "ExecuteCommandAsync", typeof(TestCommand<int>).FullName,
                payload ?? Array.Empty<byte>(), "", token);
            m_Tasks.Add(task);
            return task;
        }

        internal Task Refresh(CancellationToken token)
        {
            var task = (Task)TestReflection.Call(m_Session, "RefreshCommandsAsync", token);
            m_Tasks.Add(task);
            return task;
        }

        public void Dispose()
        {
            Host?.Dispose();
            ((IDisposable)m_Session).Dispose();
            foreach (Task task in m_Tasks)
            {
                try { task.Wait(1000); }
                catch (AggregateException) { }
                _ = task.Exception;
            }
        }
    }

    internal sealed class TestChannel : IRemoteExecutionChannel
    {
        private readonly TaskCompletionSource<bool> m_BlockedSend = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration m_Registration;
        internal Action<RemoteFrame> OnSend;
        internal RemoteMessageKind? BlockKind;
        internal Guid RequestId;
        internal bool Aborted;

        public Task SendAsync(RemoteFrame frame, CancellationToken token)
        {
            if (frame.Kind == RemoteMessageKind.CommandInputBegin) RequestId = frame.RequestId;
            if (frame.Kind == BlockKind)
            {
                m_Registration = token.Register(() => m_BlockedSend.TrySetCanceled());
                return m_BlockedSend.Task;
            }
            OnSend?.Invoke(frame);
            return Task.CompletedTask;
        }
        public Task<RemoteFrame> ReceiveAsync(CancellationToken token) =>
            Task.FromException<RemoteFrame>(new InvalidOperationException("Test does not receive frames."));
        public void Abort() { Aborted = true; m_BlockedSend.TrySetCanceled(); }
        public void Dispose() { Abort(); m_Registration.Dispose(); }
    }
}
