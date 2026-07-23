# WpfMultiProcess — WPF 多进程框架

一个"一个功能一个进程"的 WPF 多窗口框架：主进程负责窗口编排/停靠布局，每个业务
功能（feature）跑在自己独立的子进程里，子进程窗口通过 `SetWindowPos`（异步、手动
维护 Z 序，不建立 owner 关系）overlay 到主进程 dock 容器的占位区域上，做到"看起来
像一个程序，实际是好几个进程"——任何一个子进程的 UI 线程卡死都不会拖累主进程或
其他子进程。

仓库拆成两个项目：

```
src/WpfMultiProcess.Framework/   可复用框架库（net10.0-windows, class library）
                                  零 AvalonDock / Infragistics 依赖
demo/WpfMultiProcess.Demo/        演示应用（WinExe），引用 Framework + AvalonDock，
                                  包含 waveform / table 两个示例 feature
```

没有 `.sln` 文件，直接对 demo 项目 build/run 即可（会通过 ProjectReference 带出
Framework）：

```
dotnet run --project demo/WpfMultiProcess.Demo   # 主进程，自动打开 waveform/table 各一个实例
```

子进程由主进程自动拉起，命令行形如：

```
WpfMultiProcess.Demo.exe --child --feature=waveform --index=0 --session=<guid> --socket=<uds路径> --hostpid=<主进程pid>
```

`session_id`/`featureIndex` 都由主进程在拉起子进程之前生成（`SessionManager.OpenFeature`），
作为启动参数传给子进程；子进程开 feature 流时原样带上，不需要再用一次 RPC 向主进程
换取身份。同一个 feature 可以反复调用 `OpenFeature` 多开出互相独立的会话/子进程。

## 架构

框架分两层：**Framework 库**提供和具体业务无关的会话/窗口编排骨架，**Demo 应用**
在这之上实现具体的 waveform/table 两个 feature，同时提供 Framework 抽象出的
`IDockWorkspace`（用 AvalonDock 实现）。新增一个 feature 只需要在 Demo（或调库方
自己的宿主应用）里新增一对 proto + Host/Child 模块，不需要改动 Framework 一行代码；
换一个 dock 库（比如 Infragistics `XamDockManager`）也只需要另写一个
`IDockWorkspace` 实现，`SessionManager`/`OverlayHost` 都不用改。

