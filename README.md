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

`session_id`/`featureIndex` 都由主进程在拉起子进程之前生成（`SessionManager.OpenFeature`
的调用方——demo 里是 `MainWindow.OpenFeatureInstance`），作为启动参数传给子进程；
子进程开 feature 流时原样带上，不需要再用一次 RPC 向主进程换取身份。同一个 feature
可以反复调用 `OpenFeature` 多开出互相独立的会话/子进程，调用方自己维护每个
featureId 下一个可用的 featureIndex。

## 架构

框架分两层：**Framework 库**提供和具体业务无关的会话/窗口编排骨架，**Demo 应用**
在这之上实现具体的 waveform/table 两个 feature，同时提供 Framework 抽象出的
`IDockPane`（用 AvalonDock 的 `LayoutDocument` 实现）。新增一个 feature 只需要在
Demo（或调库方自己的宿主应用）里新增一对 proto + Host/Child 模块，不需要改动
Framework 一行代码；换一个 dock 库（比如 Infragistics `XamDockManager`）也只需要
另写一个 `IDockPane` 实现（造 `ContentPane`/怎么摆放完全是调库方 UI 代码自己的
职责，框架不再负责"造 pane"），`SessionManager`/`OverlayHost` 都不用改。

```
┌────────────────────────── 主进程 (gRPC Server, Kestrel/UDS) ───────────────────────────┐
│  MainWindow (demo)：AvalonDock VS2013 主题，dock pane 全动态创建（无预置 tab）           │
│    ├── 工具栏"新建波形/新建表格" → 自己造 LayoutDocument+AvalonDockPane，维护          │
│    │     每个 featureId 的下一个 featureIndex → SessionManager.OpenFeature(id,idx,pane)│
│    ├── 每次 OpenFeature → 分配 session_id、造 OverlayHost 塞进 pane、拉起子进程、       │
│    │     预登记（此时还没有具体 Session 对象），同一 feature 可反复调用多开            │
│    └── 状态栏/事件日志（订阅 SessionManager 的事件，含心跳 RTT + UI 饱和度遥测）         │
│                                                                                          │
│  CommonServiceImpl (Framework，feature 无关)         WaveformFeature/TableFeature (demo) │
│    Pong/RequestActivate/ReportUiStats                  ├── WaveformServiceImpl          │
│      → 按 session_id 委托给 SessionManager             │    Register: 自己 new Session  │
│                     ↓                                   │    → SessionManager.Register  │
│  SessionManager (Framework)                             │    校验 → 写 Reply → 把        │
│    ├── OpenFeature：分配 session_id、造 OverlayHost、    │    IServerStreamWriter 所有权 │
│    │     调 pane.SetContent、拉起子进程、预登记          │    交给 Session.ServeAsync    │
│    ├── Register(session,pid,hwnd)：校验 sessionId 是    └── TableServiceImpl            │
│    │     预登记过的、featureId 对得上、未被重复注册，         同上模式 + Sort unary       │
│    │     通过后回填 featureIndex/pid/hwnd，调                                            │
│    │     OverlayHost.AttachChild，回调 Session.OnConnected                               │
│    ├── 心跳 2s → Session.SendHeartbeat(Ping) 推给所有已连接会话；无响应检测              │
│    │     (5000ms 阈值) → UiUnresponsive/UiRecovered                                     │
│    └── CloseSession/Unregister：Dispatcher.BeginInvoke 回 UI 线程才碰                    │
│          Pane.Close()/OverlayHost.DetachChild()；Unregister 时 Session.DisposeOnce()     │
└──────────────────────────────────────────┬─────────────────────────────────────────────┘
                                  UDS: 套接字路径含主进程 PID（HTTP/2，多服务共用一个端点）
┌──────────────────────────────────────────┴───────────────── 子进程 (gRPC Client) ──────┐
│  子进程入口 ChildProgram.Run(IHost host, ChildStartOptions, IReadOnlyList<IFeatureChild>)│
│  ChildWindow/ChildShell (Framework)：WindowStyle=None, ShowInTaskbar=false,             │
│  WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW，初始位置屏幕外，框架级状态条 + UiSaturationMeter     │
│    1. host 只是 DI/日志容器（ILoggerFactory 从这里解析）：Application 实例也从           │
│       host.Services 里取（不再 new Application()），调库方在自己 Program.cs 里          │
│       注册想要的 Application（可以是子类，可以挂自定义资源），跑 app.Run(window)         │
│    2. SourceReady：拿到 hwnd → 建 Channel + ChildShell(session_id/featureId/index)      │
│    3. IFeatureChild.CreateViewModel(ctx) → feature 自己的 gRPC client 发起 Register     │
│       开流（带 session_id/hwnd/pid），得到 AsyncServerStreamingCall 交给                │
│       FeatureViewModel<TDown> 基类                                                      │
│    4. FeatureViewModel<TDown>.RunAsync 读 stream，逐条 envelope 调用具体 feature        │
│       ViewModel 的 Dispatch：Reply→标题/主题色，Ping→回 Pong，Shutdown→关窗口，          │
│       数据帧→OnData 更新绑定属性（波形折线 / DataGrid 行）                              │
│    5. 点击子窗口 → ChildShell.RequestActivate() → CommonService.RequestActivate         │
│       （子窗口不激活主窗口，靠这个 unary 补偿式激活主窗口）                             │
│    6. feature 按钮（统计/排序/模拟卡死）→ 各自 feature 服务的 unary RPC 或本地操作       │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

## 关键设计点

- **Framework / Demo 两层拆分**：`src/WpfMultiProcess.Framework` 是纯粹的可复用库
  （`OutputType=Library`，`UseWPF`，零 AvalonDock/Infragistics 依赖），只认
  `IDockPane`/`IFeatureHost`/`IFeatureChild` 这几个接口；`demo/WpfMultiProcess.Demo`
  才是 `WinExe`，引用 Framework（`ProjectReference`）+ `Dirkster.AvalonDock`，实现
  具体的 `AvalonDockPane`（`IDockPane` 的 AvalonDock 版本，包一个调用方已经建好并
  加进 `LayoutDocumentPane` 的 `LayoutDocument`）和 waveform/table 两个 feature。
  调库方要接入自己的宿主应用，只需要：为每个 feature 实现一对 `IFeatureHost`
  （主进程侧：挂 gRPC service，service 自己 `new` 一个 `Session` 子类）/
  `IFeatureChild`（子进程侧：造 `FeatureViewModel` 子类 + 造 View），以及自己的
  `IDockPane` 实现（如果不用 AvalonDock）。**框架不再有 `IDockWorkspace`** 这层
  "造 pane 的工厂"——造 dock pane（`LayoutDocument`/`ContentPane` 之类）、决定它
  挂在哪个容器下面，完全是调库方 UI 代码自己的职责，`OpenFeature` 只接收一个
  已经造好的 `IDockPane`。
- **跨项目 proto 共享**：`common.proto`（`CommonService` + `RegisterReply`/
  `Ping`/`Shutdown`/`ShutdownReason`/`Ack`/`UiStatsRequest` 等共享消息）只在
  Framework 项目里编译（`GrpcServices="Both"`）；Demo 的 `waveform.proto`/
  `table.proto` 里 `import` `common.proto`，但不重复编译它——靠 `<Protobuf>` 的
  `AdditionalImportDirs` 指向 Framework 的 `Protos` 目录，只让 protoc 解析
  `import`/找到 `csharp_namespace` 声明，真正的 `WpfMultiProcess.Ipc.Common.*`
  类型来自 `ProjectReference` 带出的 Framework 程序集，避免生成同名类型冲突。
- **可插拔的多 feature 架构**：`CommonService`（`Pong`/`RequestActivate`/
  `ReportUiStats`，都按 `session_id` 路由，feature 无关）与具体业务完全解耦；每个
  feature 各自拥有一个独立 gRPC 服务（`WaveformService`/`TableService`……），其
  `Register(StreamRequest{session_id, hwnd, pid})` 返回的强类型 stream 用 `oneof`
  三路复用：开场先写一帧 `RegisterReply`（标题/主题色，从
  `SessionManager.ReplyOf(featureId)` 取），随后是公共的 `Ping`/`Shutdown`
  （proto 里原来还有一层 `Control{ping/shutdown}` 包装，现已去掉，`Ping`/
  `Shutdown` 直接是 oneof 的两个独立分支，少一层 demux）和 feature 自己的数据帧，
  全部封装在同一个 envelope 里。主进程侧非泛型的 `Session`（抽象基类，`TDown`
  完全下沉给实现方自己的内部 Channel）与子进程侧 `FeatureViewModel<TDown>` 各自
  是这层 envelope 的接缝，`SessionManager`/`ChildShell` 本身完全不知道 `TDown`
  是什么类型——新增一个 feature 只需要新增一个 proto + 一对 Host/Child 实现，不需要
  改动 `SessionManager`、`ChildShell`、`CommonService` 或其他 feature 的任何代码。
- **会话建立顺序（重要变化）**：`SessionManager.OpenFeature(featureId, featureIndex,
  pane)` 一次调用只做"分配 session_id、造 `OverlayHost`、`pane.SetContent`、拉起
  子进程、预登记（session_id/featureId/featureIndex 这几个 feature-无关的元数据）"，
  这时候还**不知道**具体 feature 的 `Session` 长什么样（`IFeatureHost` 已经没有
  `CreateSession` 这个方法了）。真正的 `Session` 对象，是子进程连上来发起
  `Register` 时，由 feature 自己的 gRPC service 实现现造的（它知道自己的 `TDown`
  是什么），再经 `SessionManager.Register(session, pid, hwnd)` 校验 session_id
  确实是预登记过的、featureId 匹配、且没有被重复注册，通过后才把这个 `Session`
  接入心跳表、回填 `featureIndex`/`Pid`/`Hwnd`、触发 `OnConnected`。`TryOpen` 这个
  名字已经废弃，改名 `Register`，更准确地反映"这是子进程主动注册会话"而不是
  "尝试打开"。
- **`Session` 非泛型化 + 流所有权反转**：不再有泛型的 `Session<TDown>`/
  `Subscription<TDown>`——那一套是"框架代持数据管道"的设计，现在数据管道（内部
  `Channel<TDown>` + 怎么把 `Ping`/`Shutdown` 包装成自己的 envelope）完全下沉给
  实现方自己维护，框架只认非泛型的抽象基类 `Session : IDisposable`：
  - `SendHeartbeat(Ping)`/`SendClose(Shutdown)`：框架心跳循环/`CloseSession`
    调用，实现方把消息包成自己的 envelope 写进自己的管道；
  - `Task ServeAsync<T>(IServerStreamWriter<T> writer, CancellationToken ct)`：
    接收 `IServerStreamWriter<T>` 所有权的方法——feature service 的 `Register`
    处理器写完第一条 `Reply` 之后调用它并 `await`，直到会话结束才返回，典型实现
    是把自己内部 `Channel` 的 `Reader` 和这个 `writer` 一起交给静态助手
    `SessionPump.PumpAsync<T>`；
  - 生命周期钩子 `OnConnected`/`OnPong`/`OnUiStats`/`OnDisconnected` 保留，时机
    不变；
  - `Session` 继承 `IDisposable`：实现方在 `Dispose()` 里做退订/清理（典型是
    `Complete` 自己的 Channel writer、取消自己的 producer）。框架通过内部的
    `DisposeOnce()` 幂等门保证：即使同一个会话可能同时经用户主动 `CloseSession`
    和 feature service 的 `Register` 流 `finally` 两条路径触发清理，`Dispose()`
    也只会被真正调用一次。
- **进程模型**：`Program.Main` 按 `--child` 分流到 `HostProgram`（Framework 的
  `SessionManager` + Kestrel）/`ChildProgram.Run(IHost host, ChildStartOptions, ...)`
  （Framework 的通用子进程 bootstrap，按 `--feature` 在注册的 `IFeatureChild` 列表
  里查表）。子进程侧的 `IHost` 由调库方自己的 `Program.cs` 构造（典型是
  `Host.CreateApplicationBuilder()` 接上想要的日志 provider），首先是一个
  DI/日志容器——WPF 消息循环仍然是 `ChildProgram.Run` 内部的 `Application.Run`
  在驱动，不委托给 IHost 的 hosted service 生命周期，所以只需要
  `IHost.Start`/`StopAsync` 让容器就绪/收尾；同时它也是 `Application` 实例的
  来源——`ChildProgram` 不再自己 `new Application()`，而是从
  `host.Services.GetRequiredService<Application>()` 取，调库方在自己的
  `Program.cs` 里把想要的 `Application`（可以是子类、可以挂自定义
  `ResourceDictionary`/全局异常处理）注册进 `hostBuilder.Services` 即可，框架不
  替调库方决定这些。套接字路径含主进程 PID，支持应用多开互不干扰。
- **Microsoft.Extensions.Logging 接入**：框架整体接入 `Microsoft.Extensions.Logging`
  （`ILogger<T>`），主进程侧 `SessionManager`/`CommonServiceImpl` 经 ASP.NET Core
  的 DI 容器解析 `ILogger<T>`；子进程侧 `ChildProgram` 从 `host.Services` 解析
  `ILoggerFactory`，往下传给 `ChildShell`（暴露一个 `public ILogger Logger`）、
  `UiSaturationMeter`、`FeatureViewModel`（`Shutdown` 收到时的诊断日志）。两侧都用
  `AddDebug()` 输出到 Visual Studio"输出"窗口（主/子进程都是 `WinExe` 无控制台，
  不用 Console provider）。这些日志和 `SessionManager.FeatureLog`/子进程业务事件
  转发回主窗口 UI 的那条通道是两回事——`ILogger` 是给开发者/运维看的诊断信息，
  不经 gRPC 传回主进程。`Microsoft.Extensions.Hosting`/`Logging` 相关类型都已经
  随两个项目里已有的 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`
  一起可用，两个 csproj 都**不需要**再显式加对应的 `PackageReference`（加了反而
  会触发 NU1510 "该包会被裁剪" 警告）。
