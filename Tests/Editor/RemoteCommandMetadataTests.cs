using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace RemoteExecution.Tests
{
    public sealed class RemoteCommandMetadataTests
    {
        [Test]
        public void DiscoveryReadsAndValidatesTheSameMetadataSnapshot()
        {
            ChangingMetadataCommand<int>.NameReads = 0;
            Type catalogType = TestReflection.RuntimeType("RemoteCommandCatalog");
            object catalog = catalogType.GetMethod("Discover", BindingFlags.Static |
                BindingFlags.NonPublic).Invoke(null,
                    new object[] { new[] { typeof(ChangingMetadataCommand<int>) } });
            var infos = (RemoteCommandInfo[])TestReflection.Call(catalog, "EncodeInfos");
            Assert.That(ChangingMetadataCommand<int>.NameReads, Is.EqualTo(1));
            Assert.That(infos[0].Name, Is.EqualTo("First read"));
            Assert.That(infos[0].Category, Is.Empty);
            Assert.DoesNotThrow(() => RemoteExecutionProtocol.EncodeCommands(infos));

            infos[0].Name = "Mutated external DTO";
            var freshInfos = (RemoteCommandInfo[])TestReflection.Call(catalog, "EncodeInfos");
            Assert.That(freshInfos[0].Name, Is.EqualTo("First read"));
        }

        [TestCase("", "Description")]
        [TestCase("Name", " ")]
        public void WireMetadataUsesTheSameRequiredTextRules(string name, string description)
        {
            var info = new RemoteCommandInfo
            {
                TypeName = "Example.Command", Name = name, Description = description,
                Category = "", TimeoutSeconds = 1, RequestContentType = "", ResponseContentType = ""
            };
            Assert.Throws<System.IO.InvalidDataException>(() =>
                RemoteExecutionProtocol.EncodeCommands(new[] { info }));
        }
    }

    public sealed class ChangingMetadataCommand<T> : IRemoteCommand
    {
        public static int NameReads;
        public string Name => ++NameReads == 1 ? "First read" : " ";
        public string Description => "Description";
        public string Category => null;
        public int TimeoutSeconds => 1;
        public string RequestContentType => null;
        public string ResponseContentType => null;
        public Task<RemoteCommandResult> ExecuteAsync(RemoteCommandContext context,
            CancellationToken cancellationToken) => Task.FromResult(RemoteCommandResult.Success());
    }
}
