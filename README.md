# WpfMultiProcess — WPF 多进程框架

单一项目同时充当主进程与子进程（命令行参数区分），gRPC over Unix Domain Socket 通信，
子进程窗口通过 SetWindowPos（位置+手动 Z 序，不建立 owner 关系）overlay 到主进程
VS 风格 dock pane 的占位区域上。

```
dotnet run            # 主进程（自动拉起两个子进程: waveform / table）
```

子进程由主进程自动启动，参数形如：

```
WpfMultiProcess.exe --child --feature=waveform --session=<guid> --socket=%TEMP%\wpfmp-<hostpid>.sock --hostpid=<hostpid>
```

`session_id` 由**主进程生成**（`MainWindow.LaunchChild` 里 `Guid.NewGuid()`），作为启动参数
传给子进程，并在拉起子进程之前先调用 `SessionHub.Prepare(sessionId, featureId)` 登记
"这个 session_id 属于哪个 feature"——子进程不再需要一次单独的 RPC 去问"我是谁"。

## 架构

框架分两层：一层**共享公共服务**（`CommonService`：只剩 `Pong`/`RequestActivate` 两个
按 `session_id` 路由的 unary，注册/开窗环节已经合并进各 feature 自己的流里），
每个 feature 之上再挂一个**独立的 gRPC 服务**（`WaveformService`/`TableService`……），
各自的 `Register(StreamRequest{session_id, hwnd, pid})` 返回强类型 server stream，
stream 里用 `oneof` 把开场的 `RegisterReply`（标题/主题色）、公共 `Control`
（Ping/Shutdown）和 feature 自己的数据帧（`WaveformFrame`/`TableDelta`）三路复用进
同一个 envelope 里推送——新增一个 feature 只需要新增一个 proto + 一对 Host/Child
模块，不需要改动公共服务或其他 feature。

