using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RemoteExecution.HybridCLR;

namespace RemoteExecution.Tests
{
    public sealed class HybridCLRBundleCodecTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void BundleRoundTripsWithIdentityDirectlyAfterFormatMarker(bool includeSymbols)
        {
            var bundle = new HybridCLRBundle(new Guid("00112233-4455-6677-8899-aabbccddeeff"),
                "Windows64", new[]
                {
                    new HybridCLRBundleArtifact("Example", new byte[] { 1, 2, 3 },
                        includeSymbols ? new byte[] { 4, 5 } : null)
                });
            byte[] bytes = HybridCLRBundleCodec.Encode(bundle);
            byte[] prefix =
            {
                0x48, 0x43, 0x4c, 0x52,
                0x33, 0x22, 0x11, 0x00, 0x55, 0x44, 0x77, 0x66,
                0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff
            };
            Assert.That(bytes.Take(prefix.Length), Is.EqualTo(prefix));
            HybridCLRBundle decoded = HybridCLRBundleCodec.Decode(bytes);
            Assert.That(decoded.BundleId, Is.EqualTo(bundle.BundleId));
            Assert.That(decoded.Target, Is.EqualTo(bundle.Target));
            Assert.That(decoded.Artifacts.Count, Is.EqualTo(1));
            Assert.That(decoded.Artifacts[0].Name, Is.EqualTo(bundle.Artifacts[0].Name));
            Assert.That(decoded.Artifacts[0].Dll, Is.EqualTo(bundle.Artifacts[0].Dll));
            Assert.That(decoded.Artifacts[0].Pdb, Is.EqualTo(bundle.Artifacts[0].Pdb));
        }

        [TestCase("marker")]
        [TestCase("truncated")]
        [TestCase("hash")]
        [TestCase("trailing")]
        public void InvalidBundlesAreRejected(string corruption)
        {
            byte[] bytes = HybridCLRBundleCodec.Encode(new HybridCLRBundle(Guid.NewGuid(),
                "Windows64", new[] { new HybridCLRBundleArtifact("Example", new byte[] { 1, 2, 3 }) }));
            switch (corruption)
            {
                case "marker": bytes[0] = 0; break;
                case "truncated": Array.Resize(ref bytes, 19); break;
                case "hash": bytes[bytes.Length - 1] ^= 1; break;
                case "trailing": Array.Resize(ref bytes, bytes.Length + 1); break;
            }
            Assert.Throws<InvalidDataException>(() => HybridCLRBundleCodec.Decode(bytes));
        }
    }
}
