using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace RemoteExecution.Tests
{
    public sealed class RemoteExecutionProtocolTests
    {
        [Test]
        public void FrameMatchesTheDocumentedWireFormat()
        {
            var id = new Guid("00112233-4455-6677-8899-aabbccddeeff");
            var frame = new RemoteFrame(RemoteMessageKind.CommandInputChunk, id,
                new byte[] { 1, 2, 3 });
            byte[] expected =
            {
                0x55, 0x52, 0x45, 0x58, 0x09,
                0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66,
                0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
                0x03, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03
            };
            Assert.That(RemoteExecutionProtocol.EncodeFrame(frame), Is.EqualTo(expected));
            AssertFrame(RemoteExecutionProtocol.DecodeFrame(expected), frame);
        }

        [Test]
        public void ConsecutiveFramesRoundTripAcrossPartialStreamReads()
        {
            var first = new RemoteFrame(RemoteMessageKind.CommandInputChunk, Guid.NewGuid(),
                new byte[] { 1, 2, 3, 4 });
            var second = new RemoteFrame(RemoteMessageKind.Ping, Guid.Empty, Array.Empty<byte>());
            using (var output = new MemoryStream())
            {
                RemoteExecutionProtocol.WriteFrameAsync(output, first, CancellationToken.None)
                    .GetAwaiter().GetResult();
                RemoteExecutionProtocol.WriteFrameAsync(output, second, CancellationToken.None)
                    .GetAwaiter().GetResult();
                using (var input = new PartialReadStream(output.ToArray()))
                {
                    AssertFrame(RemoteExecutionProtocol.ReadFrameAsync(input, CancellationToken.None)
                        .GetAwaiter().GetResult(), first);
                    AssertFrame(RemoteExecutionProtocol.ReadFrameAsync(input, CancellationToken.None)
                        .GetAwaiter().GetResult(), second);
                    Assert.That(input.Position, Is.EqualTo(input.Length));
                }
            }
        }

        [TestCase("marker")]
        [TestCase("kind")]
        [TestCase("length")]
        [TestCase("truncated")]
        [TestCase("trailing")]
        public void InvalidFramesAreRejected(string corruption)
        {
            byte[] bytes = RemoteExecutionProtocol.EncodeFrame(new RemoteFrame(
                RemoteMessageKind.CommandInputChunk, Guid.NewGuid(), new byte[] { 1, 2, 3 }));
            switch (corruption)
            {
                case "marker": bytes[0] = 0; break;
                case "kind": bytes[4] = byte.MaxValue; break;
                case "length":
                    for (int i = 21; i < 25; i++) bytes[i] = byte.MaxValue;
                    break;
                case "truncated": Array.Resize(ref bytes, bytes.Length - 1); break;
                case "trailing": Array.Resize(ref bytes, bytes.Length + 1); break;
            }
            Assert.Throws<InvalidDataException>(() => RemoteExecutionProtocol.DecodeFrame(bytes));
        }

        [Test]
        public void HelloContainsOnlyClientMetadata()
        {
            var hello = new RemoteHello { ClientId = "client", Target = "target", UnityVersion = "unity" };
            byte[] bytes = RemoteExecutionProtocol.EncodeHello(hello);
            Assert.That(bytes, Is.EqualTo(Encoding.UTF8.GetBytes(
                "\u0006\0client\u0006\0target\u0005\0unity")));
            RemoteHello decoded = RemoteExecutionProtocol.DecodeHello(bytes);
            Assert.That(decoded.ClientId, Is.EqualTo(hello.ClientId));
            Assert.That(decoded.Target, Is.EqualTo(hello.Target));
            Assert.That(decoded.UnityVersion, Is.EqualTo(hello.UnityVersion));
        }

        private static void AssertFrame(RemoteFrame actual, RemoteFrame expected)
        {
            Assert.That(actual.Kind, Is.EqualTo(expected.Kind));
            Assert.That(actual.RequestId, Is.EqualTo(expected.RequestId));
            Assert.That(actual.Payload, Is.EqualTo(expected.Payload));
        }

        private sealed class PartialReadStream : MemoryStream
        {
            internal PartialReadStream(byte[] bytes) : base(bytes, false) { }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken) =>
                base.ReadAsync(buffer, offset, Math.Min(count, 3), cancellationToken);
        }
    }
}