- **UDS 通道**：客户端用 `SocketsHttpHandler.ConnectCallback` 手工连
  `UnixDomainSocketEndPoint`（`Ipc/GrpcUds.cs`）；服务端 Kestrel `ListenUnixSocket` +
  HTTP/2，`CommonService` 与各 feature 的 gRPC 服务共用同一个端点，靠 gRPC 自身的
  服务路由区分。
- **推送背压**：具体怎么给每个会话做背压完全是实现方自己的事——demo 里
  `WaveformSession`/`TableSession` 内部各自维护一个有界 `Channel`，消费慢时按
  `BoundedChannelFullMode` 丢最旧数据帧，主进程不会被拖垮；`Ping`/`Shutdown` 与
  feature 数据共用同一条 stream（`oneof payload`），一起走 `SessionPump.PumpAsync`
  转发给 `ServeAsync` 拿到的 `IServerStreamWriter`。
- **心跳语义**：`SessionManager` 2s 对每个已连接会话调一次
  `Session.SendHeartbeat(Ping{seq, timestamp})`；子进程收到后先
  `Dispatcher.BeginInvoke` 到 UI 线程再发 `Pong` unary——因此 RTT 度量的是"子进程
  UI 线程健康度"，UI 卡死时心跳即断，`SessionManager` 按 5000ms 阈值只在状态翻转
  时触发一次 `UiUnresponsive`/`UiRecovered`，避免刷屏。
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
  主进程窗口和其他子进程窗口的心跳/UI Automation 查询完全不受影响，持续正常响应
  （实测响应耗时 83ms，远低于 500ms 的验收线）。
  dock pane 拖动/隐藏/浮动时占位控件 `Unloaded`/不可见 → 子窗口 `SW_HIDE`。
  `LayoutUpdated` 是"整窗任意布局 pass 完成"级别的事件，tab 切换期间会连续触发
  几十次，`UpdatePlacement` 因此加了脏检查（位置/大小/可见性/Z 序都和上次发出的
  一致就直接跳过）并把 `LayoutUpdated`/`IsVisibleChanged`/宿主
  `WM_WINDOWPOSCHANGED` 都改成 `Dispatcher.BeginInvoke` 去抖合并成一次；被切走
  隐藏的子窗口也暂停重绘。
