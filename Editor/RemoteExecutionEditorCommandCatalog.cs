using System;
using System.Linq;
using UnityEditor;

namespace RemoteExecution
{
    internal static class RemoteExecutionEditorCommandCatalog
    {
        private static readonly object s_Lock = new object();
        private static RemoteCommandCatalog s_Catalog;

        internal static void Discover()
        {
            var types = TypeCache.GetTypesDerivedFrom<IRemoteCommand>()
                .Where(RemoteCommandCatalog.IsCommandType)
                .ToArray();
            RemoteCommandCatalog catalog = RemoteCommandCatalog.Discover(types);
            lock (s_Lock) s_Catalog = catalog;
        }

        internal static void Clear()
        {
            lock (s_Lock) s_Catalog = null;
        }

        internal static RemoteCommandDescriptor GetCommand<TCommand>()
            where TCommand : class, IRemoteCommand
        {
            RemoteCommandCatalog catalog;
            lock (s_Lock) catalog = s_Catalog;
            if (catalog == null) throw new InvalidOperationException(
                "Remote command discovery has not run. Start the Editor server first.");
            if (!catalog.TryGet(typeof(TCommand), out RemoteCommandDescriptor descriptor))
                throw new InvalidOperationException(
                    $"Remote command type was not discovered: {RemoteCommandCatalog.GetTypeIdentity(typeof(TCommand))}");
            return descriptor;
        }
    }
}