```
┌────────────────────────── 主进程 (gRPC Server, Kestrel/UDS) ───────────────────────────┐
│  MainWindow (demo)：AvalonDock VS2013 主题，dock pane 全动态创建（无预置 tab）           │
│    ├── 工具栏"新建波形/新建表格" → SessionManager.OpenFeature(featureId)                │
│    ├── 每次 OpenFeature → 造 Session 子类 + OverlayHost + IDockWorkspace.AddPane +      │
│    │     启动子进程，同一 feature 可反复调用多开                                        │
│    └── 状态栏/事件日志（订阅 SessionManager 的事件，含心跳 RTT + UI 饱和度遥测）         │
│                                                                                          │
│  CommonServiceImpl (Framework，feature 无关)         WaveformFeature/TableFeature (demo) │
│    Pong/RequestActivate/ReportUiStats                  ├── WaveformServiceImpl          │
│      → 按 session_id 委托给 SessionManager             │    Register: TryOpen → 写      │
│                     ↓                                   │    Reply → 50ms 正弦帧 +       │
│  SessionManager (Framework)                             │    统计 unary                 │
│    ├── OpenFeature：分配 session_id/featureIndex/       └── TableServiceImpl            │
│    │     Session/OverlayHost/dock pane，拉起子进程           Register: TryOpen → 写      │
│    ├── TryOpen<TDown>：校验 session_id 属于该 feature，       Reply → 动态行 + Sort unary │
│    │     接 Subscription<TDown>，Dispatcher.BeginInvoke                                 │
│    │     调 OverlayHost.AttachChild，回调 Session.OnConnected                            │
│    ├── 心跳 2s → Control{Ping} 推给所有会话；无响应检测                                  │
│    │     (5000ms 阈值) → UiUnresponsive/UiRecovered                                     │
│    └── CloseSession/DetachStream：同样 Dispatcher.BeginInvoke 回 UI 线程                 │
│          才碰 Pane.Close()/OverlayHost.DetachChild()                                    │
└──────────────────────────────────────────┬─────────────────────────────────────────────┘
                                  UDS: 套接字路径含主进程 PID（HTTP/2，多服务共用一个端点）
┌──────────────────────────────────────────┴───────────────── 子进程 (gRPC Client) ──────┐
│  ChildWindow/ChildShell (Framework)：WindowStyle=None, ShowInTaskbar=false,             │
│  WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW，初始位置屏幕外，框架级状态条 + UiSaturationMeter     │
│    1. SourceReady：拿到 hwnd → 建 Channel + ChildShell(session_id/featureId/index)      │
│    2. IFeatureChild.CreateViewModel(ctx) → feature 自己的 gRPC client 发起 Register     │
│       开流（带 session_id/hwnd/pid），得到 AsyncServerStreamingCall 交给                │
│       FeatureViewModel<TDown> 基类                                                      │
│    3. FeatureViewModel<TDown>.RunAsync 读 stream，逐条 envelope 调用具体 feature        │
│       ViewModel 的 Dispatch：Reply→标题/主题色，Control→Ping 回 Pong/Shutdown 关窗口，   │
│       数据帧→OnData 更新绑定属性（波形折线 / DataGrid 行）                              │
│    4. 点击子窗口 → ChildShell.RequestActivate() → CommonService.RequestActivate         │
│       （子窗口不激活主窗口，靠这个 unary 补偿式激活主窗口）                             │
│    5. feature 按钮（统计/排序/模拟卡死）→ 各自 feature 服务的 unary RPC 或本地操作       │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

## 关键设计点

- **Framework / Demo 两层拆分**：`src/WpfMultiProcess.Framework` 是纯粹的可复用库
  （`OutputType=Library`，`UseWPF`，零 AvalonDock/Infragistics 依赖），只认
  `IDockWorkspace`/`IFeatureHost`/`IFeatureChild` 这几个接口；`demo/WpfMultiProcess.Demo`
  才是 `WinExe`，引用 Framework（`ProjectReference`）+ `Dirkster.AvalonDock`，实现
  具体的 `AvalonDockWorkspace`（`IDockWorkspace` 的 AvalonDock 版本）和 waveform/table
  两个 feature。调库方要接入自己的宿主应用，只需要：实现一个 `IDockWorkspace`、
  为每个 feature 实现一对 `IFeatureHost`（主进程侧：造 `Session` 子类 + 挂 gRPC
  service）/`IFeatureChild`（子进程侧：造 `FeatureViewModel` 子类 + 造 View）。
- **跨项目 proto 共享**：`common.proto`（`CommonService` + `RegisterReply`/`Control`
  (Ping/Shutdown)/`Ack`/`UiStatsRequest` 等共享消息）只在 Framework 项目里编译
  （`GrpcServices="Both"`）；Demo 的 `waveform.proto`/`table.proto` 里 `import`
  `common.proto`，但不重复编译它——靠 `<Protobuf>` 的 `AdditionalImportDirs` 指向
  Framework 的 `Protos` 目录，只让 protoc 解析 `import`/找到 `csharp_namespace`
  声明，真正的 `WpfMultiProcess.Ipc.Common.*` 类型来自 `ProjectReference` 带出的
  Framework 程序集，避免生成同名类型冲突。
- **可插拔的多 feature 架构**：`CommonService`（`Pong`/`RequestActivate`/
  `ReportUiStats`，都按 `session_id` 路由，feature 无关）与具体业务完全解耦；每个
  feature 各自拥有一个独立 gRPC 服务（`WaveformService`/`TableService`……），其
  `Register(StreamRequest{session_id, hwnd, pid})` 返回的强类型 stream 用 `oneof`
  三路复用：开场先写一帧 `RegisterReply`（标题/主题色，从
  `SessionManager.ReplyOf(featureId)` 取），随后是公共 `Control`（Ping/Shutdown）
  和 feature 自己的数据帧，全部封装在同一个 envelope 里。主进程侧
  `Session<TDown>`/`Subscription<TDown>` 与子进程侧 `FeatureViewModel<TDown>` 各自
  是这层 envelope 的泛型接缝，`SessionManager`/`ChildShell` 本身完全不知道 `TDown`
  是什么类型——新增一个 feature 只需要新增一个 proto + 一对 Host/Child 实现，不需要
  改动 `SessionManager`、`ChildShell`、`CommonService` 或其他 feature 的任何代码。
- **会话生命周期（`SessionManager`）**：`OpenFeature(featureId)` 一次调用完成"分配
  featureIndex/session_id、造调库方的 `Session` 子类、造 `OverlayHost`、经
  `IDockWorkspace.AddPane` 建 dock pane、拉起子进程、登记"全过程，支持同一 feature
  反复调用多开出独立会话；`TryOpen<TDown>` 在 feature service 的 `Register` 收到
  开流请求时校验 `session_id` 确实是这个 feature 预留的、类型匹配，通过后接上
  `Subscription<TDown>`、触发 `Session.OnConnected`；`CloseSession`/`DetachStream`
  对称地做断开/关闭清理，`CloseAll` 供主窗口关闭时批量调用。
- **进程模型**：`Program.Main` 按 `--child` 分流到 `HostProgram`（Framework 的
  `SessionManager` + Kestrel）/`ChildProgram`（Framework 的通用子进程 bootstrap，
  按 `--feature` 在注册的 `IFeatureChild` 列表里查表）。套接字路径含主进程 PID，
  支持应用多开互不干扰。
- **UDS 通道**：客户端用 `SocketsHttpHandler.ConnectCallback` 手工连
  `UnixDomainSocketEndPoint`（`Ipc/GrpcUds.cs`）；服务端 Kestrel `ListenUnixSocket` +
  HTTP/2，`CommonService` 与各 feature 的 gRPC 服务共用同一个端点，靠 gRPC 自身的
  服务路由区分。
- **推送背压**：每个会话的每个订阅是一个 `Subscription<TDown>`，内部是一个
  `BoundedChannel(256, DropOldest)`，子进程消费慢时丢最旧数据帧，主进程不会被拖垮；
  Ping/Shutdown 与 feature 数据共用同一条 stream（`oneof payload`）。
- **心跳语义**：`SessionManager` 2s 推一次 `Control{Ping{seq, timestamp}}` 给所有
  会话；子进程收到后先 `Dispatcher.BeginInvoke` 到 UI 线程再发 `Pong` unary——因此
  RTT 度量的是"子进程 UI 线程健康度"，UI 卡死时心跳即断，`SessionManager` 按
  5000ms 阈值只在状态翻转时触发一次 `UiUnresponsive`/`UiRecovered`，避免刷屏。
- **UI 线程饱和度**：心跳只能判断"彻底失联"，`Child/UiSaturationMeter.cs`
  （框架级、feature-无关，挂在 `ChildShell` 上）额外给出一个 0..100 的"饱和度"
  百分数——不是 CPU 占用率，而是 UI 线程还有没有空闲容量及时执行低优先级回调。
  两部分：探针（权威值）由专用后台线程用 `Stopwatch` 自驱动，维持"恰好一个"未决的
  `Dispatcher.BeginInvoke(Background, ...)` 探针，超过约 8ms grace 之后每步
  （~15ms）把这段墙钟切片积进 `_busyMs`，探针一旦被执行立刻重发下一个；UI 线程
  彻底卡死时探针永远不会被执行，整段卡死墙钟原样计入——且这个累加动作是后台线程
  自己做的，完全不依赖 UI 线程，所以卡死**进行中**就能报出接近 100% 的饱和度，不用
  等卡死结束。hooks（归因）在 UI 线程注册 `Dispatcher.Hooks`，统计 dispatcher
  忙时占比、最长排队延迟、最长单次操作、操作数——能定性"忙在哪个操作上"。两部分都经
  `CommonService.ReportUiStats`（按 session_id）fire-and-forget 上报,
  `SessionManager.OnUiStats` 转发给调库方的 `Session.OnUiStats`（可 override）并抛出
  `UiStatsReceived` 事件，`MainWindow` 订阅后在状态栏展示。
- **窗口嵌入**：不用 `SetParent`，也**不用** `SetWindowLongPtr(GWLP_HWNDPARENT)` 做
  owner 关系——早期方案曾用 owner，但跨进程 owner/SetParent 都会让 Windows 隐式合并
  两个线程的输入队列（等效 `AttachThreadInput`），一旦子进程 UI 线程卡死，主进程和
  另一个子进程窗口的输入会被一起冻住，代价无法接受。现改为子窗口与宿主**没有任何
  系统级关系**：`OverlayHost` 只靠 `SetWindowPos` 持续把子窗口钉在占位控件的屏幕
  矩形上（`LayoutUpdated`/`LocationChanged`/`StateChanged`/宿主
  `WM_WINDOWPOSCHANGED` 驱动），并且每次都显式算一遍 `hWndInsertAfter`（取宿主
  `GW_HWNDPREV` 紧邻的窗口）手动把子窗口插到宿主正上方，靠"持续纠正 Z 序"代替
  owner 关系。子窗口自身加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` 并拦截
  `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`：点击不激活、不进 Alt-Tab/任务栏、也不会
  自己扰乱这里维护的 Z 序；代价是点击子窗口不会带起主窗口，用一次
  `CommonService.RequestActivate` unary（按 session_id 上报）换取主窗口
  `Activate()` 补偿。**关键坑**：光去掉 owner 关系还不够——`SetWindowPos`/
  `ShowWindow` 对不同线程（含跨进程）的窗口默认会像 `SendMessage` 一样同步阻塞
  发消息，子窗口卡死时仍会拖住调用方所在的主进程 UI 线程。必须加上
  `SWP_ASYNCWINDOWPOS`（隐藏时用 `SetWindowPos`+`SWP_HIDEWINDOW` 代替
  `ShowWindow(SW_HIDE)`）让请求改为 post 给目标线程、调用方立即返回，才是真正
  隔离卡死影响的关键——本次重构后用一个子进程卡死 10 秒的实测验证过：卡死期间
  主进程窗口和其他子进程窗口的心跳/UI Automation 查询完全不受影响，持续正常响应。
  dock pane 拖动/隐藏/浮动时占位控件 `Unloaded`/不可见 → 子窗口 `SW_HIDE`。
  `LayoutUpdated` 是"整窗任意布局 pass 完成"级别的事件，tab 切换期间会连续触发
  几十次，`UpdatePlacement` 因此加了脏检查（位置/大小/可见性/Z 序都和上次发出的
  一致就直接跳过）并把 `LayoutUpdated`/`IsVisibleChanged`/宿主
  `WM_WINDOWPOSCHANGED` 都改成 `Dispatcher.BeginInvoke` 去抖合并成一次；被切走
  隐藏的子窗口也暂停重绘。