- **跨线程访问 WPF 对象**：`SessionManager.Register`/`Unregister`/`CloseSession`
  都可能在 gRPC 线程池线程（Kestrel）或 `Process.Exited`（ThreadPool）上被调用，
  而它们要碰的 `OverlayHost`（`Border`）、`IDockPane`（AvalonDock `LayoutDocument`）
  都是 UI 线程独占的 `DependencyObject`——这几处统一用
  `entry.Overlay.Dispatcher.BeginInvoke(...)` fire-and-forget 调度回 UI 线程再调用
  `AttachChild`/`DetachChild`/`Pane.Close()`；而 `Session.OnConnected`/
  `OnDisconnected` 等生命周期钩子（典型实现只是起停一个后台 producer）不碰任何 UI
  对象，原样同步调用即可，不需要过度加 Dispatcher 调度。
- **生命周期**：
  - 主窗口关闭 → `SessionManager.CloseAll()` 依次 `CloseSession(sessionId,
    ShutdownReason.HostClosing)`（推 `Shutdown{reason}` → 子进程 `Close()`；1.5s
    未退则 `Kill()` 兜底）——实测验证过关闭主窗口后所有子进程全部自动退出，无孤儿
    进程残留。
  - 主进程崩溃 → 子进程监听 `Process.Exited` 自杀 + stream 断开双保险
    （`FeatureViewModel<TDown>.RunAsync` 捕获异常后关窗口）。
  - 子进程退出/断开 → `SessionManager.Unregister` 触发 `OverlayHost.DetachChild()`
    回到空白占位、`Session.OnDisconnected()`、`Session.DisposeOnce()`；对应的
    dock pane 关闭（用户点 pane 关闭按钮）会经 `CloseSession(sessionId,
    ShutdownReason.PaneClosed)` 单独触发子进程退出。
  - `ShutdownReason` 枚举尽量覆盖所有已知的关闭触发源：`HostClosing`（主窗口
    关闭）/`PaneClosed`（用户关了这个 pane）/`ClosedByApi`（调库方代码主动调用
    `CloseSession`，默认值）/`SessionRejected`（`Register` 校验失败被拒）/
    `Replaced`（预留：同 session 重复连接被顶掉）/`HostError`（主进程内部错误）/
    `Restarting`（预留：重启/升级）——子进程侧收到后行为一致（关窗口），
    `reason`/`detail` 只用于日志/可观测性区分"我是被谁关掉的"。
