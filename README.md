# WpfMultiProcess — WPF 多进程框架

单一项目同时充当主进程与子进程（命令行参数区分），gRPC over Unix Domain Socket 通信，
子进程窗口通过 SetWindowPos（位置+手动 Z 序，不建立 owner 关系）overlay 到主进程
VS 风格 dock pane 的占位区域上。

```
dotnet run            # 主进程（自动拉起两个子进程: waveform / table）
```

子进程由主进程自动启动，参数形如：

```
WpfMultiProcess.exe --child --feature=waveform --socket=%TEMP%\wpfmp-<hostpid>.sock --hostpid=<hostpid>
```

## 架构

```
┌────────────────────── 主进程 (gRPC Server, Kestrel/UDS) ──────────────────────┐
│  MainWindow (AvalonDock VS2013 主题)                                          │
│    ├── LayoutDocument "waveform" → OverlayHost(空白占位)                      │
│    ├── LayoutDocument "table"    → OverlayHost(空白占位)                      │
│    └── 事件日志 anchorable + 心跳状态栏                                        │
│  HostCoordinator: 每 feature 一个有界 Channel<ServerMessage>                  │
│    ├── DataLoop   50ms  → FeatureData(server stream 推送)                    │
│    └── Heartbeat  2s    → Ping{seq, timestamp}(同一条 stream 推送)           │
└──────────────────────────────┬───────────────────────────────────────────────┘
                    UDS: %TEMP%\wpfmp-<pid>.sock (HTTP/2)
┌──────────────────────────────┴─────────────── 子进程 (gRPC Client) ──────────┐
│  ChildWindow(WindowStyle=None, ShowInTaskbar=false, 初始位置屏幕外)           │
│    1. unary GetInitParams   → 标题/主题色/配置                                │
│    2. Subscribe server stream → FeatureData / Ping / Shutdown                │
│    3. unary RegisterWindow(hwnd) → 主进程手动 Z 序 + overlay                 │
│    4. Ping 到达 → Dispatcher 调度到 UI 线程 → unary Pong(回显时间戳)          │
│    5. 按钮点击 → unary ReportInteraction                                     │
└──────────────────────────────────────────────────────────────────────────────┘
```

## 关键设计点

- **进程模型**：`Program.Main` 按 `--child` 分流到 `HostProgram` / `ChildProgram`。
  套接字路径含主进程 PID，支持应用多开互不干扰。
- **UDS 通道**：客户端用 `SocketsHttpHandler.ConnectCallback` 手工连 `UnixDomainSocketEndPoint`
  （`Ipc/GrpcUds.cs`）；服务端 Kestrel `ListenUnixSocket` + HTTP/2。
- **推送背压**：每个订阅一个 `BoundedChannel(256, DropOldest)`，子进程消费慢时丢最旧数据帧，
  主进程不会被拖垮；Ping/Shutdown 与数据共用一条 stream（`oneof payload`）。
- **心跳语义**：主进程 2s 推一次 `Ping{seq, timestamp}`；子进程收到后先 `Dispatcher.BeginInvoke`
  到 UI 线程再发 `Pong` unary——因此 RTT 度量的是"子进程 UI 线程健康度"，UI 卡死时心跳即断。
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
  代价是点击子窗口不会带起主窗口，用一次 `ReportInteraction("focus_request", ...)`
  上报换取主窗口 `Activate()` 补偿。**关键坑**：光去掉 owner 关系还不够——
  `SetWindowPos`/`ShowWindow` 对不同线程（含跨进程）的窗口默认会像 `SendMessage`
  一样同步阻塞发消息，子窗口卡死时仍会拖住调用方所在的主进程 UI 线程（实测
  子窗口卡死几秒后主窗口对 `SendMessageTimeout` 也会短暂无响应）。必须加上
  `SWP_ASYNCWINDOWPOS`（隐藏时用 `SetWindowPos`+`SWP_HIDEWINDOW` 代替
  `ShowWindow(SW_HIDE)`）让请求改为 post 给目标线程、调用方立即返回，才是
  真正隔离卡死影响的关键。
  dock pane 拖动/隐藏/浮动时占位控件 `Unloaded`/不可见 → 子窗口 `SW_HIDE`。
- **生命周期**：
  - 主窗口关闭 → stream 推 `Shutdown` → 子进程 `Close()`；1.5s 未退则 `Kill()` 兜底。
  - 主进程崩溃 → 子进程监听 `Process.Exited` 自杀 + stream 断开双保险。
  - 子进程退出/断开 → `OverlayHost.DetachChild()` 回到空白占位。
- **DPI**：主/子进程同一 manifest（PerMonitorV2），`PointToScreen` 直接给出物理像素，
  跨显示器坐标一致。

## 文件结构

| 文件 | 职责 |
|---|---|
| `Protos/ipc.proto` | 服务契约：Subscribe(server stream) + 4 个 unary |
| `Program.cs` | 入口 + 命令行解析 |
| `Ipc/GrpcUds.cs` | UDS 通道工厂（socket 路径约定） |
| `Ipc/Win32.cs` | GetWindow / GetWindowLongPtr / SetWindowLongPtr / SetWindowPos / ShowWindow P/Invoke |
| `Host/HostProgram.cs` | Kestrel 启动 + WPF 消息循环 + 清理 |
| `Host/HostCoordinator.cs` | 订阅表、数据泵、心跳泵、上行事件分发 |
| `Host/IpcService.cs` | gRPC 服务实现（薄壳，状态在 Coordinator） |
| `Host/MainWindow.cs` | AvalonDock 布局、子进程启动/回收、日志 |
| `Host/OverlayHost.cs` | 占位控件：无 owner 关系,SetWindowPos(异步)钉位置+Z 序 |
| `Child/ChildProgram.cs` | 子进程入口 + 孤儿自杀 |
| `Child/ChildIpcClient.cs` | stream 订阅 + unary 封装 |
| `Child/ChildWindow.cs` | 无边框窗口、波形渲染、UI 线程 Pong |