- **跨线程访问 WPF 对象**：`SessionManager.TryOpen`/`DetachStream`/`CloseSession`
  都可能在 gRPC 线程池线程（Kestrel）或 `Process.Exited`（ThreadPool）上被调用，
  而它们要碰的 `OverlayHost`（`Border`）、`IDockPane`（AvalonDock `LayoutDocument`）
  都是 UI 线程独占的 `DependencyObject`——这几处统一用
  `entry.Overlay.Dispatcher.BeginInvoke(...)` fire-and-forget 调度回 UI 线程再调用
  `AttachChild`/`DetachChild`/`Pane.Close()`；而 `Session.OnConnected`/
  `OnDisconnected` 等生命周期钩子（典型实现只是起停一个后台 producer）不碰任何 UI
  对象，原样同步调用即可，不需要过度加 Dispatcher 调度。
- **生命周期**：
  - 主窗口关闭 → `SessionManager.CloseAll()` 依次 `CloseSession`（推
    `Control{Shutdown}` → 子进程 `Close()`；1.5s 未退则 `Kill()` 兜底）——实测验证过
    关闭主窗口后所有子进程全部自动退出，无孤儿进程残留。
  - 主进程崩溃 → 子进程监听 `Process.Exited` 自杀 + stream 断开双保险
    （`FeatureViewModel<TDown>.RunAsync` 捕获异常后关窗口）。
  - 子进程退出/断开 → `OverlayHost.DetachChild()` 回到空白占位；对应的 dock pane
    (`Pane.Close()`) 也一并关闭。
