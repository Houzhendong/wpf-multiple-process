using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Host.Session;

/// <summary>启动一个子进程需要的固定参数:可执行文件路径 + UDS 套接字路径。
/// 由宿主应用(demo 的 HostProgram)在拉起 Kestrel 之后构造好传给 SessionManager。</summary>
public sealed record SessionLaunchOptions(string ExecutablePath, string SocketPath);

/// <summary>
/// 会话层核心(演进自原 SessionHub,原地改名并承担了原来 MainWindow.LaunchChild +
/// HostFeatureRegistry 的职责):调库方注册好 <see cref="IFeatureHost"/> 列表之后,
/// "开一个 feature 实例"这件事(生成 session_id、造 OverlayHost、拉起子进程、预登记)
/// 由 <see cref="OpenFeature"/> 一次调用完成——featureIndex 和 <see cref="IDockPane"/>
/// 都由调用方自己决定/造好再传进来(不再有 IDockWorkspace 这层,SessionManager 不
/// 负责"造 pane"),支持同一 feature 反复调用多开出独立的会话/独立的子进程,但同一
/// (featureId, featureIndex) 不能同时有两个活跃会话。
///
/// 会话建立的顺序整个反过来了:<see cref="OpenFeature"/> 这时候还不知道具体
/// feature 的 Session 长什么样(<see cref="IFeatureHost"/> 已经不再有
/// CreateSession 这个方法了),它只预登记 sessionId/featureId/featureIndex 这几个
/// feature-无关的元数据 + 拉起子进程。真正的 <see cref="Session"/> 对象,是子进程
/// 连上来发起 Register 时,由 feature 自己的 gRPC service 实现现造的(它知道自己的
/// TDown 是什么),再经 <see cref="Register"/> 校验 sessionId 确实是预登记过的、
/// featureId 匹配,才把这个 Session 接入 SessionEntry、接上心跳表、触发生命周期
/// 钩子。这样 SessionManager 就完全不需要知道任何具体 feature 的 Session 构造
/// 细节了。
/// </summary>
public sealed class SessionManager : IDisposable
{
    private sealed class SessionEntry
    {
        public required string FeatureId { get; init; }
        public required int FeatureIndex { get; init; }
        public required OverlayHost Overlay { get; init; }
        public required IDockPane Pane { get; init; }

        /// <summary>不再是 required init:RestartSession 意外退出重试时要换成新拉起的进程,
        /// 原来的 init-only 没法二次赋值。</summary>
        public required Process Process { get; set; }

        /// <summary>OpenFeature 时还是 null(只是预登记),子进程 Register 校验通过后
        /// 才落地——见 <see cref="SessionManager.Register"/>。RestartSession 会把它清回 null
        /// (Unregister 不会清,只是把 _connected/_lastPongMs 摘掉,详见 RestartSession 注释)。</summary>
        public Session? Session { get; set; }

        /// <summary>Process.Exited 判定为"意外退出"(entry 还在 _entries 里,没人主动关过)时
        /// 置位;RestartSession 成功后清空。<see cref="OpenFeature"/> 里同一 (featureId,
        /// featureIndex) 的重复检查天然会因为 entry 仍在 _entries 里而拒绝,不需要额外读这个
        /// 标志——它只是给 RestartSession 自己防重入用。</summary>
        public bool IsFaulted { get; set; }

        /// <summary>RestartSession(通常来自 UI 线程的重试按钮点击)和 Process.Exited 处理
        /// (线程池)都会读写 Process/Session/IsFaulted 这几个字段,用一把简单的锁把两者串行化,
        /// 避免"刚重启的新进程立刻又崩溃"这种极端时序下两条路径互相踩。</summary>
        public Lock Gate { get; } = new();
    }

    private readonly SessionLaunchOptions _launch;
    private readonly IReadOnlyList<IFeatureHost> _features;
    private readonly ILogger<SessionManager> _logger;