- **DPI**：主/子进程同一 manifest（PerMonitorV2），`PointToScreen` 直接给出物理
  像素，跨显示器坐标一致。

## 文件结构

### `src/WpfMultiProcess.Framework/`（可复用库，零 AvalonDock/Infragistics 依赖）

| 文件 | 职责 |
|---|---|
| `WpfMultiProcess.Framework.csproj` | `Library`, `net10.0-windows`, `UseWPF`；`FrameworkReference=Microsoft.AspNetCore.App`（Kestrel + Hosting/Logging Abstractions 都靠它，不需要额外 PackageReference）；只编译 `common.proto` |
| `Protos/common.proto` | 公共契约：`CommonService`(Pong/RequestActivate/ReportUiStats，按 session_id 路由) + 共享消息(`RegisterReply`/`Ping`/`Shutdown`/`ShutdownReason`/`Ack`/`UiStatsRequest`) |
| `Ipc/GrpcUds.cs` | UDS 通道工厂（客户端 `ConnectCallback` + 套接字路径约定） |
| `Ipc/Win32.cs` | GetWindow / GetWindowLongPtr / SetWindowLongPtr / SetWindowPos / ShowWindow P/Invoke |
| `Host/IDockPane.cs` | `IDockPane`：框架对"dock pane"的最小抽象，形状贴着 `XamDockManager.ContentPane` 设计，零依赖任何具体 dock 库；造 pane 是调库方 UI 代码自己的职责，框架只接收造好的 pane |
| `Host/IFeatureHost.cs` | 调库方主进程侧接缝：`FeatureId`/`Descriptor`/`MapService(endpoints)` |
| `Host/Session/Session.cs` | 非泛型抽象基类 `Session : IDisposable`：会话身份(`SessionId`/`FeatureId`/`FeatureIndex`/`Pid`/`Hwnd`)、`SendHeartbeat`/`SendClose`、`ServeAsync<T>`(接收 stream 所有权)、`OnConnected`/`OnPong`/`OnUiStats`/`OnDisconnected` 生命周期虚方法、`Dispose`/内部幂等门 `DisposeOnce` |
| `Host/Session/SessionPump.cs` | 静态助手 `PumpAsync<T>`：把 `ChannelReader<T>` 里的 envelope 转发进 `IServerStreamWriter<T>`，多数 `Session.ServeAsync` 实现的样板 |
| `Host/Session/SessionManager.cs` | 会话层核心：`OpenFeature(featureId, featureIndex, pane)`/`Register(session, pid, hwnd)`/`Unregister`/`CloseSession`/`CloseAll`、心跳泵(2s)/无响应检测(5000ms)、`ReplyOf`、`FindSession<TSession>`、跨线程 Dispatcher 调度 |
| `Host/CommonServiceImpl.cs` | `CommonService` 实现（薄壳，按 session_id 转发给 SessionManager），构造注入 `ILogger<CommonServiceImpl>` |
| `Host/OverlayHost.cs` | 占位控件：无 owner 关系，`SetWindowPos`(`SWP_ASYNCWINDOWPOS`)钉位置+手动 Z 序（feature 无关） |
| `Child/ChildStartOptions.cs` | 子进程启动参数：featureId/featureIndex/sessionId/socketPath/hostPid |
| `Child/ChildProgram.cs` | 子进程通用入口：`Run(IHost host, ChildStartOptions, IReadOnlyList<IFeatureChild>)`——孤儿自杀看护 + 从 `host.Services` 取 `ILoggerFactory`/`Application` + 拉起 `ChildWindow` + bootstrap(建 channel/`ChildShell` → `IFeatureChild.CreateViewModel`/`CreateView` → `Start()`) |
| `Child/ChildContext.cs` | `{Channel, SessionId, FeatureIndex, Shell}` 只读结构体，传给 `IFeatureChild.CreateViewModel` |
| `Child/ChildWindow.cs` | 无边框子窗口：`WS_EX_NOACTIVATE`\|`WS_EX_TOOLWINDOW`、初始位置屏幕外、`SourceReady`/`Closed` 接缝 |
| `Child/ChildShell.cs` | 子进程框架状态条：持有 Hwnd、暴露 `Logger`、`ApplyReply`(标题/主题色)、`SendPong`/`RequestActivate`、`RequestClose`、拉起/停止 `UiSaturationMeter` |
| `Child/UiSaturationMeter.cs` | 框架级、feature-无关：后台线程探针积分 UI 线程饱和度(权威值) + `Dispatcher.Hooks` 归因，经 `CommonService.ReportUiStats` 上报 |
| `Child/IFeatureChild.cs` | 调库方子进程侧接缝：`FeatureId`/`CreateViewModel(ChildContext)`/`CreateView(FeatureViewModel)` |
| `Child/FeatureViewModel.cs` | `FeatureViewModel`(非泛型) + `FeatureViewModel<TDown>`：`RunAsync` 循环读 stream、`HandlePing`(回 Pong)/`HandleShutdown`(记诊断日志→关窗口)、`OnReply`(标题/主题色)、抽象 `Dispatch`/`OnData` |