- **DPI**：主/子进程同一 manifest（PerMonitorV2），`PointToScreen` 直接给出物理
  像素，跨显示器坐标一致。

## 文件结构

### `src/WpfMultiProcess.Framework/`（可复用库，零 AvalonDock/Infragistics 依赖）

| 文件 | 职责 |
|---|---|
| `WpfMultiProcess.Framework.csproj` | `Library`, `net10.0-windows`, `UseWPF`；`FrameworkReference=Microsoft.AspNetCore.App`（Kestrel 需要）；只编译 `common.proto` |
| `Protos/common.proto` | 公共契约：`CommonService`(Pong/RequestActivate/ReportUiStats，按 session_id 路由) + 共享消息(`RegisterReply`/`Control`(Ping/Shutdown)/`Ack`/`UiStatsRequest`) |
| `Ipc/GrpcUds.cs` | UDS 通道工厂（客户端 `ConnectCallback` + 套接字路径约定） |
| `Ipc/Win32.cs` | GetWindow / GetWindowLongPtr / SetWindowLongPtr / SetWindowPos / ShowWindow P/Invoke |
| `Host/IDockWorkspace.cs` | `IDockWorkspace`/`IDockPane`：框架对"dock 容器"的最小抽象，形状贴着 `XamDockManager` 设计，零依赖任何具体 dock 库 |
| `Host/IFeatureHost.cs` | 调库方主进程侧接缝：`FeatureId`/`Descriptor`/`CreateSession(ctx)`/`MapService(endpoints)` |
| `Host/Session/Session.cs` | `Session`（非泛型）+ `Session<TDown>`：会话身份、`SendHeartbeat`/`SendClose`、`OnConnected`/`OnPong`/`OnUiStats`/`OnDisconnected` 生命周期虚方法、`PushData` |
| `Host/Session/Subscription.cs` | 订阅句柄：`Subscription<TDown>` 持有有界 channel(256, DropOldest) + wrap 委托 |
| `Host/Session/SessionManager.cs` | 会话层核心：`OpenFeature`/`TryOpen<TDown>`/`DetachStream<TDown>`/`CloseSession`/`CloseAll`、心跳泵(2s)/无响应检测(5000ms)、`ReplyOf`、`FindSession<TSession>`、跨线程 Dispatcher 调度 |
| `Host/CommonServiceImpl.cs` | `CommonService` 实现（薄壳，按 session_id 转发给 SessionManager） |
| `Host/OverlayHost.cs` | 占位控件：无 owner 关系，`SetWindowPos`(`SWP_ASYNCWINDOWPOS`)钉位置+手动 Z 序（feature 无关） |
| `Child/ChildStartOptions.cs` | 子进程启动参数：featureId/featureIndex/sessionId/socketPath/hostPid |
| `Child/ChildProgram.cs` | 子进程通用入口：孤儿自杀看护 + 拉起 `ChildWindow` + bootstrap(建 channel/`ChildShell` → `IFeatureChild.CreateViewModel`/`CreateView` → `Start()`) |
| `Child/ChildContext.cs` | `{Channel, SessionId, FeatureIndex, Shell}` 只读结构体，传给 `IFeatureChild.CreateViewModel` |
| `Child/ChildWindow.cs` | 无边框子窗口：`WS_EX_NOACTIVATE`\|`WS_EX_TOOLWINDOW`、初始位置屏幕外、`SourceReady`/`Closed` 接缝 |
| `Child/ChildShell.cs` | 子进程框架状态条：持有 Hwnd、`ApplyReply`(标题/主题色)、`SendPong`/`RequestActivate`、`RequestClose`、拉起/停止 `UiSaturationMeter` |
| `Child/UiSaturationMeter.cs` | 框架级、feature-无关：后台线程探针积分 UI 线程饱和度(权威值) + `Dispatcher.Hooks` 归因，经 `CommonService.ReportUiStats` 上报 |
| `Child/IFeatureChild.cs` | 调库方子进程侧接缝：`FeatureId`/`CreateViewModel(ChildContext)`/`CreateView(FeatureViewModel)` |
| `Child/FeatureViewModel.cs` | `FeatureViewModel`(非泛型) + `FeatureViewModel<TDown>`：`RunAsync` 循环读 stream、`HandleControl`(Ping→Pong/Shutdown→关窗口)、`OnReply`(标题/主题色)、抽象 `Dispatch`/`OnData` |

