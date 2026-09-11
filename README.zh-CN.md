# Unity Remote Execution（简体中文）

[English](README.md) · 简体中文

Unity Remote Execution 是一个面向开发工作流的 Unity Editor 与 Player 远程执行桥接。包内置 TCP，也支持业务层提供 WebSocket 或其他可靠有序传输。业务代码注册命令，Editor 显示每个 Player 的命令目录；Editor 工具可以发送有大小限制的二进制请求并接收二进制结果。

核心包不依赖 HybridCLR。HybridCLR 编译和运行时程序集加载是条件启用的可选适配器，并且同样通过通用远程命令实现。

## 安装

在 Unity 2021.3 或更高版本项目中打开 **Window > Package Manager**，点击左上角的 **+**，选择 **Add package from git URL...**，粘贴以下地址：

```text
https://github.com/XuToWei/UnityRemoteExecution.git
```

## Player 启动

包不会自动向场景添加组件，也不会自动连接。由业务层在自己的启动流程或开发 UI 中启动和停止 Player 客户端：

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

`ConnectionState` 会返回 `Disconnected`、`Connecting`、`Handshaking`、`Connected` 或 `Faulted`；只有 Editor 完成协议握手后，`IsConnected` 才为 `true`。状态回调在 Unity 主线程触发。发生故障时，`LastError` 提供稳定的错误码和消息。

`Start` 会同步校验 transport、客户端 ID、超时和可选传输限制。使用 host/port overload 或未提供自定义 transport 时，包会使用默认 TCP。连接期间使用相同参数重复调用不会产生新连接；故障后再次调用会重试，传入不同参数则替换当前连接。包不会自动重连，重试时机和 UI 完全由业务层控制。`Stop` 可以安全地重复调用。

Player API 不支持 Editor Play Mode。先在 **Window > Remote Execution** 中启动 Editor 监听服务，再由构建后的 Player 调用 `RemoteExecutionPlayerApi.Start`。不需要添加 `RemoteExecutionComponent` 或创建配置资产。


### 可选运行时连接 UI

如果需要开箱即用的开发面板，可以在自己的运行时代码中创建 `RemoteExecutionPlayerConnectionUI`，并从自己的 `MonoBehaviour.OnGUI()` 调用它的 `OnGUI()`。它是一个可选的普通 C# helper：不会创建 GameObject，不会自行订阅生命周期，不会自动连接，也不会替换静态 API 或业务自己的连接 UI。

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

helper 提供 TCP host、port、client ID、`ShowUI` 和 `Area` 属性。Connect/Retry、Stop/Disconnect 按钮会调用 `RemoteExecutionPlayerApi.Start` 和 `Stop`，并显示当前状态和故障信息。helper 的创建、显示、启用和销毁都由调用方控制；它不会隐式停止全局连接。

自定义传输仍然可用。可以给 `OptionsProvider` 赋值 `IRemoteExecutionPlayerOptionsProvider`，也可以由代码直接调用 `Connect(RemoteExecutionPlayerOptions)`：

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

// 配置一次，然后由调用方继续调用 m_ConnectionUI.OnGUI()。
m_ConnectionUI.OptionsProvider = new CustomPlayerOptions();
```

provider 每次连接尝试都必须返回新的 options 对象和新的 transport 实例。Transport 的所有权和释放仍由 Player API 管理，helper 不应自行释放它。指定 provider 可以保留自定义连接行为，不会被静默替换为 TCP。如果业务需要完全控制连接时机或 UI，仍可直接使用静态 API。


包本身不限制可连接的 Player 构建类型。Editor 监听地址默认使用 `127.0.0.1`，本机连接时 `Start` 通常也传入该地址。当 TCP 监听在回环地址（如 `127.0.0.1`）时，窗口右上角的连接状态会在实际监听端点后附上本机 IPv4 地址，优先显示带网关网卡的地址；悬停可查看其他本机地址。地址会在窗口打开、重新获得焦点或启动监听时刷新。监听地址旁的 **Reset** 按钮可还原为 `127.0.0.1`。局域网使用时，应让 Editor 监听可达的本机接口（也可监听 `0.0.0.0`），向 Player 传入 Editor 机器的实际局域网地址，并按需放行防火墙端口。`0.0.0.0` 只能用于监听，不能作为 Player 的目标地址。

核心协议不认证 `ClientId`。默认 TCP 没有认证或加密；自定义传输可以提供 TLS 或认证，但业务层仍应自行定义信任策略。只能在可信开发环境中使用，并由接入项目负责生产构建的包含和启用策略。

## 自定义传输

默认情况下，Player 的 `Start(host, port, ...)` 和 Editor 窗口都使用 TCP。需要 WebSocket 或其他协议时，业务层实现一个 `IRemoteExecutionTransport` 类型，并分别创建客户端和服务端实例：

```csharp
// Player：客户端实例主动连接。
var playerTransport = GameWebSocketTransport.CreateClient(
    "wss://dev.example/remote"); // Kind 返回 "WebSocket"。
