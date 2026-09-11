using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace RemoteExecution
{
    internal sealed class RemoteCommandCatalog
    {
        private readonly Dictionary<string, RemoteCommandDescriptor> m_ByType;

        private RemoteCommandCatalog(IReadOnlyList<RemoteCommandDescriptor> descriptors)
        {
            Descriptors = descriptors;
            m_ByType = descriptors.ToDictionary(item => item.TypeName,
                StringComparer.Ordinal);
        }

        internal IReadOnlyList<RemoteCommandDescriptor> Descriptors { get; }

        internal static RemoteCommandCatalog Discover(IEnumerable<Type> candidateTypes)
        {
            if (candidateTypes == null) throw new ArgumentNullException(nameof(candidateTypes));
            Type[] types = candidateTypes.Where(IsCommandType)
                .Distinct()
                .OrderBy(type => type.Assembly.FullName, StringComparer.Ordinal)
                .ThenBy(type => type.FullName ?? type.Name, StringComparer.Ordinal)
                .ToArray();
            var descriptors = new List<RemoteCommandDescriptor>(types.Length);
            foreach (Type type in types)
            {
                try
                {
                    var command = (IRemoteCommand)Activator.CreateInstance(type);
                    if (command == null)
                        throw new InvalidOperationException("Command constructor returned no instance.");
                    descriptors.Add(new RemoteCommandDescriptor(type, command));
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Remote command '{GetTypeIdentity(type)}' could not be discovered: {exception.GetBaseException().Message}",
                        exception);
                }
            }

            var duplicate = descriptors.GroupBy(item => item.TypeName,
                    StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
                throw new InvalidOperationException(
                    $"Remote command type '{duplicate.Key}' is duplicated.");
            return new RemoteCommandCatalog(descriptors.OrderBy(item => item.TypeName,
                StringComparer.Ordinal).ToArray());
        }

        internal static bool IsCommandType(Type type)
        {
            return type != null && type.IsClass && !type.IsAbstract &&
                !type.ContainsGenericParameters &&
                typeof(IRemoteCommand).IsAssignableFrom(type) &&
                !typeof(UnityEngine.Object).IsAssignableFrom(type) &&
                type.FullName != null &&
                type.GetConstructor(BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null) != null;
        }

        internal bool TryGet(string typeName, out RemoteCommandDescriptor descriptor)
        {
            return m_ByType.TryGetValue(typeName, out descriptor);
        }

        internal bool TryGet(Type type, out RemoteCommandDescriptor descriptor)
        {
            descriptor = null;
            return type != null && m_ByType.TryGetValue(type.FullName, out descriptor);
        }

        internal async Task<RemoteCommandResult> ExecuteAsync(RemoteCommandDescriptor descriptor,
            RemoteCommandContext context, CancellationToken cancellationToken)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (context == null) throw new ArgumentNullException(nameof(context));
            cancellationToken.ThrowIfCancellationRequested();
            if (context.CancellationToken != cancellationToken)
                throw new InvalidOperationException(
                    "Command context cancellation token does not match the execution token.");
            if (context.Payload.Length > RemoteExecutionProtocol.MaxCommandRequestBytes)
                throw new InvalidDataException(
                    $"Command request exceeds {RemoteExecutionProtocol.MaxCommandRequestBytes} bytes.");
            RemoteCommandResult result = await descriptor.Command.ExecuteAsync(
                context, cancellationToken).ConfigureAwait(false);
            if (result == null)
                throw new InvalidOperationException("Remote command returned no result.");
            if (result.Payload.Length > RemoteExecutionProtocol.MaxCommandResponseBytes)
                throw new InvalidDataException(
                    $"Command response exceeds {RemoteExecutionProtocol.MaxCommandResponseBytes} bytes.");
            return result;
        }

        internal RemoteCommandInfo[] EncodeInfos()
        {
            return Descriptors.Select(descriptor => descriptor.ToInfo()).ToArray();
        }

        internal static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            if (assembly == null) return Array.Empty<Type>();
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null);
            }
            catch { return Array.Empty<Type>(); }
        }

        internal static string GetTypeIdentity(Type type)
        {
            return type?.AssemblyQualifiedName ?? type?.FullName ?? type?.Name ?? "<unknown>";
        }

    }
}