```
┌───────────────────────── 主进程 (gRPC Server, Kestrel/UDS) ──────────────────────────┐
│  MainWindow (AvalonDock VS2013 主题,tab 列表来自 HostFeatureRegistry.Modules)         │
│    ├── LaunchChild: 生成 session_id → hub.Prepare(id, featureId) → 传 --session 启动  │
│    ├── LayoutDocument "waveform" → OverlayHost(空白占位)                              │
│    ├── LayoutDocument "table"    → OverlayHost(空白占位)                              │
│    └── 事件日志 anchorable + 心跳状态栏（订阅 SessionHub 的事件）                      │
│                                                                                        │
│  CommonServiceImpl(公共服务,feature 无关)          HostFeatureRegistry             │
│    Pong/RequestActivate/ReportUiStats → 按 session_id 查表 ├── WaveformHostModule    │
│         ↓ 委托                                       │      → WaveformServiceImpl    │
│  SessionHub                                          │         Register: TryOpen →   │
│    ├── Prepare(id,featureId) 预登记 / TryOpen 校验并落 hwnd  写 Reply → 50ms 正弦帧    │
│    ├── Heartbeat 2s → Control{Ping} 推给所有订阅       │         + min/max/avg/count  │
│    ├── CheckUnresponsive(5000ms 阈值)→ UiUnresponsive/UiRecovered  └── TableHostModule│
│    └── AttachStream/DetachStream(Subscription<TEnv>,每 feature 一个有界 channel)     │
│                 → TableServiceImpl(Register: TryOpen → 写 Reply → 8 行动态表          │
│                    + Sort unary + upsert/remove/reorder)                              │
└──────────────────────────────────────────┬───────────────────────────────────────────┘
                                UDS: %TEMP%\wpfmp-<hostpid>.sock (HTTP/2,多服务共用一个端点)
┌──────────────────────────────────────────┴────────────── 子进程 (gRPC Client) ───────┐
│  ChildShell(WindowStyle=None, ShowInTaskbar=false, 初始位置屏幕外,框架级状态条)        │
│  session_id 来自 --session 启动参数,不再有独立会话对象；同一个 Channel 上多建一个       │
│  CommonServiceClient 供 Pong/RequestActivate/ReportUiStats 使用                       │
│    1. SourceInitialized: 拿到 hwnd → 建 Channel + CommonServiceClient → 拉起          │
│       UiSaturationMeter(探针+hooks 上报 UI 饱和度) → ChildContext                     │
│    2. ChildFeatureRegistry.Get(featureId).CreateView(ctx) → feature 视图自己开流       │
│    3. feature 视图: WaveformService/TableService.Register(session_id, hwnd, pid) 开流  │
│         → down.Reply    → ChildShell.ApplyReply(标题/主题色)                          │
│         → down.Control  → ChildShell.OnPing(转发 CommonService.Pong) / RequestClose   │
│         → down.Frame/Delta → 更新 UI(波形折线 / DataGrid 行)                          │
│    4. 点击子窗口 → ChildShell.RequestActivate() → CommonService.RequestActivate       │
│    5. feature 按钮(统计/排序) → 各自 feature 服务的 unary RPC                          │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

## 关键设计点

- **可插拔的多 feature 架构**：公共服务（`CommonService`：只剩 `Pong`/`RequestActivate`，
  都按 `session_id` 路由，不知道也不关心是哪个 feature）与具体业务完全解耦；会话的
  建立（原来独立的 `Register`/`RegisterWindow`）合并进了每个 feature 自己 `Register`
  的开流请求里（`StreamRequest{session_id, hwnd, pid}`）——`session_id` 由主进程作为
  启动参数下发、`SessionHub.Prepare` 预登记它归属哪个 feature，子进程开流时报上
  `hwnd`/`pid`，`SessionHub.TryOpen` 校验 session_id 确实是给这个 feature 预留的之后
  才算真正建立会话、落地 hwnd 触发 overlay。每个 feature 各自拥有一个独立 gRPC 服务
  （`WaveformService`/`TableService`……），其 `Register` 返回的强类型 stream 用
  `oneof` 三路复用：开场先写一帧 `RegisterReply`（标题/主题色，从
  `SessionHub.ReplyOf(featureId)` 取），随后是公共 `Control`（Ping/Shutdown）和
  feature 自己的数据帧，全部封装在同一个 envelope 里。主/子进程各有一个对称的
  wrap/unwrap 接缝：主进程侧 `Subscription<TEnv>` 用构造时传入的
  `Func<Control,TEnv> wrap` 把心跳/关闭包成 feature 自己的 envelope 类型再推流；
  子进程侧每个 feature 视图从自己的 envelope 里把 `Control` 解出来，统一转发给
  `ChildShell.OnPing`/`RequestClose`（回 Pong 时走的是同一个 Channel 上另建的
  `CommonServiceClient`，一次普通的 fire-and-forget unary，不是往 stream 里写，没有
  并发写的坑；回 Pong / 关窗口的行为与具体 feature 完全无关）。子进程不再有独立的
  会话对象——`ChildContext`（`Channel`/`SessionId`/`Shell`）只是把这几样东西打包传给
  `IFeatureChildModule.CreateView`，feature 视图自己攥着 stream 生命周期。主进程侧
  `HostFeatureRegistry`、子进程侧 `ChildFeatureRegistry` 各自维护"featureId → 模块"的
  映射，`MainWindow` 的 tab 列表和子进程的视图构造都从各自 registry 里迭代/查表
  得到——新增一个 feature 只需要新增一个 proto + 一对 Host/Child 模块并注册进
  registry，不需要改动公共服务、`MainWindow`、`ChildShell` 或其他 feature 的任何
  代码。
- **进程模型**：`Program.Main` 按 `--child` 分流到 `HostProgram` / `ChildProgram`。
  套接字路径含主进程 PID，支持应用多开互不干扰。
- **UDS 通道**：客户端用 `SocketsHttpHandler.ConnectCallback` 手工连 `UnixDomainSocketEndPoint`
  （`Ipc/GrpcUds.cs`）；服务端 Kestrel `ListenUnixSocket` + HTTP/2，`CommonService` 与
  各 feature 的 gRPC 服务共用同一个端点，靠 gRPC 自身的服务路由区分；子进程侧同一个
  `GrpcChannel` 上既建 feature 的 stream 客户端，也建 `CommonServiceClient`。
- **推送背压**：`SessionHub.AttachStream` 为每个会话的每个订阅建一个
  `Subscription<TEnv>`，内部是一个 `BoundedChannel(256, DropOldest)`，子进程消费慢时
  丢最旧数据帧，主进程不会被拖垮；Ping/Shutdown 与 feature 数据共用同一条 stream
  （`oneof payload`）。
- **心跳语义**：`SessionHub` 2s 推一次 `Control{Ping{seq, timestamp}}` 给所有 feature 的
  所有订阅；子进程收到后先 `Dispatcher.BeginInvoke` 到 UI 线程再发 `Pong` unary——
  因此 RTT 度量的是"子进程 UI 线程健康度"，UI 卡死时心跳即断，`SessionHub.CheckUnresponsive`
  按 5000ms 阈值只在状态翻转时触发一次 `UiUnresponsive`/`UiRecovered`，避免刷屏。
- **UI 线程饱和度**：心跳只能判断"彻底失联"，`Child/UiSaturationMeter.cs`（框架级、
  feature-无关，挂在 `ChildShell` 上）额外给出一个 0..100 的"饱和度"百分数——不是
  CPU 占用率，而是 UI 线程还有没有空闲容量及时执行低优先级回调。两部分：探针
  （权威值）由专用后台线程用 `Stopwatch` 自驱动，维持"恰好一个"未决的
  `Dispatcher.BeginInvoke(Background, ...)` 探针，post 时刻记下来，超过约 8ms
  grace 之后每步（~15ms）把这段墙钟切片积进 `_busyMs`，探针一旦被执行立刻重发下一个；
  UI 线程彻底卡死时探针永远不会被执行，整段卡死墙钟原样计入——且这个累加动作是
  后台线程自己做的，完全不依赖 UI 线程，所以卡死**进行中**就能报出接近 100% 的饱和度，
  不用等卡死结束，每满 1s 窗口按 busy/窗口墙钟算一次百分比上报。hooks（归因）在 UI
  线程注册 `Dispatcher.Hooks`（`OperationPosted`/`Started`/`Completed`/`Aborted`），
  统计 dispatcher 忙时占比、最长排队延迟（`maxQueueLatencyMs`）、最长单次操作
  （`longestOpMs`，用 depth 计数应对嵌套操作）、操作数（`opCount`）——能定性"忙在
  哪个操作上"，但不是权威值，若操作仍在飞（`_depth>0`）后台线程取快照时会临时把
  "到目前为止"的时长也计入，让归因侧在卡死进行中也能看出苗头。两部分都经
  `CommonService.ReportUiStats`（按 session_id）fire-and-forget 上报，和 `Pong`
  共用同一个 `CommonServiceClient`；`SessionHub.OnUiStats` 按 session_id 找 featureId
  抛出 `UiStatsReceived` 事件，`MainWindow` 订阅后在状态栏展示每个 feature 最新一行
  （饱和度/dispatcher 忙时/队列延迟/最长操作），并在 saturation_pct 持续 >80% 时
  记一条事件日志（状态翻转或每 ~2s 一条，不刷屏）。
- **窗口嵌入**：不用 `SetParent`，也**不用** `SetWindowLongPtr(GWLP_HWNDPARENT)` 做
  owner 关系——早期方案曾用 owner，但跨进程 owner/SetParent 都会让 Windows 隐式合并
  两个线程的输入队列（等效 `AttachThreadInput`），一旦子进程 UI 线程卡死，主进程和
  另一个子进程窗口的输入会被一起冻住，代价无法接受。现改为子窗口与宿主**没有任何
  系统级关系**：`OverlayHost` 只靠 `SetWindowPos` 持续把子窗口钉在占位控件的屏幕矩形
  上（`LayoutUpdated`/`LocationChanged`/`StateChanged`/宿主 `WM_WINDOWPOSCHANGED` 驱动），
  并且每次都显式算一遍 `hWndInsertAfter`（取宿主 `GW_HWNDPREV` 紧邻的窗口）手动把子
  窗口插到宿主正上方，靠"持续纠正 Z 序"代替 owner 关系。子窗口自身加
  `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` 并拦截 `WM_MOUSEACTIVATE` 返回
  `MA_NOACTIVATE`：点击不激活、不进 Alt-Tab/任务栏、也不会自己扰乱这里维护的 Z 序；
  代价是点击子窗口不会带起主窗口，用一次 `CommonService.RequestActivate` unary
  （按 session_id 上报）换取主窗口 `Activate()` 补偿。**关键坑**：光去掉 owner 关系还不够——
  `SetWindowPos`/`ShowWindow` 对不同线程（含跨进程）的窗口默认会像 `SendMessage`
  一样同步阻塞发消息，子窗口卡死时仍会拖住调用方所在的主进程 UI 线程（实测
  子窗口卡死几秒后主窗口对 `SendMessageTimeout` 也会短暂无响应）。必须加上
  `SWP_ASYNCWINDOWPOS`（隐藏时用 `SetWindowPos`+`SWP_HIDEWINDOW` 代替
  `ShowWindow(SW_HIDE)`）让请求改为 post 给目标线程、调用方立即返回，才是
  真正隔离卡死影响的关键。
  dock pane 拖动/隐藏/浮动时占位控件 `Unloaded`/不可见 → 子窗口 `SW_HIDE`。
  `LayoutUpdated` 是"整窗任意布局 pass 完成"级别的事件，tab 切换期间会连续
  触发几十次，`UpdatePlacement` 因此加了脏检查（位置/大小/可见性/Z 序都和
  上次发出的一致就直接跳过）并把 `LayoutUpdated`/`IsVisibleChanged`/宿主
  `WM_WINDOWPOSCHANGED` 都改成 `Dispatcher.BeginInvoke` 去抖合并成一次；
  被切走隐藏的子窗口也暂停重绘——否则异步 `SetWindowPos` 请求会在子进程
  消息队列里越积越多，最新位置反而要排在最后处理，表现为跟随明显变慢。
- **生命周期**：
  - 主窗口关闭 → `SessionHub.PushShutdownAll()` 给每个订阅推 `Control{Shutdown}` →
    子进程 `Close()`；1.5s 未退则 `Kill()` 兜底。
  - 主进程崩溃 → 子进程监听 `Process.Exited` 自杀 + stream 断开双保险。
  - 子进程退出/断开 → `OverlayHost.DetachChild()` 回到空白占位。
- **DPI**：主/子进程同一 manifest（PerMonitorV2），`PointToScreen` 直接给出物理像素，
  跨显示器坐标一致。

## 文件结构

| 文件 | 职责 |
|---|---|
| `Protos/common.proto` | 公共契约：`CommonService`(Pong/RequestActivate/ReportUiStats，按 session_id 路由) + 共享消息(`RegisterReply`/`Control`(Ping/Shutdown)/`Ack`/`UiStatsRequest`) |
| `Protos/waveform.proto` | `WaveformService`：Register(StreamRequest{session_id,hwnd,pid} → server stream, envelope = Reply⊕Control⊕Frame) + GetStatistics unary |
| `Protos/table.proto` | `TableService`：Register(StreamRequest{session_id,hwnd,pid} → server stream, envelope = Reply⊕Control⊕Delta) + Sort unary |
| `Program.cs` | 入口 + 命令行解析(`CmdLine` 含 `SessionId`，来自 `--session`) |
| `Ipc/GrpcUds.cs` | UDS 通道工厂（socket 路径约定） |
| `Ipc/Win32.cs` | GetWindow / GetWindowLongPtr / SetWindowLongPtr / SetWindowPos / ShowWindow P/Invoke |
| `Host/HostProgram.cs` | Kestrel 启动 + 服务注册(CommonService + 各 feature 服务) + WPF 消息循环 + 清理 |
| `Host/Session/Subscription.cs` | 订阅句柄：`Subscription<TEnv>` 持有有界 channel + `Func<Control,TEnv> wrap` |
| `Host/Session/SessionHub.cs` | `Prepare`/`TryOpen` session_id↔featureId 映射与校验、`ReplyOf`、心跳泵(2s)/无响应检测(5000ms)、订阅表(Attach/DetachStream)、上行事件(含 `OnUiStats`/`UiStatsReceived`) |
| `Host/CommonServiceImpl.cs` | `CommonService` 实现（薄壳，Pong/RequestActivate/ReportUiStats 按 session_id 转发给 SessionHub） |
| `Host/IFeatureHostModule.cs` | feature 主进程侧模块契约：FeatureId/Descriptor/Map(endpoints) |
| `Host/HostFeatureRegistry.cs` | 持有全部 `IFeatureHostModule`，`MainWindow`/`HostProgram` 据此迭代 |
| `Host/Features/Waveform/WaveformHostModule.cs`, `WaveformServiceImpl.cs` | 波形 feature：Register 先 TryOpen 再写 Reply，随后 50ms 正弦帧 + min/max/avg/count 统计 |
| `Host/Features/Table/TableHostModule.cs`, `TableServiceImpl.cs` | 表格 feature：Register 先 TryOpen 再写 Reply，随后动态行(增删/变值) + Sort unary |
| `Host/MainWindow.cs` | AvalonDock 布局(tab 来自 registry)、`LaunchChild` 生成 session_id 并 `hub.Prepare`、订阅 SessionHub 事件(含 UI 饱和度状态栏 + 持续高位记日志)、日志、F9 float/dock |
| `Host/OverlayHost.cs` | 占位控件：无 owner 关系,SetWindowPos(异步)钉位置+Z 序（feature 无关，未改动） |
| `Child/ChildProgram.cs` | 子进程入口 + 孤儿自杀 |
| `Child/ChildContext.cs` | `{Channel, SessionId, Shell}` 只读结构体，传给 `IFeatureChildModule.CreateView`；子进程不再有独立会话对象 |
| `Child/ChildShell.cs` | 无边框窗口框架：持有 Channel + `CommonServiceClient`（Pong/RequestActivate/ReportUiStats）+ Win32 摆位/激活拦截 + 状态条 + 拉起/停止 `UiSaturationMeter`，中间区域交给 feature 视图自己开流 |
| `Child/UiSaturationMeter.cs` | 框架级、feature-无关：后台线程 Background 优先级探针积分 UI 线程饱和度(权威值) + `Dispatcher.Hooks` 归因(忙时/队列延迟/最长操作)，经 `CommonService.ReportUiStats` 每窗口(~1s)上报 |
| `Child/IFeatureChildModule.cs` | feature 子进程侧模块契约：FeatureId/CreateView(ChildContext) |
| `Child/ChildFeatureRegistry.cs` | 持有全部 `IFeatureChildModule`，`ChildShell` 据 `--feature` 查表 |
| `Child/Features/Waveform/WaveformChildModule.cs`, `WaveformView.cs` | 波形视图：自己开 `WaveformService.Register` 流，demux Reply/Control/Frame，Polyline 渲染(隐藏时暂停) + 统计按钮 |
| `Child/Features/Table/TableChildModule.cs`, `TableView.cs` | 表格视图：自己开 `TableService.Register` 流，demux Reply/Control/Delta，DataGrid + 排序按钮 |