RemoteExecutionPlayerApi.Start(
    new RemoteExecutionPlayerOptions(playerTransport, "Test Device"));

// Editor：同一种实现的独立服务端实例负责监听。
var editorTransport = GameWebSocketTransport.CreateServer(
    "wss://localhost:9443/remote");
RemoteExecutionEditorApi.StartServer(
    new RemoteExecutionServerOptions(editorTransport));
```

`IRemoteExecutionTransport` 同时规定主动连接和监听行为，避免同一协议的 Player/Editor 实现不一致。客户端和服务端使用独立实例；调用角色不支持的方法应抛出 `InvalidOperationException`。Player/Editor API 会接管 transport，并在停止或替换时调用 `Dispose()`；释放操作必须及时解除仍在等待的端点操作。

每个 transport 都要提供简短且稳定的 `Kind`，例如 `"TCP"`、`"WebSocket"` 或 `"IPC"`。Editor 通过 `RemoteExecutionEditorApi.TransportKind` 展示当前服务端 transport 的 `Kind`；该值不是由 Player 上报。`ConfigurationKey` 用于表示全部连接配置并比较 Player 的重复 `Start`，且不得包含 secret；`Description` 是包含端点详情的展示文本，在绑定随机端口后可以变化。不要通过解析这两个值生成 `Kind`。

`IRemoteExecutionChannel` 仍保持独立，因为一个服务端 transport 可以接受多个 Player，每个连接都需要独立的收发和生命周期。Transport 被释放后，已经返回的 channel 仍由会话自行管理，不能随 transport 一起关闭。

Channel 传输完整的 `RemoteFrame`，必须保证可靠、无丢失且严格有序。包最多同时执行一次 send 和一次 receive；`Abort()` 必须立即解除等待中的 I/O，且与 `Dispose()` 一样可安全重复调用。

WebSocket 推荐将一个完整协议帧映射为一个 binary message：发送时使用 `RemoteExecutionProtocol.EncodeFrame`，接收完整消息后使用 `DecodeFrame`。WebSocket fragment 必须先重组，text message 必须拒绝。UDP 等无序协议若要接入，必须由实现层补齐排序、重传、去重和连接语义。

## Runtime 扩展接口

在 Player 连接前，为每个二进制 command 定义一个具体的 `IRemoteCommand` 类型。类型自己持有 definition 和 handler：

```csharp
using System.Threading;
using System.Threading.Tasks;
using RemoteExecution;