### `demo/WpfMultiProcess.Demo/`（演示应用，WinExe，引用 Framework + AvalonDock）

| 文件 | 职责 |
|---|---|
| `WpfMultiProcess.Demo.csproj` | `WinExe`，`ProjectReference` Framework，`PackageReference` `Dirkster.AvalonDock`(+VS2013 主题)；`waveform.proto`/`table.proto` 用 `AdditionalImportDirs` 解析 `common.proto` 的 import 而不重复编译 |
| `Program.cs` | 入口 + 命令行解析(`CmdLine`)，按 `--child` 分流到 `ChildProgram.Run`/`HostProgram.Run` |
| `Protos/waveform.proto` | `WaveformService`：Register(StreamRequest → server stream, envelope = Reply⊕Control⊕Frame) + GetStatistics unary |
| `Protos/table.proto` | `TableService`：Register(StreamRequest → server stream, envelope = Reply⊕Control⊕Delta) + Sort unary |
| `Host/HostProgram.cs` | 主进程入口：造 `MainWindow`/`SessionManager`，起 Kestrel(CommonService + 各 feature 服务)，回调 `AttachSessionManager` |
| `Host/MainWindow.cs` | AvalonDock 主窗口：dock pane 全动态创建，"新建波形/新建表格"按钮触发 `OpenFeature`，订阅 `SessionManager` 事件展示状态栏/事件日志，F9 float/dock 测试钩子 |
| `Host/AvalonDockWorkspace.cs` | `IDockWorkspace` 的 AvalonDock 实现：`AddPane` 建 `LayoutDocument`，包装成 `IDockPane` |
| `Host/Features/Waveform/WaveformFeature.cs`, `WaveformSession.cs`, `WaveformServiceImpl.cs` | 波形 feature：`IFeatureHost` 实现 + `Session<WaveformDown>` 子类(50ms 正弦帧 producer) + gRPC 服务实现(Register 走 TryOpen，GetStatistics unary) |
| `Host/Features/Table/TableFeature.cs`, `TableSession.cs`, `TableServiceImpl.cs` | 表格 feature：同上，动态行(增删/变值) + Sort unary |
| `Child/Features/Waveform/WaveformFeatureChild.cs`, `WaveformViewModel.cs`, `WaveformView.cs` | 波形子进程侧：`IFeatureChild` 实现 + `FeatureViewModel<WaveformDown>` 子类(自己发起 Register) + Polyline 渲染视图(隐藏时暂停) + 统计/模拟卡死按钮 |
| `Child/Features/Table/TableFeatureChild.cs`, `TableViewModel.cs`, `TableView.cs` | 表格子进程侧：同上，DataGrid + 排序按钮 |

## 已验证的运行时行为

以下行为在本次重构后逐一做过实际多进程运行验证（非仅静态代码审查）：

- 多开：同一 feature 反复点击"新建波形/新建表格"能正确开出独立 `featureIndex` 的
  新子进程，各自 overlay 到自己的 dock pane。
- 心跳/Pong：状态栏心跳计数持续滚动，多个子进程共享同一心跳序列，互相独立。
- unary RPC：统计(`GetStatistics`)/排序(`Sort`) 按钮触发对应 feature 服务的 unary 调用
  并正确回显结果。
- **卡死隔离**（`SWP_ASYNCWINDOWPOS` 的核心价值）：让一个子进程 UI 线程
  `Thread.Sleep` 卡死 10 秒，同一时间窗口内其余子进程窗口的心跳计数持续正常滚动、
  对 UI Automation 查询保持正常响应，卡死子进程在 10 秒后自动恢复。
- pane 关闭：关闭某个 tab 只终止对应的那一个子进程，其余会话不受影响。
- 主窗口关闭：所有子进程随主窗口关闭自动退出，进程列表里不留任何孤儿
  `WpfMultiProcess.Demo.exe`。
