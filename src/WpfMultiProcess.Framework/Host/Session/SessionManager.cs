using System.Collections.Concurrent;
using System.Diagnostics;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Host.Session;

/// <summary>启动一个子进程需要的固定参数:可执行文件路径 + UDS 套接字路径。
/// 由宿主应用(demo 的 HostProgram)在拉起 Kestrel 之后构造好传给 SessionManager。</summary>
public sealed record SessionLaunchOptions(string ExecutablePath, string SocketPath);

/// <summary>
/// 会话层核心(演进自原 SessionHub,原地改名并承担了原来 MainWindow.LaunchChild +
/// HostFeatureRegistry 的职责):调库方注册好 <see cref="IFeatureHost"/> 列表和一个
/// <see cref="IDockWorkspace"/> 实现之后,"开一个 feature 实例"这件事(分配
/// featureIndex/session_id、造 Session、造 OverlayHost、建 dock pane、拉起子进程、
/// 登记)完全由 <see cref="OpenFeature"/> 一次调用完成,支持同一 feature 反复调用
/// 多开出独立的会话/独立的子进程。
///
/// 心跳/Pong 超时检测/UI 饱和度路由这几个和具体 feature 完全无关的职责原样保留;
/// 会话建立的校验(TryOpen)现在还多做一件事:把 Session&lt;TDown&gt; 和它的
/// Subscription&lt;TDown&gt; 接上,并调用 Session 上的生命周期虚方法(OnConnected/
/// OnPong/OnUiStats/OnDisconnected),取代原来"SessionHub 只认 feature-无关状态"的
/// 设计——具体 feature 的会话状态(producer 是否在跑、统计值……)现在下沉到调库方
/// 自己的 Session 子类里,SessionManager 本身仍然不知道 TDown 是什么类型。
/// </summary>
public sealed class SessionManager : IDisposable
{
    private sealed class SessionEntry
    {
        public required Session Session { get; init; }
        public required string FeatureId { get; init; }
        public required OverlayHost Overlay { get; init; }
        public required IDockPane Pane { get; init; }
        public required Process Process { get; init; }
    }

    private readonly IDockWorkspace _dock;
    private readonly SessionLaunchOptions _launch;
    private readonly IReadOnlyList<IFeatureHost> _features;

    private readonly ConcurrentDictionary<string, SessionEntry> _entries = new();     // sessionId -> entry
    private readonly ConcurrentDictionary<string, Subscription> _subs = new();        // sessionId -> 订阅(心跳循环只认这个基类)
    private readonly ConcurrentDictionary<string, long> _lastPongMs = new();
    private readonly ConcurrentDictionary<string, int> _nextFeatureIndex = new();     // featureId -> 下一个可用 index
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
    /// <summary>sessionId, featureId, pingSeq, 往返耗时 ms。</summary>
    public event Action<string, string, long, double>? PongReceived;
    /// <summary>sessionId, featureId, 已失联毫秒数。</summary>
    public event Action<string, string, double>? UiUnresponsive;
    public event Action<string, string>? UiRecovered;
    public event Action<string, string>? ActivateRequested;         // sessionId, featureId
    /// <summary>feature service 记业务日志到主窗口,和框架自身的连接/心跳日志分开;
    /// tag 由调用方自己拼(通常是 "{featureId}#{featureIndex}"),SessionManager 不关心格式。</summary>
    public event Action<string, string>? FeatureLog;
    public event Action<string, string, UiStatsRequest>? UiStatsReceived; // sessionId, featureId, stats