    private readonly ConcurrentDictionary<string, SessionEntry> _entries = new();     // sessionId -> entry(预登记/已连接都在这)
    private readonly ConcurrentDictionary<string, Session> _connected = new();        // sessionId -> 已连接的会话(心跳循环只认这个非泛型基类)
    private readonly ConcurrentDictionary<string, long> _lastPongMs = new();
    private readonly HashSet<string> _unresponsive = [];
    private readonly Lock _unresponsiveLock = new();
    private readonly CancellationTokenSource _cts = new();
    private long _pingSeq;

    /// <summary>超过这么久没收到 Pong(约 2 个心跳周期)就判定 UI 线程无响应。</summary>
    private const long UnresponsiveThresholdMs = 5000;

    public event Action<string, string, int>? SessionOpened;        // sessionId, featureId, featureIndex
    public event Action<string, string>? SessionClosed;             // sessionId, featureId(CloseSession 完成时)
    public event Action<string, string, int>? SessionConnected;     // sessionId, featureId, pid
    public event Action<string, string, nint>? WindowRegistered;    // sessionId, featureId, hwnd
    public event Action<string, string>? SessionDisconnected;       // sessionId, featureId(feature stream 断开)
    /// <summary>sessionId, featureId, exitCode——子进程意外退出(Process.Exited 触发时 entry
    /// 还在 _entries 里,说明没人主动关过)时触发,供宿主应用记日志/告警。此时 pane 没有关闭,
    /// entry 也没有被移除,OverlayHost 已经换成了错误页,调库方可以用这个事件做额外的通知
    /// (比如弹 toast),不需要自己重新判定"是不是意外退出"。</summary>
    public event Action<string, string, int>? SessionFaulted;
    /// <summary>sessionId, featureId, pingSeq, 往返耗时 ms。</summary>
    public event Action<string, string, long, double>? PongReceived;
    /// <summary>sessionId, featureId, 已失联毫秒数。</summary>
    public event Action<string, string, double>? UiUnresponsive;
    public event Action<string, string>? UiRecovered;
    /// <summary>feature service 记业务日志到主窗口,和框架自身的连接/心跳日志分开;
    /// tag 由调用方自己拼(通常是 "{featureId}#{featureIndex}"),SessionManager 不关心格式。</summary>
    public event Action<string, string>? FeatureLog;
    public event Action<string, string, UiStatsRequest>? UiStatsReceived; // sessionId, featureId, stats