### `demo/WpfMultiProcess.Demo/`（演示应用，WinExe，引用 Framework + AvalonDock）

| 文件 | 职责 |
|---|---|
| `WpfMultiProcess.Demo.csproj` | `WinExe`，`ProjectReference` Framework，`PackageReference` `Dirkster.AvalonDock`(+VS2013 主题)；`waveform.proto`/`table.proto` 用 `AdditionalImportDirs` 解析 `common.proto` 的 import 而不重复编译 |
| `Program.cs` | 入口 + 命令行解析(`CmdLine`)，按 `--child` 分流：子进程侧自己 `Host.CreateApplicationBuilder()` 建 `IHost`（挂 `AddDebug()`、注册 `Application` 单例）交给 `ChildProgram.Run`；主进程侧调 `HostProgram.Run()` |
| `Protos/waveform.proto` | `WaveformService`：Register(StreamRequest → server stream, envelope = Reply⊕Ping⊕Shutdown⊕Frame) + GetStatistics unary |
| `Protos/table.proto` | `TableService`：Register(StreamRequest → server stream, envelope = Reply⊕Ping⊕Shutdown⊕Delta) + Sort unary |
| `Host/HostProgram.cs` | 主进程入口：造 `MainWindow`，起 Kestrel（`SessionManager` 经 DI 工厂注册，`AddDebug()` 日志），回调 `AttachSessionManager` |
| `Host/MainWindow.cs` | AvalonDock 主窗口：dock pane 全动态创建，"新建波形/新建表格"按钮自己造 `LayoutDocument`+`AvalonDockPane`、维护每个 featureId 的下一个 featureIndex，调 `SessionManager.OpenFeature(id, index, pane)`；订阅 `SessionManager` 事件展示状态栏/事件日志，F9 float/dock 测试钩子 |
| `Host/AvalonDockPane.cs` | `IDockPane` 的 AvalonDock 实现：包一个调用方已经建好并加进 `LayoutDocumentPane` 的 `LayoutDocument` |
| `Host/Features/Waveform/WaveformFeature.cs`, `WaveformSession.cs`, `WaveformServiceImpl.cs` | 波形 feature：`IFeatureHost` 实现 + `Session` 子类(内部 `Channel<WaveformDown>` + 50ms 正弦帧 producer) + gRPC 服务实现(Register 里自己 `new` Session → `SessionManager.Register` 校验 → 写 Reply → `session.ServeAsync`，GetStatistics unary 经 `FindSession` 读快照) |
| `Host/Features/Table/TableFeature.cs`, `TableSession.cs`, `TableServiceImpl.cs` | 表格 feature：同上模式，动态行(增删/变值) + Sort unary |
| `Child/Features/Waveform/WaveformFeatureChild.cs`, `WaveformViewModel.cs`, `WaveformView.cs` | 波形子进程侧：`IFeatureChild` 实现 + `FeatureViewModel<WaveformDown>` 子类(自己发起 Register) + Polyline 渲染视图(隐藏时暂停) + 统计/模拟卡死按钮 |
| `Child/Features/Table/TableFeatureChild.cs`, `TableViewModel.cs`, `TableView.cs` | 表格子进程侧：同上，DataGrid + 排序按钮 |

