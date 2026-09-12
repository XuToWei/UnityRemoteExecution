using System;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace RemoteExecution.Tests
{
    public sealed class RemoteLogSearchTests
    {
        private static readonly UTF8Encoding s_Utf8 = new UTF8Encoding(false, true);

        [Test]
        public void CommandSearchesMessagesAndStacksNewestFirst()
        {
            Type buffer = TestReflection.RuntimeType("RemoteLogBuffer");
            InvokeStatic(buffer, "Reset");
            string needle = "Needle-" + Guid.NewGuid().ToString("N");
            InvokeStatic(buffer, "Capture", needle + " first", "first stack",
                LogType.Log);
            InvokeStatic(buffer, "Capture", "stack match", needle + " stack",
                LogType.Warning);
            InvokeStatic(buffer, "Capture", needle + " newest", "newest stack",
                LogType.Error);

            RemoteCommandResult result = Execute(Encoding.UTF8.GetBytes(
                needle.ToLowerInvariant()));
            string output = s_Utf8.GetString(result.Payload);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.ContentType, Is.EqualTo(SearchPlayerLogsCommand.ContentType));
            Assert.That(output, Does.Contain("Matched 3 logs"));
            Assert.That(output, Does.Contain(needle + " stack"));
            Assert.That(output.IndexOf("newest", StringComparison.Ordinal),
                Is.LessThan(output.IndexOf("first", StringComparison.Ordinal)));
        }

        [Test]
        public void CommandRejectsOversizedQueries()
        {
            RemoteCommandResult result = Execute(
                new byte[SearchPlayerLogsCommand.MaxQueryBytes + 1]);
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo("INVALID_LOG_SEARCH"));
        }

        private static RemoteCommandResult Execute(byte[] payload)
        {
            var context = (RemoteCommandContext)Activator.CreateInstance(
                typeof(RemoteCommandContext), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[]
                {
                    typeof(SearchPlayerLogsCommand).FullName, "Tests", payload,
                    SearchPlayerLogsCommand.ContentType, CancellationToken.None
                }, null);
            return new SearchPlayerLogsCommand().ExecuteAsync(context,
                CancellationToken.None).GetAwaiter().GetResult();
        }

        private static object InvokeStatic(Type type, string method,
            params object[] arguments)
        {
            return type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic |
                BindingFlags.Public).Invoke(null, arguments);
        }
    }
}
