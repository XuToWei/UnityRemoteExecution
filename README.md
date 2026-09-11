# Unity Remote Execution

English · [简体中文](README.zh-CN.md)

Unity Remote Execution is a bridge between the Unity Editor and a Player, intended for development workflows. TCP is bundled, while business code can provide WebSocket or another reliable ordered transport. Business code registers named commands, the Editor displays each Player's command catalog, and Editor tools can send bounded binary requests and receive bounded binary results.

The core package has no HybridCLR dependency. HybridCLR compilation and runtime assembly loading are a conditional adapter implemented with the same generic command API.

## Installation

In a Unity 2021.3 or newer project, open **Window > Package Manager**, click **+**, select **Add package from git URL...**, and paste:

```text
https://github.com/XuToWei/UnityRemoteExecution.git
```

## Player startup

The package does not add anything to a scene automatically and does not connect automatically. Start and stop the Player client from your own bootstrap or development UI:

```csharp
using RemoteExecution;
using UnityEngine;

public sealed class RemoteExecutionControls : MonoBehaviour
{
    private void OnEnable()
    {
        RemoteExecutionPlayerApi.ConnectionStateChanged += OnConnectionStateChanged;
    }

    private void OnDisable()
    {
        RemoteExecutionPlayerApi.ConnectionStateChanged -= OnConnectionStateChanged;
    }

    public void Connect()
    {
        RemoteExecutionPlayerApi.Start("192.168.1.20", 38421, "Test Device");
    }

    public void Disconnect()
    {
        RemoteExecutionPlayerApi.Stop();
    }

    private static void OnConnectionStateChanged(RemoteExecutionConnectionState state)
    {
        Debug.Log($"Remote Execution: {state}");
        if (state == RemoteExecutionConnectionState.Faulted)
        {
            RemoteExecutionConnectionError error = RemoteExecutionPlayerApi.LastError;
            Debug.LogWarning($"[{error.Code}] {error.Message}");
        }
    }
}
```

`ConnectionState` reports `Disconnected`, `Connecting`, `Handshaking`, `Connected`, or `Faulted`; `IsConnected` is true only after the Editor acknowledges the protocol handshake. State-change callbacks run on Unity's main thread. `LastError` contains a stable code and message while faulted.

`Start` validates the transport, client ID, timeouts, and optional transfer limits synchronously. The host/port overload and a missing custom transport use the bundled TCP transport. Calling it again with the same parameters while active does nothing; calling it after a fault retries, and calling it with different parameters replaces the current connection. There is no automatic reconnect, so the business layer controls retry timing and UI. `Stop` is safe to call repeatedly.

The Player API is unavailable in Editor Play Mode. Start the Editor listener from **Window > Remote Execution**, then call `RemoteExecutionPlayerApi.Start` from a built Player. No `RemoteExecutionComponent` or settings asset is required.


### Optional runtime connection UI

For a ready-to-use development panel, create `RemoteExecutionPlayerConnectionUI` in your own runtime code and call its `OnGUI()` from your own `MonoBehaviour.OnGUI()`. It is an optional plain C# helper: it does not create a GameObject, subscribe to lifecycle callbacks, connect by itself, or replace the static API or your own connection UI.

```csharp
using RemoteExecution;
using UnityEngine;

public sealed class RemoteExecutionControls : MonoBehaviour
{
    private readonly RemoteExecutionPlayerConnectionUI m_ConnectionUI =
        new RemoteExecutionPlayerConnectionUI();

    private void OnGUI()
    {
        m_ConnectionUI.OnGUI();
    }
}
```

The helper exposes TCP host, port, client ID, `ShowUI`, and `Area` properties. Its Connect/Retry and Stop/Disconnect buttons call `RemoteExecutionPlayerApi.Start` and `Stop`, and it displays the current state and fault details. The caller controls when the helper is created, shown, enabled, and destroyed; it never stops the global connection implicitly.

Custom transports remain available. Assign an `IRemoteExecutionPlayerOptionsProvider` to `OptionsProvider`, or call `Connect(RemoteExecutionPlayerOptions)` from your own code:

```csharp
public sealed class CustomPlayerOptions : IRemoteExecutionPlayerOptionsProvider
{
    public RemoteExecutionPlayerOptions CreateOptions()
    {
        return new RemoteExecutionPlayerOptions(
            GameWebSocketTransport.CreateClient("wss://dev.example/remote"),
            "Test Device");
    }
}

// Configure once, then let the caller invoke m_ConnectionUI.OnGUI().
m_ConnectionUI.OptionsProvider = new CustomPlayerOptions();
```

A provider must return a new options object and a new transport instance for every connection attempt. The Player API owns and disposes the transport; the helper must not dispose it. Assigning a provider preserves custom connection behavior instead of silently replacing it with TCP. Use the static API directly when the application needs complete control over connection timing or UI.