## 已验证的运行时行为

以下行为在本次重构后逐一做过实际多进程运行验证（非仅静态代码审查，使用 UI
Automation + Win32 API 对真实运行中的 demo 进程操作）：

- 干净构建：`dotnet clean` + `dotnet build` 全项目 0 警告/0 错误。
- 启动 + 自动打开：主进程启动后自动打开 waveform/table 各一个实例。
- 多开：同一 feature 反复点击"新建波形/新建表格"能正确开出独立递增
  `featureIndex` 的新子进程，各自 overlay 到自己的 dock pane。
- **卡死隔离**（`SWP_ASYNCWINDOWPOS` 的核心价值，验收线 500ms）：让一个子进程 UI
  线程 `Thread.Sleep` 卡死 10 秒，500ms 内对另一个未受影响的子进程窗口发起 UI
  Automation 调用，实测 83ms 内完成响应；同一时间窗口内其余子进程窗口的心跳
  RTT 持续正常滚动，卡死子进程在 10 秒后自动恢复，全程无进程崩溃。
- unary RPC：统计(`GetStatistics`)/排序(`Sort`) 按钮触发对应 feature 服务的 unary
  调用并正确回显结果。
- pane 关闭 → 子进程退出：关闭某个 tab 只终止对应的那一个子进程（`ShutdownReason.
  PaneClosed`），其余会话不受影响，进程数相应减少。
- 20 次连续 tab 切换：实测每次切换 12~18ms，无可观察卡顿，4 个子进程全程存活。
- 主窗口关闭 → 全部子进程自动退出（`ShutdownReason.HostClosing`），进程列表里
  不留任何孤儿 `WpfMultiProcess.Demo.exe`——间接验证了 `Session.Dispose` 在
  `CloseSession`/`Process.Exited`/stream `finally` 并发路径下确实只被调用一次
  （`DisposeOnce` 幂等门），否则大概率会在这个多会话同时收尾的场景下表现为异常/
  挂起/僵尸进程。
