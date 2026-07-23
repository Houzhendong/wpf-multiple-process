using System.Collections.Concurrent;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Host.Session;

/// <summary>
/// 会话层核心,feature-无关(演进自原 HostCoordinator):维护会话表、驱动心跳/Pong 超时
/// 检测,并把上行事件以 featureId 为 key 抛给 UI——本框架一进程一 feature,MainWindow
/// 沿用 featureId 索引 OverlayHost 字典,不需要再额外感知 sessionId。
///
/// session_id 不再由这里生成:MainWindow 在拉起子进程之前就先 Guid.NewGuid() 生成好,
/// 当启动参数传给子进程,同时调用 <see cref="Prepare"/> 预登记"这个 session_id 属于
/// 哪个 feature"。子进程开自己 feature 的 gRPC 流(带 hwnd/pid)时,由 <see cref="TryOpen"/>
/// 校验 session_id 确实是主进程预登记过的、且 feature 对得上,才正式建立会话——这就是
/// 原来 OpenSession + RegisterWindow 两次 RPC 合并成一次开流请求之后,校验环节的落点。
///
/// 具体 feature 的数据推送(波形/表格…)完全不在这里,由各 feature 自己的
/// gRPC service 维护;SessionHub 只认 Subscription 基类和 Common.Control。
/// </summary>
public sealed class SessionHub : IDisposable
{
    private sealed class SessionInfo
    {
        public required string FeatureId { get; init; }
        public int Pid { get; set; }
        public nint Hwnd { get; set; }
    }

    private readonly HostFeatureRegistry _registry;
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, Subscription> _subs = new();
    private readonly ConcurrentDictionary<string, long> _lastPongMs = new();
    private readonly HashSet<string> _unresponsive = [];
    private readonly Lock _unresponsiveLock = new();
    private readonly CancellationTokenSource _cts = new();
    private long _pingSeq;

    /// <summary>超过这么久没收到 Pong(约 2 个心跳周期)就判定 UI 线程无响应。</summary>
    private const long UnresponsiveThresholdMs = 5000;

    public event Action<string, int>? SessionConnected;        // featureId, pid
    public event Action<string, nint>? WindowRegistered;        // featureId, hwnd
    public event Action<string>? SessionDisconnected;           // featureId
    /// <summary>featureId, pingSeq, 往返耗时 ms(主进程发出 Ping → 子进程 UI 线程 Pong 到达)。</summary>
    public event Action<string, long, double>? PongReceived;
    /// <summary>featureId, 已失联毫秒数;心跳循环里检测到某会话超过阈值没回 Pong 时触发一次。</summary>
    public event Action<string, double>? UiUnresponsive;
    /// <summary>featureId;之前判定无响应的会话又收到 Pong 时触发一次。</summary>
    public event Action<string>? UiRecovered;
    public event Action<string>? ActivateRequested;             // featureId
    /// <summary>feature service 记业务日志到主窗口,和框架自身的连接/心跳日志分开。</summary>
    public event Action<string, string>? FeatureLog;
    /// <summary>featureId, 子进程 UiSaturationMeter 每窗口(约 1s)上报一次的 UI 线程
    /// 饱和度遥测,和心跳的 Pong/RTT 是两条独立的通道。</summary>
    public event Action<string, UiStatsRequest>? UiStatsReceived;