public sealed class TableReloadCommand : IRemoteCommand
{
    public string Name => "Reload tables";
    public string Description => "替换运行时表格数据并重载。";
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

具体 command 需要 public 无参构造函数，并在 Player 启动时自动发现。Editor 在 `StartServer` 时用 `TypeCache` 发现同样的 command 类型，仅建立 metadata 映射供 Editor typed API 使用，不会执行 handler。协议中的 command type 使用 command 类的 `FullName`，类型必须同时存在于 Editor 和 Player 兼容的程序集。请求/响应大小由协议和 Player 连接的全局配置决定，不再由单个 command 定义。反射发现的 command 类型在 IL2CPP 构建中需要通过 `[Preserve]` 或 `Assets/link.xml` 保留。

使用 `RemoteExecutionEditorApi.ExecuteCommandAsync<TableReloadCommand>(payload)` 调用 command。字符串 ID API 仍可用于动态 command。

## Editor API

业务 Editor 工具无需访问 socket 即可查询 Player 并执行命令：

```csharp
RemoteExecutionClientInfo player = RemoteExecutionEditorApi.GetClients()[0];
byte[] tableBytes = BuildTableBytes();

RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync<TableReloadCommand>(
    player.Id, tableBytes);

if (!result.Succeeded)
    UnityEngine.Debug.LogError($"[{result.Code}] {result.Message}");
```

对于无输入 command，Editor 代码可以直接以窗口 **命令** 页签当前选中的 Player 为目标，并等待其返回结果：

```csharp
RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync<RefreshGameCommand>();
byte[] response = result.Payload;
```

该调用会发送空 payload，并使用 `RefreshGameCommand` 声明的请求 content type。Remote Execution 窗口需要保持打开并已选中一个 Player。command 需要输入，或调用方需要明确选择 Player 时，应继续使用带 `sessionId` 的 overload。

`RefreshCommandsAsync(sessionId)` 会等待最新目录真正写入缓存后再完成。`GetClients()` 返回包含 Player target、显式 `IsReady` 状态和命令 metadata 的只读快照。`ClientId` 是 Player 自行声明的展示信息，不是安全身份。命令目录继续作为 Editor panel 与 Player 间的能力协商 API；核心窗口不再提供通用命令执行器。

## Editor 工具 Panel

Editor 集成实现 `IRemoteExecutionEditorPanel`，核心窗口通过 Unity `TypeCache` 自动发现：

```csharp
using RemoteExecution;
using UnityEngine;

public sealed class TableRemoteExecutionPanel : IRemoteExecutionEditorPanel
{
    public string Id => "game.tables";
    public string DisplayName => "Tables";
    public int Order => 50;

    public bool IsAvailable(RemoteExecutionEditorContext context, out string reason)
    {
        reason = context.SelectedPlayer == null ? "请选择 Player。" : string.Empty;
        return context.SelectedPlayer != null;
    }

    public void DrawGUI(RemoteExecutionEditorContext context)
    {
        if (!GUILayout.Button("构建并重载") || context.IsOperationRunning) return;
        int sessionId = context.SelectedPlayer.Id;
        byte[] bytes = BuildTableBytes();
        context.TryStartOperation("正在重载表格……", async cancellationToken =>
        {
            RemoteExecutionResult result = await RemoteExecutionEditorApi.ExecuteCommandAsync<TableReloadCommand>(
                sessionId, bytes, cancellationToken);
            if (!result.Succeeded)
                throw new System.InvalidOperationException($"[{result.Code}] {result.Message}");
            return "表格重载完成。";
        });
    }
}
```

Panel 必须是提供无参构造的 concrete 普通 C# 类，稳定 `Id` 必须全局唯一。`IsAvailable` 不能产生副作用；context 只在当前 `DrawGUI` 调用中有效，启动任务前应捕获 Player 和所有输入值。宿主保证同一 Player 在所有 panel 间同时只运行一个任务，不同 Player 可以并发。需要确定性清理时可实现 `IDisposable`；需要跨 reload 保存状态时应自行使用 `SessionState`、`EditorPrefs` 或 `ScriptableSingleton`。

带取消参数的 `RefreshCommandsAsync`、`ExecuteCommandAsync` overload 接收 `CancellationToken`。Unity 编译器调用和 Player handler 的取消仍然是协作式的。

## 可选 HybridCLR 适配器

安装 `com.code-philosophy.hybridclr` 后，包内 `versionDefines` 会自动启用条件适配器。它会在 **Window > Remote Execution** 内增加 **HybridCLR** 工具，并在 Player 注册 `RemoteExecution.HybridCLR.HybridCLRRemoteExecutionCommand`。

Remote Execution 窗口是唯一 Editor 入口：**基础**页签管理连接配置和 Player 概览；**命令**页签统一管理 Player/工具选择、每个 Player 的任务状态、结果和取消。命令页的二级工具切换栏包含 HybridCLR 与其他业务 panel，不再创建独立窗口。每个面向用户的远程操作由自身的 `IRemoteExecutionEditorPanel` 提供界面。

HybridCLR panel 提供：

- 将动态 `IHybridCLRRemoteExecutionEntry` 源码编译到 `RemoteExecution.Dynamic`；
- DLL/PDB 校验加载和接口入口执行。

动态源码示例：

```csharp
using System.Threading;
using System.Threading.Tasks;
using RemoteExecution.HybridCLR;

public sealed class RemoteExecutionEntry : IHybridCLRRemoteExecutionEntry
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 在这里处理程序集加载完成后的业务逻辑。
        await ReloadGameLogicAsync(cancellationToken);
    }

    private static Task ReloadGameLogicAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

入口必须是 public concrete class，提供 public 无参构造函数，并且一个 bundle 中只能存在一个 `IHybridCLRRemoteExecutionEntry` 实现。`ExecuteAsync` 的异常会作为 `ENTRY_EXECUTION_FAILED` 返回 Editor；取消会沿用远程命令的取消或超时结果。

该 panel 不会编译或发送项目中的热更新程序集。动态源码引用的程序集必须已在 Player 中加载。

HybridCLR 加载不是核心协议特性。Editor 把自有的 HybridCLR envelope 通过普通通用命令输入帧发送。Player 在加载前完整校验 envelope 和每个 artifact 的 hash。Bundle 必须包含且仅包含一个 public concrete `IHybridCLRRemoteExecutionEntry` 实现，并提供 public 无参构造函数。加载后，适配器会在固定的 `RemoteExecution.HybridCLR.HybridCLRRemoteExecutionCommand` 请求内调用 `Task ExecuteAsync(CancellationToken)`；动态入口不会发布到命令目录。项目仍需自行完成正常的 HybridCLR AOT metadata 配置。

完整 envelope（含 metadata）最大 128 MiB，并会完整缓冲在内存中。程序集不能从 Player AppDomain 卸载。同名程序集 hash 变化，或加载、解析入口过程中发生失败，都必须重启 Player。部分加载无法回滚；适配器会拒绝后续 apply，避免继续扩大不一致状态。

未安装 HybridCLR 时，该命令和 HybridCLR panel 都不存在。命令页仍显示其他模块提供的 panel；若没有任何实现，则显示空状态。

## 限制和行为

- Unity 2021.3 或更高版本。
- 包本身不强制要求 Development/Debug Player；构建包含范围和生产环境启用策略由接入项目控制。
- Player 不会自动启动或重连；业务代码负责 `Start`、重试和 `Stop`。
- 单帧 payload 最大 1 MiB。
- 单分片最大 60 KiB。
- command request 硬上限 128 MiB，普通业务命令默认 16 MiB。
- command response 硬上限 64 MiB，默认 16 MiB。
- 请求/响应大小是协议级全局限制，可以由 Player 连接配置进一步降低；command 不再声明单独的大小限制。
- 二进制输入和输出包含总长度与 SHA-256，分片必须完整且严格有序。
- 每个 Player 同时只执行一个 command。
- 命令 handler 和结果处理在 Unity 主线程执行，普通 `await` 会保留 Unity 同步上下文，HybridCLR 入口也遵循这一约定。handler 主动使用 `ConfigureAwait(false)` 或后台任务时，需要在访问 Unity API 前自行切回主线程；同步的 CPU 密集型 handler 会阻塞 Unity 帧。
- 超时和取消是协作式的；忽略取消的 handler 不能被安全强行中断。
- 核心协议不认证 `ClientId`；默认 TCP 没有认证或加密，自定义传输的 TLS/认证由其实现负责。
- SHA-256 只能检测意外的传输损坏，不能认证对端，也不能抵御主动篡改。
- 不要暴露破坏性操作或修改生产数据的 handler。

## 协议

Editor 与 Player 使用同一份包，共用一种协议格式。每帧包含 25 字节帧头：`UREX` 格式标识（4 字节）、消息类型（1 字节）、请求 ID（16 字节，按 `Guid.ToByteArray()` 顺序）和 payload 长度（4 字节小端整数），随后是 payload。连接使用无认证的 `Hello(requestId) → Ready(same requestId)` 握手；`Hello` 携带 client ID、target 和 Unity 版本，`Ready` payload 必须为空。

命令目录使用具体 command 类型的 `FullName`，请求/响应大小是全局限制而不是单个 command 的限制。消息编号固定：`Hello=1`、`Ready=2`、`Error=3`、`Ping=4`、`Pong=5`、`ListCommands=6`、`Commands=7`、command input begin/chunk/end 为 `8..10`、command result metadata/chunk/end 为 `11..13`、`CancelCommand=14`。HybridCLR envelope 作为普通命令 payload 传输，使用独立的 `HCLR` 格式标识，后面直接跟随 bundle ID 和 bundle 内容。

## 超时与取消

Player 的命令超时独立于主线程更新；同步命令也应定期检查取消令牌。Editor 在命令取得执行名额后开始计时，发送请求和等待结果共用命令超时加 5 秒宽限的预算；目录刷新共用 10 秒预算。正在发送帧时取消或超时会中止当前连接，以免后续请求接续到不完整帧。业务层可按需重新连接。排队等待时取消不会中止前一个操作的连接。

无载荷且未声明 content type 的失败结果会保留远端错误码；带载荷的结果仍须符合命令声明的响应类型。字符串调用传入命令类型的完整名称，也可以优先使用上面的泛型调用；`RefreshGameCommand` 代表业务自行实现的无输入命令。

## 回归测试

安装与 Unity 版本匹配的 Unity Test Framework，刷新资源后在 EditMode 运行 `RemoteExecution.Tests.Editor`。安装 HybridCLR 后，还可运行 `RemoteExecution.HybridCLR.Tests.Editor` 验证 bundle 编解码。以 Git 包安装时，将 `com.xw.remote-execution` 加入工程 `Packages/manifest.json` 的 `testables` 列表以启用包测试。测试使用隔离的内存通道，不启动真实连接或修改场景。