The package does not restrict which Player build types may connect. The Editor bind address defaults to `127.0.0.1`, and `Start` commonly uses the same host for local connections. When TCP listens on a loopback address such as `127.0.0.1`, the top-right connection status appends a local IPv4 address after the actual listening endpoint, preferring adapters with a gateway. Hover over the status to see other local addresses. Addresses refresh when the window opens, regains focus, or starts listening. **Reset** beside the bind address restores `127.0.0.1`. For LAN use, bind the Editor to a reachable local interface (or `0.0.0.0`), pass the Editor machine's actual LAN address to the Player, and allow the port through the firewall. `0.0.0.0` is a bind address, not a valid Player destination.

The core protocol does not authenticate `ClientId`. Bundled TCP has no authentication or encryption. A custom transport may provide TLS or authentication, but the consuming project still defines its trust policy. Use this bridge only in trusted development environments and control its production-build inclusion and enablement.

## Custom transports

By default, the Player `Start(host, port, ...)` overload and the Editor window use TCP. For WebSocket or another protocol, business code implements one `IRemoteExecutionTransport` type and creates separate client and server instances:

```csharp
// Player: a client instance initiates the connection.
var playerTransport = GameWebSocketTransport.CreateClient(
    "wss://dev.example/remote"); // Kind returns "WebSocket".
RemoteExecutionPlayerApi.Start(
    new RemoteExecutionPlayerOptions(playerTransport, "Test Device"));

// Editor: a separate server instance of the same implementation listens.
var editorTransport = GameWebSocketTransport.CreateServer(
    "wss://localhost:9443/remote");
RemoteExecutionEditorApi.StartServer(
    new RemoteExecutionServerOptions(editorTransport));
```

`IRemoteExecutionTransport` defines both connecting and listening so the Player and Editor sides of one protocol cannot drift into separate contracts. Client and server use distinct instances; invoking a method unsupported by the configured role should throw `InvalidOperationException`. The Player/Editor API takes ownership and calls `Dispose()` on stop or replacement; disposal must promptly unblock pending endpoint operations.

Each transport supplies a short, stable `Kind` such as `"TCP"`, `"WebSocket"`, or `"IPC"`. The Editor displays the active server transport's `Kind` through `RemoteExecutionEditorApi.TransportKind`; this value is not reported by a Player. `ConfigurationKey` identifies all connection-relevant settings for repeated Player `Start` comparison and must not contain secrets, while `Description` is endpoint-oriented display text that may change after binding an ephemeral port. Do not derive `Kind` by parsing either value.

`IRemoteExecutionChannel` remains separate because one server transport can accept multiple Players, each requiring independent send/receive operations and lifecycle. Disposing the transport must not close channels it already returned; each session owns its channel.

A channel carries complete `RemoteFrame` values and must provide reliable, lossless, strictly ordered delivery. The package performs at most one send and one receive concurrently. `Abort()` must promptly unblock pending I/O and, like `Dispose()`, be safely repeatable.

For WebSocket, map one complete protocol frame to one binary message: use `RemoteExecutionProtocol.EncodeFrame` for sending and `DecodeFrame` after receiving a complete message. Reassemble WebSocket fragments first and reject text messages. An unordered transport such as UDP must provide ordering, retransmission, duplicate suppression, and connection semantics beneath the channel contract.

## Runtime extension API

Register each named binary command as a concrete `IRemoteCommand` type. The type owns its definition and handler:

```csharp
using System.Threading;
using System.Threading.Tasks;
using RemoteExecution;

public sealed class TableReloadCommand : IRemoteCommand
{
    public string Name => "Reload tables";
    public string Description => "Replace the runtime table data and reload it.";
    public string Category => "Tables";
    public int TimeoutSeconds => 60;
    public string RequestContentType => "application/octet-stream";
    public string ResponseContentType => "application/octet-stream";

    public async Task<RemoteCommandResult> ExecuteAsync(
        RemoteCommandContext context, CancellationToken cancellationToken)
    {
        byte[] response = await ReloadTablesAsync(context.Payload, cancellationToken);
        return RemoteCommandResult.Success(
            "Tables reloaded.", response, "application/octet-stream");
    }

    private static Task<byte[]> ReloadTablesAsync(
        byte[] data, CancellationToken cancellationToken)
    {
        return Task.FromResult(new byte[0]);
    }
}
```

Concrete commands have a public parameterless constructor and are discovered when the Player starts. The Editor discovers the same command types with `TypeCache` when `StartServer` runs; this creates metadata mappings for typed Editor calls and does not execute the handler. The protocol command type is the command class's `FullName`; the type must be present in both Editor and Player-compatible assemblies. Global request/response limits are defined by the protocol and Player connection configuration, not by individual commands. Reflection-discovered command types must be preserved in IL2CPP builds with `[Preserve]` or `Assets/link.xml`.