    public SessionManager(IDockWorkspace dock, SessionLaunchOptions launch, IReadOnlyList<IFeatureHost> features)
    {
        _dock = dock;
        _launch = launch;
        _features = features;
        _ = HeartbeatLoopAsync(_cts.Token);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>打开一个 feature 的新实例:分配 featureIndex(该 feature 下递增)、生成
    /// session_id、造 Session、造 OverlayHost、经 IDockWorkspace 建 dock pane、
    /// 拉起子进程、登记。同一个 featureId 可以反复调用,每次都是独立的会话。</summary>
    public OverlayHost OpenFeature(string featureId)
    {
        var feature = _features.FirstOrDefault(f => f.FeatureId == featureId)
            ?? throw new ArgumentException($"未注册的 featureId: {featureId}", nameof(featureId));

        int index = _nextFeatureIndex.AddOrUpdate(featureId, 0, (_, cur) => cur + 1);
        string sessionId = Guid.NewGuid().ToString();

        var session = feature.CreateSession(new FeatureInstanceContext(sessionId, index));
        var overlay = new OverlayHost();
        var pane = _dock.AddPane(sessionId, $"{feature.Descriptor.Title} #{index}", overlay);

        var entry = new SessionEntry
        {
            Session = session,
            FeatureId = featureId,
            Overlay = overlay,
            Pane = pane,
            Process = StartChildProcess(featureId, sessionId, index),
        };
        _entries[sessionId] = entry;

        pane.Closed += (_, _) => CloseSession(sessionId);

        entry.Process.EnableRaisingEvents = true;
        entry.Process.Exited += (_, _) =>
        {
            FeatureLog?.Invoke($"{featureId}#{index}", $"子进程退出 (code {SafeExitCode(entry.Process)})");
            CloseSession(sessionId);
        };

        SessionOpened?.Invoke(sessionId, featureId, index);
        FeatureLog?.Invoke($"{featureId}#{index}", $"已启动子进程 pid {entry.Process.Id}");
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

    /// <summary>关闭一个会话:推 Shutdown、关 pane、把 OverlayHost 打回空白占位、
    /// 从会话表里移除,并在子进程 1.5s 内未自行退出时 Kill 兜底。可以从三个方向
    /// 触发(用户关 pane / 子进程自己退出 / CloseAll 时批量调用),用 TryRemove 做
    /// 幂等门,保证只真正执行一次。</summary>
    public void CloseSession(string sessionId)
    {
        if (!_entries.TryRemove(sessionId, out var entry)) return;

        entry.Session.SendClose();
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

    /// <summary>主窗口关闭时调用:依次关闭全部会话(SendClose + kill 兜底),
    /// 复用 CloseSession 的全部逻辑。</summary>
    public void CloseAll()
    {
        foreach (var sessionId in _entries.Keys.ToList())
            CloseSession(sessionId);
    }

    /// <summary>feature service 的 Register 收到开流请求(带 session_id/hwnd/pid)时调用:
    /// 校验 session_id 是 OpenFeature 建立过的、feature 匹配、且该 Session 确实是
    /// Session&lt;TDown&gt;,通过则造 Subscription&lt;TDown&gt;(由 Session 自己的
    /// wrapControl 现造,见 Session&lt;TDown&gt;.CreateSubscription)、接上心跳表、
    /// 触发 SessionConnected/WindowRegistered、OverlayHost.AttachChild、
    /// Session.OnConnected。未知的 session_id / feature 不对 / 类型不对一律拒绝,
    /// 调用方应直接结束这次 RPC,不写 Reply、不建订阅。</summary>
    public bool TryOpen<TDown>(string sessionId, string featureId, int pid, nint hwnd,
        out Subscription<TDown>? subscription)
    {
        subscription = null;
        if (!_entries.TryGetValue(sessionId, out var entry) || entry.FeatureId != featureId)
            return false;
        if (entry.Session is not Session<TDown> typedSession)
            return false;

        entry.Session.Pid = pid;
        entry.Session.Hwnd = hwnd;
        _lastPongMs[sessionId] = NowMs();

        var sub = typedSession.CreateSubscription();
        typedSession.AttachSubscription(sub);
        _subs[sessionId] = sub;
        subscription = sub;

        SessionConnected?.Invoke(sessionId, featureId, pid);
        WindowRegistered?.Invoke(sessionId, featureId, hwnd);
        // TryOpen 在 gRPC 线程池线程上执行,OverlayHost 是 UI 线程的 DependencyObject
        // (Border),必须调度回它自己的 Dispatcher 才能碰它——这里不同步等待(fire-and-
        // forget BeginInvoke),和旧 SessionHub 时代 MainWindow 订阅 WindowRegistered 后
        // 用 Dispatcher.BeginInvoke 调 AttachChild 的方式一致。OnConnected 不碰任何 UI
        // 对象(典型实现只起一个后台 producer),线程无关,原样同步调用即可。
        entry.Overlay.Dispatcher.BeginInvoke(() => entry.Overlay.AttachChild(hwnd));
        typedSession.OnConnected(hwnd);
        return true;
    }

    /// <summary>feature service 的 Register 的 stream 结束(finally 块)时调用,
    /// 对称于 TryOpen&lt;TDown&gt;:把 Session 和这条 Subscription 解绑、从心跳表里
    /// 摘除、回到空白占位、触发 SessionDisconnected + Session.OnDisconnected。
    /// 注意这不等于 CloseSession——子进程可能只是暂时断线,pane 仍然留着等重连
    /// (本框架里子进程和会话是 1:1、断线基本等于结束,但语义上和"用户主动关 pane"
    /// 分开,方便调库方将来做重连策略)。</summary>
    public void DetachStream<TDown>(Subscription<TDown> sub)
    {
        if (!_subs.TryGetValue(sub.SessionId, out var cur) || !ReferenceEquals(cur, sub))
            return; // 已经被更新的订阅替换,不是"自己"的注册,防止误删

        _subs.TryRemove(sub.SessionId, out _);
        _lastPongMs.TryRemove(sub.SessionId, out _);
        lock (_unresponsiveLock) _unresponsive.Remove(sub.SessionId);

        if (_entries.TryGetValue(sub.SessionId, out var entry))
        {
            if (entry.Session is Session<TDown> typedSession)
                typedSession.DetachSubscription(sub);
            // 同 TryOpen:DetachStream 也来自 gRPC 线程池,DetachChild 摸的是 UI 线程的
            // OverlayHost,必须调度回它的 Dispatcher;OnDisconnected 线程无关,同步调用。
            entry.Overlay.Dispatcher.BeginInvoke(() => entry.Overlay.DetachChild());
            entry.Session.OnDisconnected();
            SessionDisconnected?.Invoke(sub.SessionId, sub.FeatureId);
        }
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
    /// Session 子类,读/改其中的业务状态。找不到或类型不对时返回 null。</summary>
    public TSession? FindSession<TSession>(string sessionId) where TSession : Session =>
        _entries.TryGetValue(sessionId, out var entry) ? entry.Session as TSession : null;

    public void OnPong(string sessionId, PongRequest request)
    {
        _lastPongMs[sessionId] = NowMs();
        if (!_entries.TryGetValue(sessionId, out var entry)) return;
        double rtt = NowMs() - request.PingTimestampMs;
        entry.Session.OnPong(request.Seq, rtt);
        PongReceived?.Invoke(sessionId, entry.FeatureId, request.Seq, rtt);
    }

    public void OnActivate(string sessionId)
    {
        if (_entries.TryGetValue(sessionId, out var entry))
            ActivateRequested?.Invoke(sessionId, entry.FeatureId);
    }

    /// <summary>CommonServiceImpl.ReportUiStats 收到子进程上报后调用:按 session_id
    /// 找到会话,转发给 Session.OnUiStats,再原样抛给 UI——阈值判断("持续 >80%
    /// 记一条日志"之类)留给 MainWindow 自己维护状态。</summary>
    public void OnUiStats(UiStatsRequest request)
    {
        if (_entries.TryGetValue(request.SessionId, out var entry))
        {
            entry.Session.OnUiStats(request);
            UiStatsReceived?.Invoke(request.SessionId, entry.FeatureId, request);
        }
    }

    public void LogEvent(string tag, string message) => FeatureLog?.Invoke(tag, message);

    /// <summary>心跳:每 2 秒向所有会话推送带时间戳的 Ping(经 Session.SendHeartbeat →
    /// WriteControl → 各自 Session&lt;TDown&gt; 的 wrapControl 接缝包成自己的 envelope)。</summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                long seq = ++_pingSeq;
                var ping = new Ping { Seq = seq, TimestampMs = NowMs() };
                foreach (var sub in _subs.Values)
                    if (_entries.TryGetValue(sub.SessionId, out var entry))
                        entry.Session.SendHeartbeat(ping);

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
        foreach (var sub in _subs.Values)
        {
            if (!_lastPongMs.TryGetValue(sub.SessionId, out var last)) continue;
            if (!_entries.TryGetValue(sub.SessionId, out var entry)) continue;
            double elapsed = now - last;
            bool isUnresponsiveNow = elapsed > UnresponsiveThresholdMs;

            lock (_unresponsiveLock)
            {
                bool wasUnresponsive = _unresponsive.Contains(sub.SessionId);
                if (isUnresponsiveNow && !wasUnresponsive)
                {
                    _unresponsive.Add(sub.SessionId);
                    UiUnresponsive?.Invoke(sub.SessionId, entry.FeatureId, elapsed);
                }
                else if (!isUnresponsiveNow && wasUnresponsive)
                {
                    _unresponsive.Remove(sub.SessionId);
                    UiRecovered?.Invoke(sub.SessionId, entry.FeatureId);
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