    public SessionHub(HostFeatureRegistry registry)
    {
        _registry = registry;
        _ = HeartbeatLoopAsync(_cts.Token);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>MainWindow 在拉起子进程之前调用:预登记这个 session_id 属于哪个 feature,
    /// 子进程随后开流时据此校验(见 TryOpen)。</summary>
    public void Prepare(string sessionId, string featureId) =>
        _sessions[sessionId] = new SessionInfo { FeatureId = featureId };

    /// <summary>feature service 的 Register 收到开流请求(带 session_id/hwnd/pid)时调用:
    /// 校验 session_id 已被 Prepare 过且 feature 匹配,通过则正式建立会话并触发
    /// SessionConnected + WindowRegistered;未知的 session_id(或 feature 对不上)一律
    /// 拒绝——不建会话,调用方应直接结束这次 RPC。</summary>
    public bool TryOpen(string sessionId, string featureId, int pid, nint hwnd)
    {
        if (!_sessions.TryGetValue(sessionId, out var info) || info.FeatureId != featureId)
            return false;

        info.Pid = pid;
        info.Hwnd = hwnd;
        _lastPongMs[sessionId] = NowMs();
        SessionConnected?.Invoke(featureId, pid);
        WindowRegistered?.Invoke(featureId, hwnd);
        return true;
    }

    /// <summary>Register 时回给子进程的展示元数据,从 HostFeatureRegistry 里查该 feature
    /// 的 Descriptor 现拼一份 —— 原来是 SessionServiceImpl.Register 的返回值,现在搭在
    /// feature 自己 stream 的第一条 XxxDown 里。</summary>
    public RegisterReply ReplyOf(string featureId)
    {
        var descriptor = _registry.DescriptorOf(featureId);
        var reply = new RegisterReply { Title = descriptor.Title, AccentColor = descriptor.AccentColor };
        foreach (var kv in descriptor.Settings)
            reply.Settings.Add(kv.Key, kv.Value);
        return reply;
    }

    public void OnPong(string sessionId, PongRequest request)
    {
        _lastPongMs[sessionId] = NowMs();
        if (!_sessions.TryGetValue(sessionId, out var info)) return;
        double rtt = NowMs() - request.PingTimestampMs;
        PongReceived?.Invoke(info.FeatureId, request.Seq, rtt);
    }

    public void OnActivate(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var info))
            ActivateRequested?.Invoke(info.FeatureId);
    }

    /// <summary>CommonServiceImpl.ReportUiStats 收到子进程上报后调用:按 session_id
    /// 找到 featureId,原样把整个请求抛给 UI——极薄路由,不做任何聚合/判定,
    /// 阈值判断(比如"持续 >80% 记一条日志")留给 MainWindow 自己维护状态。</summary>
    public void OnUiStats(UiStatsRequest request)
    {
        if (_sessions.TryGetValue(request.SessionId, out var info))
            UiStatsReceived?.Invoke(info.FeatureId, request);
    }

    /// <summary>feature service 的 Register 拿到 Subscription 后调用,把它挂进心跳循环。</summary>
    public void AttachStream(Subscription sub) => _subs[sub.SessionId] = sub;

    public void DetachStream(Subscription sub)
    {
        // 只移除仍是自己的注册(防止重连后误删新订阅)
        if (_subs.TryGetValue(sub.SessionId, out var cur) && ReferenceEquals(cur, sub))
        {
            _subs.TryRemove(sub.SessionId, out _);
            _lastPongMs.TryRemove(sub.SessionId, out _);
            lock (_unresponsiveLock) _unresponsive.Remove(sub.SessionId);
            _sessions.TryRemove(sub.SessionId, out _);
            SessionDisconnected?.Invoke(sub.FeatureId);
        }
    }

    public void LogEvent(string featureId, string message) => FeatureLog?.Invoke(featureId, message);

    /// <summary>心跳:每 2 秒向所有会话推送带时间戳的 Ping(通过各自 feature 的 WriteControl,
    /// 也就是 wrapControl 接缝,搭进各自的 envelope 类型里)。</summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                long seq = ++_pingSeq;
                var control = new Control { Ping = new Ping { Seq = seq, TimestampMs = NowMs() } };
                foreach (var sub in _subs.Values)
                    sub.WriteControl(control);

                CheckUnresponsive();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 每个心跳 tick 检查一遍所有会话距上次 Pong 的时长,只在无响应/恢复状态翻转时发一次事件,
    /// 避免同一个状态每 2 秒重复刷屏。
    /// </summary>
    private void CheckUnresponsive()
    {
        long now = NowMs();
        foreach (var sub in _subs.Values)
        {
            if (!_lastPongMs.TryGetValue(sub.SessionId, out var last)) continue;
            if (!_sessions.TryGetValue(sub.SessionId, out var info)) continue;
            double elapsed = now - last;
            bool isUnresponsiveNow = elapsed > UnresponsiveThresholdMs;

            lock (_unresponsiveLock)
            {
                bool wasUnresponsive = _unresponsive.Contains(sub.SessionId);
                if (isUnresponsiveNow && !wasUnresponsive)
                {
                    _unresponsive.Add(sub.SessionId);
                    UiUnresponsive?.Invoke(info.FeatureId, elapsed);
                }
                else if (!isUnresponsiveNow && wasUnresponsive)
                {
                    _unresponsive.Remove(sub.SessionId);
                    UiRecovered?.Invoke(info.FeatureId);
                }
            }
        }
    }

    public void PushShutdownAll()
    {
        var control = new Control { Shutdown = new Shutdown() };
        foreach (var sub in _subs.Values)
            sub.WriteControl(control);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