Use `RemoteExecutionEditorApi.ExecuteCommandAsync<TableReloadCommand>(payload)` to invoke the command. The string-ID API remains available for dynamic commands.

## Editor API

Business Editor tools can query connected Players and execute commands without accessing sockets:

```csharp
RemoteExecutionClientInfo player = RemoteExecutionEditorApi.GetClients()[0];
byte[] tableBytes = BuildTableBytes();

RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync<TableReloadCommand>(
    player.Id, tableBytes);

if (!result.Succeeded)
    UnityEngine.Debug.LogError($"[{result.Code}] {result.Message}");
```

For an input-free command, Editor code can target the Player currently selected on the window's **Commands** tab and await its result directly:

```csharp
RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync<RefreshGameCommand>();
byte[] response = result.Payload;
```

This call sends an empty payload with the request content type declared by `RefreshGameCommand`. The Remote Execution window must remain open with a Player selected. Use the `sessionId` overload when the command needs input or the caller must choose the Player explicitly.

Call `RefreshCommandsAsync(sessionId)` to await a fresh command catalog. `GetClients()` returns read-only snapshots containing the Player target, explicit `IsReady` state, and current command metadata. `ClientId` is self-reported display metadata and is not a security identity. The command catalog remains a capability-negotiation API for Editor panels; the core window does not provide a generic command executor.

## Editor tool panels

Editor integrations implement `IRemoteExecutionEditorPanel`; the core window discovers implementations through Unity `TypeCache`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using RemoteExecution;
using UnityEditor;
using UnityEngine;

public sealed class TableRemoteExecutionPanel : IRemoteExecutionEditorPanel
{
    public string Id => "game.tables";
    public string DisplayName => "Tables";
    public int Order => 50;

    public bool IsAvailable(RemoteExecutionEditorContext context, out string reason)
    {
        reason = context.SelectedPlayer == null ? "Select a Player." : string.Empty;
        return context.SelectedPlayer != null;
    }

    public void DrawGUI(RemoteExecutionEditorContext context)
    {
        if (!GUILayout.Button("Build and reload") || context.IsOperationRunning) return;
        int sessionId = context.SelectedPlayer.Id;
        byte[] bytes = BuildTableBytes();
        context.TryStartOperation("Reloading tables...", async cancellationToken =>
        {
            RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync<TableReloadCommand>(
                sessionId, bytes, cancellationToken);
            if (!result.Succeeded)
                throw new System.InvalidOperationException($"[{result.Code}] {result.Message}");
            return "Tables reloaded.";
        });
    }
}
```

A panel must be a concrete ordinary C# class with a parameterless constructor. Its stable `Id` must be globally unique. `IsAvailable` must have no side effects, and the context is valid only during the current `DrawGUI` call. Capture Player/input values before starting an operation. The host allows one operation per Player across all panels; different Players can run concurrently. Panels needing deterministic cleanup may implement `IDisposable`, and panels needing state across reloads should use `SessionState`, `EditorPrefs`, or `ScriptableSingleton`.

The cancellation-aware `RefreshCommandsAsync` and `ExecuteCommandAsync` overloads accept a `CancellationToken`. Cancellation remains cooperative for Unity compiler calls and Player handlers.

## Optional HybridCLR adapter

When `com.code-philosophy.hybridclr` is installed, the package's `versionDefines` activates the conditional adapter automatically. It adds a **HybridCLR** tool inside **Window > Remote Execution** and the Player command `RemoteExecution.HybridCLR.HybridCLRRemoteExecutionCommand`.

The Remote Execution window is the only Editor entry point. Its **Basic** tab owns connection settings and the connected-Player overview. Its **Commands** tab owns Player/tool selection, per-Player operation state, status, and cancellation; the secondary tool switcher contains HybridCLR and other contributed panels rather than separate windows. Every user-facing remote action supplies its own `IRemoteExecutionEditorPanel`.

The HybridCLR panel provides:

- dynamic `IHybridCLRRemoteExecutionEntry` source compilation into `RemoteExecution.Dynamic`;
- validated DLL/PDB loading followed by interface-entry execution.

Dynamic source example:

```csharp
using System.Threading;
using System.Threading.Tasks;
using RemoteExecution.HybridCLR;

public sealed class RemoteExecutionEntry : IHybridCLRRemoteExecutionEntry
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Handle application work after the assembly has loaded.
        await ReloadGameLogicAsync(cancellationToken);
    }

    private static Task ReloadGameLogicAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

The entry must be a public concrete class with a public parameterless constructor, and a bundle must contain exactly one `IHybridCLRRemoteExecutionEntry` implementation. Exceptions from `ExecuteAsync` are returned to the Editor as `ENTRY_EXECUTION_FAILED`; cancellation uses the normal remote-command cancellation or timeout result.

