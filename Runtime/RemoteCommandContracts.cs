using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Scripting;

namespace RemoteExecution
{
    [RequireImplementors]
    public interface IRemoteCommand
    {
        string Name { get; }
        string Description { get; }
        string Category { get; }
        int TimeoutSeconds { get; }
        string RequestContentType { get; }
        string ResponseContentType { get; }

        Task<RemoteCommandResult> ExecuteAsync(
            RemoteCommandContext context, CancellationToken cancellationToken);
    }

    internal sealed class RemoteCommandDescriptor
    {
        internal RemoteCommandDescriptor(Type commandType, IRemoteCommand command)
        {
            CommandType = commandType ?? throw new ArgumentNullException(nameof(commandType));
            Command = command ?? throw new ArgumentNullException(nameof(command));
            TypeName = commandType.FullName ?? throw new InvalidOperationException(
                "Remote command type must have a full name.");
            Name = command.Name;
            Description = command.Description;
            Category = command.Category ?? string.Empty;
            TimeoutSeconds = command.TimeoutSeconds;
            RequestContentType = command.RequestContentType ?? string.Empty;
            ResponseContentType = command.ResponseContentType ?? string.Empty;
        }

        internal Type CommandType { get; }
        internal IRemoteCommand Command { get; }
        internal string TypeName { get; }
        internal string Name { get; }
        internal string Description { get; }
        internal string Category { get; }
        internal int TimeoutSeconds { get; }
        internal string RequestContentType { get; }
        internal string ResponseContentType { get; }
        internal bool IsExecutable => Command != null;
    }

    public sealed class RemoteCommandContext
    {
        internal RemoteCommandContext(string commandType, string clientId, byte[] payload,
            string contentType, CancellationToken cancellationToken)
        {
            CommandType = commandType;
            ClientId = clientId;
            Payload = payload ?? Array.Empty<byte>();
            ContentType = contentType ?? string.Empty;
            CancellationToken = cancellationToken;
        }

        public string CommandType { get; }
        public string ClientId { get; }
        public byte[] Payload { get; }
        public string ContentType { get; }
        public CancellationToken CancellationToken { get; }
    }

    public sealed class RemoteCommandResult
    {
        private RemoteCommandResult(bool succeeded, string code, string message,
            byte[] payload, string contentType)
        {
            Succeeded = succeeded;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Payload = payload == null || payload.Length == 0
                ? Array.Empty<byte>() : (byte[])payload.Clone();
            ContentType = contentType ?? string.Empty;
        }

        public bool Succeeded { get; }
        public string Code { get; }
        public string Message { get; }
        public byte[] Payload { get; }
        public string ContentType { get; }

        public static RemoteCommandResult Success(string message = "", byte[] payload = null,
            string contentType = "")
        {
            return new RemoteCommandResult(true, string.Empty, message, payload, contentType);
        }

        public static RemoteCommandResult Failure(string code, string message,
            byte[] payload = null, string contentType = "")
        {
            return new RemoteCommandResult(false, code, message, payload, contentType);
        }
    }
}