    public SessionManager(SessionLaunchOptions launch, IReadOnlyList<IFeatureHost> features, ILogger<SessionManager> logger)
    {
        _launch = launch;
        _features = features;
        _logger = logger;
        _ = HeartbeatLoopAsync(_cts.Token);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>打开一个 feature 的新实例:调用方(demo 里是 MainWindow)自己决定
    /// featureIndex 并造好一个 IDockPane 传进来——同一 (featureId, featureIndex) 不能
    /// 有第二个活跃会话,重复会直接抛 InvalidOperationException(调用方的计数器逻辑
    /// 有 bug 才会走到这里,不是运行时可恢复的场景)。生成 session_id、造
    /// OverlayHost、把它塞进 pane、拉起子进程、预登记(这时候还没有具体的 Session
    /// 对象,等子进程连上来 Register 才会有)。</summary>
    public OverlayHost OpenFeature(string featureId, int featureIndex, IDockPane pane)
    {
        var feature = _features.FirstOrDefault(f => f.FeatureId == featureId)
            ?? throw new ArgumentException($"未注册的 featureId: {featureId}", nameof(featureId));

        if (_entries.Values.Any(e => e.FeatureId == featureId && e.FeatureIndex == featureIndex))
        {
            _logger.LogError("OpenFeature rejected: featureId={FeatureId} featureIndex={FeatureIndex} already has an active session",
                featureId, featureIndex);
            throw new InvalidOperationException(
                $"featureId={featureId} featureIndex={featureIndex} 已经存在一个活跃会话,不能重复 OpenFeature");
        }

        string sessionId = Guid.NewGuid().ToString();

        var overlay = new OverlayHost();
        pane.SetContent(overlay);

        var entry = new SessionEntry
        {
            FeatureId = featureId,
            FeatureIndex = featureIndex,
            Overlay = overlay,
            Pane = pane,
            Process = StartChildProcess(featureId, sessionId, featureIndex),
        };
        _entries[sessionId] = entry;

        pane.Closed += (_, _) => CloseSession(sessionId, ShutdownReason.PaneClosed);

        WireProcessExited(sessionId, entry, entry.Process);

        _logger.LogInformation("OpenFeature: sessionId={SessionId} featureId={FeatureId} featureIndex={FeatureIndex} pid={Pid}",
            sessionId, featureId, featureIndex, entry.Process.Id);
        SessionOpened?.Invoke(sessionId, featureId, featureIndex);
        FeatureLog?.Invoke($"{featureId}#{featureIndex}", $"已启动子进程 pid {entry.Process.Id}");
        return overlay;
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    private Process StartChildProcess(string featureId, string sessionId, int featureIndex)
    {
        var psi = new ProcessStartInfo(_launch.ExecutablePath) { UseShellExecute = false };
        psi.ArgumentList.Add("--child");
        psi.ArgumentList.Add($"--feature={featureId}");
        psi.ArgumentList.Add($"--index={featureIndex}");
        psi.ArgumentList.Add($"--session={sessionId}");
        psi.ArgumentList.Add($"--socket={_launch.SocketPath}");
        psi.ArgumentList.Add($"--hostpid={Environment.ProcessId}");
        return Process.Start(psi)!;
    }

    /// <summary>给一个子进程实例挂 Exited 回调,OpenFeature 首次拉起的进程和 RestartSession
    /// 重新拉起的进程统一走这一个方法。回调里显式捕获这次挂钩的 <paramref name="process"/>
    /// 实例本身,不读 entry.Process 的"当时"值——RestartSession 可能已经把 entry.Process
    /// 换成更新的进程,旧进程如果晚一步才触发 Exited,靠这里捕获的值和 entry.Process 的
    /// "当前"值做 ReferenceEquals 比较(见 <see cref="HandleChildExited"/>),才能分辨出这是
    /// 不是一次过期事件,避免污染刚重启的新会话。</summary>
    private void WireProcessExited(string sessionId, SessionEntry entry, Process process)
    {
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => HandleChildExited(sessionId, entry, process);
    }

    /// <summary>
    /// Process.Exited 统一走这里。先判断这次触发是不是来自 entry 当前的进程——不是就是
    /// RestartSession 替换掉的旧进程的延迟事件,直接丢弃。再判断 entry 是否还在 _entries
    /// 里:三条主动关闭路径(用户关 pane / CloseAll / 调库方调 CloseSession)都会先
    /// TryRemove 掉 entry,之后 KillIfStillRunningAsync 兜底杀掉进程导致的 Exited 落到这里
    /// 正好命中"已移除"分支,清理已经在 CloseSession 里做完了,什么都不用做。entry 还在,
    /// 说明没人主动关过——这就是"意外退出"的判定信号,不需要额外的标志位:保留 entry/pane
    /// (不调 CloseSession),标记 IsFaulted,把 OverlayHost 换成错误页 + 重试按钮,触发
    /// SessionFaulted 供宿主记日志/告警。
    /// </summary>
    private void HandleChildExited(string sessionId, SessionEntry entry, Process exitedProcess)
    {
        lock (entry.Gate)
        {
            if (!ReferenceEquals(entry.Process, exitedProcess))
                return; // 上一个已经被 RestartSession 替换掉的旧进程,延迟触发的 Exited,忽略

            int exitCode = SafeExitCode(exitedProcess);
            FeatureLog?.Invoke($"{entry.FeatureId}#{entry.FeatureIndex}", $"子进程退出 (code {exitCode})");

            if (!_entries.TryGetValue(sessionId, out var current) || !ReferenceEquals(current, entry))
                return; // entry 已经被 CloseSession 移除,这是主动关闭路径里 Kill 兜底触发的正常 Exited

            entry.IsFaulted = true;
            _logger.LogWarning(
                "Unexpected child exit: sessionId={SessionId} featureId={FeatureId} featureIndex={FeatureIndex} exitCode={ExitCode}",
                sessionId, entry.FeatureId, entry.FeatureIndex, exitCode);
            SessionFaulted?.Invoke(sessionId, entry.FeatureId, exitCode);

            var info = new OverlayFaultInfo(entry.FeatureId, entry.FeatureIndex, exitCode, () => RestartSession(sessionId));
            // HandleChildExited 来自线程池(Process.Exited),ShowFault 碰的是 UI 线程独占的
            // OverlayHost,和 AttachChild/DetachChild 一样必须调度回它自己的 Dispatcher。
            entry.Overlay.Dispatcher.BeginInvoke(() => entry.Overlay.ShowFault(info));
        }
    }

    /// <summary>
    /// 错误页"重试"按钮的动作(经 <see cref="OverlayFaultInfo.Retry"/> 间接调用,通常来自
    /// UI 线程的按钮点击,但不假设调用方线程)。复用同一个 sessionId/featureId/featureIndex/
    /// pane,不新生成 session_id:
    ///   1. 旧 <see cref="Session"/>(如果曾经连接成功过)还挂在 entry 上,<see cref="Register"/>
    ///      有"entry.Session 非 null 就拒绝重复注册"的检查,必须显式清掉,否则新子进程带着
    ///      同一个 session_id 连上来会被拒绝。这里的摘除/DisposeOnce 都是幂等操作——如果
    ///      <see cref="Unregister"/>(stream 断开路径)已经跑过一遍,重复做没有副作用;真正
    ///      必须在这里做、Unregister 不会做的是 entry.Session = null,Unregister 不碰
    ///      _entries,不清这个字段的话会一直挡住 Register 的重复注册检查。
    ///   2. OverlayHost 打回中性的等待占位(新子进程真正连上来之前那段真空期,不能让上一次
    ///      崩溃的错误页继续挂着)。
    ///   3. 拉起一个新子进程,换掉 entry.Process,重新挂 Exited 处理器——旧进程如果晚一步才
    ///      触发 Exited,靠 <see cref="HandleChildExited"/> 里的 ReferenceEquals 判断挡掉,
    ///      不会污染这次重启。
    /// entry.Gate 保证这整套清理和 HandleChildExited 标记 IsFaulted 不会互相踩(比如刚重启的
    /// 新进程立刻又崩溃这种极端时序)。
    /// </summary>
    public void RestartSession(string sessionId)
    {
        if (!_entries.TryGetValue(sessionId, out var entry)) return; // 用户没点重试就先关了 pane,entry 已经被彻底清理,无事可做

        lock (entry.Gate)
        {
            if (!entry.IsFaulted) return; // 不是故障态(比如错误页按钮被连点两次),避免重复重启一个还活着/已经在重启中的会话

            if (entry.Session is { } oldSession)
            {
                _connected.TryRemove(sessionId, out _);
                _lastPongMs.TryRemove(sessionId, out _);
                lock (_unresponsiveLock) _unresponsive.Remove(sessionId);
                oldSession.DisposeOnce();
                entry.Session = null;
            }

            entry.IsFaulted = false;
            entry.Overlay.Dispatcher.BeginInvoke(() => entry.Overlay.ResetToWaiting());

            var newProcess = StartChildProcess(entry.FeatureId, sessionId, entry.FeatureIndex);
            entry.Process = newProcess;
            WireProcessExited(sessionId, entry, newProcess);

            _logger.LogInformation(
                "RestartSession: sessionId={SessionId} featureId={FeatureId} featureIndex={FeatureIndex} newPid={Pid}",
                sessionId, entry.FeatureId, entry.FeatureIndex, newProcess.Id);
            FeatureLog?.Invoke($"{entry.FeatureId}#{entry.FeatureIndex}", $"重试:已启动新子进程 pid {newProcess.Id}");
        }
    }

    /// <summary>关闭一个会话:推 Shutdown(带上触发源 reason,子进程可能还没连上来,
    /// 这种情况下 Session 是 null,没有管道可推,靠下面的 Kill 兜底)、关 pane、把
    /// OverlayHost 打回空白占位、从会话表里移除,并在子进程 1.5s 内未自行退出时 Kill
    /// 兜底。可以从三个方向触发(用户关 pane / 子进程自己退出 / CloseAll 时批量调用),
    /// 用 TryRemove 做幂等门,保证只真正执行一次。默认 reason 是 CLOSED_BY_API,对应
    /// "调库方代码主动调用",各触发源自己传更精确的 reason(见 OpenFeature 里的
    /// pane.Closed/CloseAll)。</summary>
    public void CloseSession(string sessionId,
        ShutdownReason reason = ShutdownReason.ClosedByApi, string detail = "")
    {
        if (!_entries.TryRemove(sessionId, out var entry)) return;

        _logger.LogInformation("CloseSession: sessionId={SessionId} featureId={FeatureId} reason={Reason} detail={Detail}",
            sessionId, entry.FeatureId, reason, detail);
        entry.Session?.SendClose(new Shutdown { Reason = reason, Detail = detail });
        // CloseSession 的调用方里,pane.Closed(用户点关闭)和 CloseAll(主窗口关闭)
        // 都在 UI 线程,但 Process.Exited(子进程自己退出/被杀)来自线程池——Pane.Close()
        // /Overlay.DetachChild() 都会碰 UI 线程独占的 WPF 对象(AvalonDock LayoutDocument/
        // OverlayHost),统一调度回 UI 线程最安全,哪怕调用方本来就在 UI 线程上也只是
        // 多一次 BeginInvoke,无副作用。
        entry.Overlay.Dispatcher.BeginInvoke(() =>
        {
            try { entry.Pane.Close(); } catch { /* 可能已经在关闭中(正是这次调用的触发源) */ }
            entry.Overlay.DetachChild();
        });

        _ = KillIfStillRunningAsync(entry.Process);
        SessionClosed?.Invoke(sessionId, entry.FeatureId);
    }

    private static async Task KillIfStillRunningAsync(Process proc)
    {
        try
        {
            bool exited = await Task.Run(() => proc.WaitForExit(1500));
            if (!exited) proc.Kill();
        }
        catch { /* 已退出 */ }
    }

    /// <summary>主窗口关闭时调用:依次关闭全部会话(SendClose{HOST_CLOSING} + kill 兜底),
    /// 复用 CloseSession 的全部逻辑。</summary>
    public void CloseAll()
    {
        foreach (var sessionId in _entries.Keys.ToList())
            CloseSession(sessionId, ShutdownReason.HostClosing);
    }

    /// <summary>feature 自己的 gRPC service 实现在 Register 收到开流请求(带
    /// session_id/hwnd/pid)时调用:此时 <paramref name="session"/> 是调用方（feature
    /// service impl）刚 new 出来的、还没有接入任何东西的 Session 子类实例。这里校验
    /// session.SessionId 确实是 OpenFeature 预登记过的、featureId 匹配、且这个
    /// sessionId 目前还没有别的会话注册过(同一 sessionId 重复注册一律拒绝——简单
    /// 起见不做"顶替旧会话",那是 REPLACED 这个 reason 预留给将来的行为),通过后
    /// 把 featureIndex/pid/hwnd 回填进 Session、接上心跳表(_connected)、触发
    /// SessionConnected/WindowRegistered、OverlayHost.AttachChild、
    /// Session.OnConnected。返回 false 时调用方应直接结束这次 RPC,不写 Reply、不
    /// 调用 ServeAsync。</summary>
    public bool Register(Session session, int pid, nint hwnd)
    {
        if (!_entries.TryGetValue(session.SessionId, out var entry) || entry.FeatureId != session.FeatureId)
        {
            _logger.LogWarning("Register rejected: sessionId={SessionId} featureId={FeatureId} not pre-registered or featureId mismatch",
                session.SessionId, session.FeatureId);
            return false;
        }
        if (entry.Session is not null)
        {
            _logger.LogWarning("Register rejected: sessionId={SessionId} already has a registered session", session.SessionId);
            return false; // 这个 sessionId 已经有一个会话注册过了,拒绝重复注册
        }

        session.FeatureIndex = entry.FeatureIndex;
        session.Pid = pid;
        session.Hwnd = hwnd;
        entry.Session = session;
        _lastPongMs[session.SessionId] = NowMs();
        _connected[session.SessionId] = session;

        SessionConnected?.Invoke(session.SessionId, session.FeatureId, pid);
        WindowRegistered?.Invoke(session.SessionId, session.FeatureId, hwnd);
        // Register 在 gRPC 线程池线程上执行,OverlayHost 是 UI 线程的 DependencyObject
        // (Border),必须调度回它自己的 Dispatcher 才能碰它——这里不同步等待(fire-and-
        // forget BeginInvoke),和旧 SessionHub 时代 MainWindow 订阅 WindowRegistered 后
        // 用 Dispatcher.BeginInvoke 调 AttachChild 的方式一致。OnConnected 不碰任何 UI
        // 对象(典型实现只起一个后台 producer),线程无关,原样同步调用即可。
        entry.Overlay.Dispatcher.BeginInvoke(() => entry.Overlay.AttachChild(hwnd));
        session.OnConnected(hwnd);
        _logger.LogInformation("Registered: sessionId={SessionId} featureId={FeatureId} pid={Pid} hwnd={Hwnd:X}",
            session.SessionId, session.FeatureId, pid, hwnd);
        return true;
    }

    /// <summary>feature service 的 Register 的 stream 结束(finally 块)时调用,
    /// 对称于 <see cref="Register"/>:把会话从"已连接"表里摘除、从心跳表里摘除、
    /// 回到空白占位、触发 SessionDisconnected + Session.OnDisconnected,并保证
    /// <see cref="Session.Dispose"/> 恰好被调用一次(经 <see cref="Session.DisposeOnce"/>
    /// 幂等门——同一个会话可能同时经用户主动 CloseSession 和这里两条路径触发清理)。
    /// 注意这不等于 CloseSession——子进程可能只是暂时断线,pane 仍然留着等重连
    /// (本框架里子进程和会话是 1:1、断线基本等于结束,但语义上和"用户主动关 pane"
    /// 分开,方便调库方将来做重连策略)。</summary>
    public void Unregister(Session session)
    {
        if (!_connected.TryGetValue(session.SessionId, out var cur) || !ReferenceEquals(cur, session))
            return; // 已经被更新的会话替换,不是"自己"的注册,防止误删

        _connected.TryRemove(session.SessionId, out _);
        _lastPongMs.TryRemove(session.SessionId, out _);
        lock (_unresponsiveLock) _unresponsive.Remove(session.SessionId);

        if (_entries.TryGetValue(session.SessionId, out var entry))
        {
            // 同 Register:Unregister 也来自 gRPC 线程池,DetachChild 摸的是 UI 线程的
            // OverlayHost,必须调度回它的 Dispatcher;OnDisconnected 线程无关,同步调用。
            entry.Overlay.Dispatcher.BeginInvoke(() => entry.Overlay.DetachChild());
            session.OnDisconnected();
            SessionDisconnected?.Invoke(session.SessionId, session.FeatureId);
        }

        _logger.LogInformation("Unregistered: sessionId={SessionId} featureId={FeatureId}", session.SessionId, session.FeatureId);
        session.DisposeOnce();
    }

    /// <summary>Register 时回给子进程的展示元数据,从注册的 IFeatureHost 列表里查该
    /// feature 的 Descriptor 现拼一份。</summary>
    public RegisterReply ReplyOf(string featureId)
    {
        var descriptor = _features.First(f => f.FeatureId == featureId).Descriptor;
        var reply = new RegisterReply { Title = descriptor.Title, AccentColor = descriptor.AccentColor };
        foreach (var kv in descriptor.Settings)
            reply.Settings.Add(kv.Key, kv.Value);
        return reply;
    }

    /// <summary>feature 专属 unary(统计/排序……)按 session_id 取回调库方自己的
    /// Session 子类,读/改其中的业务状态。找不到、还没连接、或类型不对时返回 null。</summary>
    public TSession? FindSession<TSession>(string sessionId) where TSession : Session =>
        _entries.TryGetValue(sessionId, out var entry) ? entry.Session as TSession : null;

    public void OnPong(string sessionId, PongRequest request)
    {
        _lastPongMs[sessionId] = NowMs();
        if (!_entries.TryGetValue(sessionId, out var entry) || entry.Session is not { } session) return;
        double rtt = NowMs() - request.PingTimestampMs;
        session.OnPong(request.Seq, rtt);
        PongReceived?.Invoke(sessionId, entry.FeatureId, request.Seq, rtt);
    }

    /// <summary>CommonServiceImpl.ReportUiStats 收到子进程上报后调用:按 session_id
    /// 找到会话,转发给 Session.OnUiStats,再原样抛给 UI——阈值判断("持续 >80%
    /// 记一条日志"之类)留给 MainWindow 自己维护状态。</summary>
    public void OnUiStats(UiStatsRequest request)
    {
        if (_entries.TryGetValue(request.SessionId, out var entry) && entry.Session is { } session)
        {
            session.OnUiStats(request);
            UiStatsReceived?.Invoke(request.SessionId, entry.FeatureId, request);
        }
    }

    public void LogEvent(string tag, string message) => FeatureLog?.Invoke(tag, message);

    /// <summary>心跳:每 2 秒向所有已连接会话推送带时间戳的 Ping(经
    /// Session.SendHeartbeat,各自实现把它包成自己的 envelope 写进自己的管道)。</summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                long seq = ++_pingSeq;
                var ping = new Ping { Seq = seq, TimestampMs = NowMs() };
                foreach (var session in _connected.Values)
                    session.SendHeartbeat(ping);

                CheckUnresponsive();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 每个心跳 tick 检查一遍所有会话距上次 Pong 的时长,只在无响应/恢复状态翻转时
    /// 发一次事件,避免同一个状态每 2 秒重复刷屏。
    /// </summary>
    private void CheckUnresponsive()
    {
        long now = NowMs();
        foreach (var session in _connected.Values)
        {
            if (!_lastPongMs.TryGetValue(session.SessionId, out var last)) continue;
            if (!_entries.TryGetValue(session.SessionId, out var entry)) continue;
            double elapsed = now - last;
            bool isUnresponsiveNow = elapsed > UnresponsiveThresholdMs;

            lock (_unresponsiveLock)
            {
                bool wasUnresponsive = _unresponsive.Contains(session.SessionId);
                if (isUnresponsiveNow && !wasUnresponsive)
                {
                    _unresponsive.Add(session.SessionId);
                    _logger.LogWarning("UI unresponsive: sessionId={SessionId} featureId={FeatureId} elapsedMs={ElapsedMs}",
                        session.SessionId, entry.FeatureId, elapsed);
                    UiUnresponsive?.Invoke(session.SessionId, entry.FeatureId, elapsed);
                }
                else if (!isUnresponsiveNow && wasUnresponsive)
                {
                    _unresponsive.Remove(session.SessionId);
                    _logger.LogInformation("UI recovered: sessionId={SessionId} featureId={FeatureId}", session.SessionId, entry.FeatureId);
                    UiRecovered?.Invoke(session.SessionId, entry.FeatureId);
                }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