The panel does not compile or send project hot-update assemblies. Any assemblies referenced by the dynamic source must already be loaded in the Player.

HybridCLR loading is not a core protocol feature. The Editor serializes a HybridCLR-owned envelope and sends it through ordinary generic command input frames. The Player adapter validates the entire envelope and its per-artifact hashes before loading. A bundle must contain exactly one public concrete `IHybridCLRRemoteExecutionEntry` implementation with a public parameterless constructor. After loading, the adapter invokes `Task ExecuteAsync(CancellationToken)` inside the fixed `RemoteExecution.HybridCLR.HybridCLRRemoteExecutionCommand` request. The dynamic entry is not published in the command catalog. Projects remain responsible for their normal HybridCLR AOT metadata configuration.

The complete envelope, including metadata, is limited to 128 MiB and is buffered in memory. Assemblies cannot be unloaded from the Player AppDomain. Reapplying the same name with a different hash, or a failure while loading or resolving the entry, requires restarting the Player. A partial load cannot be rolled back; the adapter rejects later apply attempts to avoid expanding an inconsistent state.

Without HybridCLR, the adapter command and HybridCLR panel are absent. The Commands tab then shows panels contributed by other modules, or an empty state when none are installed.

## Limits and behavior

- Unity 2021.3 or newer.
- The package does not enforce a Development/Debug Player requirement; consuming projects control build inclusion and production enablement.
- The Player never starts or reconnects automatically; business code owns `Start`, retry, and `Stop`.
- Maximum frame payload: 1 MiB.
- Maximum transfer chunk: 60 KiB.
- Hard command request limit: 128 MiB; default business-command request limit: 16 MiB.
- Hard/default command response limits: 64 MiB / 16 MiB.
- Global request/response limits are protocol-wide and may be lowered by the Player connection configuration; commands no longer declare individual size limits.
- Binary input and output include total length and SHA-256, and chunks must be complete and ordered.
- Each Player executes at most one command at a time.
- Command handlers and result processing run on Unity's main thread. Ordinary `await` preserves Unity's synchronization context, including in HybridCLR entries. Handlers that explicitly use `ConfigureAwait(false)` or background work must return to the main thread before accessing Unity APIs; synchronous CPU-heavy handlers block the Unity frame.
- Timeouts and cancellation are cooperative; a handler that ignores cancellation cannot be safely interrupted.
- The core protocol does not authenticate `ClientId`; bundled TCP is unauthenticated and unencrypted, while a custom transport owns any TLS/authentication policy.
- SHA-256 checks detect accidental transfer corruption, but do not authenticate the peer or protect against active tampering.
- Do not expose destructive or production-data handlers.

## Protocol

Editor and Player use the same package release and share one protocol format. Each frame has a 25-byte header: the `UREX` format marker (4 bytes), message kind (1 byte), request ID (16 bytes, in `Guid.ToByteArray()` order), and payload length (4-byte little-endian integer), followed by the payload. The handshake is an unauthenticated `Hello(requestId) → Ready(same requestId)` exchange; `Hello` carries the client ID, target, and Unity version, while `Ready` must have an empty payload.

Command catalog entries use the concrete command type's `FullName`; request/response size limits are global rather than per command. Message numbers are fixed: `Hello=1`, `Ready=2`, `Error=3`, `Ping=4`, `Pong=5`, `ListCommands=6`, `Commands=7`, command input begin/chunk/end `=8..10`, command result metadata/chunk/end `=11..13`, and `CancelCommand=14`. The HybridCLR envelope is an ordinary command payload with its own `HCLR` format marker followed directly by the bundle ID and bundle contents.

## Timeouts and cancellation

Player command deadlines run independently of main-thread updates; synchronous commands must still check their cancellation token. Once an Editor command acquires its execution slot, sending the request and awaiting its result share the command timeout plus a five-second grace period. Catalog refresh has a ten-second budget including sending. Cancelling or timing out during a frame write aborts the connection so later requests cannot continue an incomplete frame. The application can reconnect as needed. Cancelling a queued operation does not abort the active connection.

A failure with no payload and no content type preserves its remote error code. Results carrying a payload must still match the declared response content type. String calls take the full command type name; prefer the generic calls shown above. `RefreshGameCommand` represents an application-defined command with no input.

## Regression tests

Install a Unity Test Framework version compatible with your Editor, refresh assets, and run `RemoteExecution.Tests.Editor` in EditMode. With HybridCLR installed, also run `RemoteExecution.HybridCLR.Tests.Editor` for bundle encoding and decoding. When installed as a Git package, add `com.xw.remote-execution` to the project manifest `testables` array to enable package tests. Tests use isolated in-memory channels without starting real connections or modifying scenes.
