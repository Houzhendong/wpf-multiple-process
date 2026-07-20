using System.Collections.Concurrent;
using System.Threading.Channels;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Host;

/// <summary>
/// 主进程侧的 IPC 协调器:持有每个 feature 的推送队列,驱动心跳与实时数据两个循环,
/// 并把 gRPC 服务收到的上行消息以事件形式抛给 UI(事件在线程池线程触发,UI 侧自行调度)。
/// </summary>
public sealed class HostCoordinator : IDisposable
{
    public sealed class Subscription
    {
        public required string FeatureId { get; init; }
        public required int Pid { get; init; }
        public required Channel<ServerMessage> Queue { get; init; }
    }

    private readonly ConcurrentDictionary<string, Subscription> _subs = new();
    private readonly ConcurrentDictionary<string, long> _dataSeq = new();
    private readonly ConcurrentDictionary<string, double> _walkState = new();
    private readonly ConcurrentDictionary<string, long> _lastPongMs = new();
    private readonly HashSet<string> _unresponsive = [];
    private readonly Lock _unresponsiveLock = new();
    private readonly CancellationTokenSource _cts = new();
    private long _pingSeq;

    /// <summary>超过这么久没收到 Pong(约 2 个心跳周期)就判定 UI 线程无响应。</summary>
    private const long UnresponsiveThresholdMs = 5000;

    public event Action<string, int>? SubscriberConnected;
    public event Action<string>? SubscriberDisconnected;
    public event Action<string, nint, int>? WindowRegistered;
    public event Action<string, string, string>? InteractionReceived;
    /// <summary>featureId, pingSeq, 往返耗时 ms(主进程发出 Ping → 子进程 UI 线程 Pong 到达)。</summary>
    public event Action<string, long, double>? PongReceived;
    /// <summary>featureId, 已失联毫秒数;在心跳循环里检测到某 feature 超过阈值没回 Pong 时触发一次。</summary>
    public event Action<string, double>? UiUnresponsive;
    /// <summary>featureId;之前判定无响应的 feature 又收到 Pong 时触发一次。</summary>
    public event Action<string>? UiRecovered;

    public HostCoordinator()
    {
        _ = HeartbeatLoopAsync(_cts.Token);
        _ = DataLoopAsync(_cts.Token);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Subscription AddSubscriber(string featureId, int pid)
    {
        var sub = new Subscription
        {
            FeatureId = featureId,
            Pid = pid,
            // 有界队列,子进程消费不过来时丢最旧的数据帧,避免主进程内存膨胀
            Queue = Channel.CreateBounded<ServerMessage>(
                new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest }),
        };
        _subs[featureId] = sub;
        _lastPongMs[featureId] = NowMs();
        SubscriberConnected?.Invoke(featureId, pid);
        return sub;
    }

    public void RemoveSubscriber(string featureId, Subscription sub)
    {
        // 只移除仍是自己的注册(防止重连后误删新订阅)
        if (_subs.TryGetValue(featureId, out var cur) && ReferenceEquals(cur, sub))
        {
            _subs.TryRemove(featureId, out _);
            _lastPongMs.TryRemove(featureId, out _);
            lock (_unresponsiveLock) _unresponsive.Remove(featureId);
            SubscriberDisconnected?.Invoke(featureId);
        }
    }

    public void OnWindowRegistered(string featureId, nint hwnd, int pid) =>
        WindowRegistered?.Invoke(featureId, hwnd, pid);

    public void OnInteraction(string featureId, string action, string payloadJson) =>
        InteractionReceived?.Invoke(featureId, action, payloadJson);

    public void OnPong(PongRequest pong)
    {
        _lastPongMs[pong.FeatureId] = NowMs();
        double rtt = NowMs() - pong.PingTimestampMs;
        PongReceived?.Invoke(pong.FeatureId, pong.Sequence, rtt);
    }

    public void PushShutdown(string featureId)
    {
        if (_subs.TryGetValue(featureId, out var sub))
            sub.Queue.Writer.TryWrite(new ServerMessage { Shutdown = new Shutdown() });
    }

    public void PushShutdownAll()
    {
        foreach (var id in _subs.Keys)
            PushShutdown(id);
    }

    /// <summary>心跳:每 2 秒向所有订阅者推送带时间戳的 Ping。</summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                long seq = ++_pingSeq;
                var msg = new ServerMessage { Ping = new Ping { Sequence = seq, TimestampMs = NowMs() } };
                foreach (var sub in _subs.Values)
                    sub.Queue.Writer.TryWrite(msg);

                CheckUnresponsive();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 每个心跳 tick 检查一遍所有订阅者距上次 Pong 的时长,只在无响应/恢复状态翻转时发一次事件,
    /// 避免同一个状态每 2 秒重复刷屏。
    /// </summary>
    private void CheckUnresponsive()
    {
        long now = NowMs();
        foreach (var sub in _subs.Values)
        {
            if (!_lastPongMs.TryGetValue(sub.FeatureId, out var last)) continue;
            double elapsed = now - last;
            bool isUnresponsiveNow = elapsed > UnresponsiveThresholdMs;

            lock (_unresponsiveLock)
            {
                bool wasUnresponsive = _unresponsive.Contains(sub.FeatureId);
                if (isUnresponsiveNow && !wasUnresponsive)
                {
                    _unresponsive.Add(sub.FeatureId);
                    UiUnresponsive?.Invoke(sub.FeatureId, elapsed);
                }
                else if (!isUnresponsiveNow && wasUnresponsive)
                {
                    _unresponsive.Remove(sub.FeatureId);
                    UiRecovered?.Invoke(sub.FeatureId);
                }
            }
        }
    }

    /// <summary>实时数据泵:50ms 一帧,waveform 推正弦,其余 feature 推随机游走。</summary>
    private async Task DataLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var rand = new Random();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var sub in _subs.Values)
                {
                    long seq = _dataSeq.AddOrUpdate(sub.FeatureId, 1, (_, v) => v + 1);
                    double value;
                    if (sub.FeatureId == "waveform")
                    {
                        value = Math.Sin(seq * 0.12) * (0.6 + 0.4 * Math.Sin(seq * 0.011));
                    }
                    else
                    {
                        value = _walkState.AddOrUpdate(sub.FeatureId,
                            0, (_, v) => Math.Clamp(v + (rand.NextDouble() - 0.5) * 0.2, -3, 3));
                    }
                    sub.Queue.Writer.TryWrite(new ServerMessage
                    {
                        Feature = new FeatureData
                        {
                            FeatureId = sub.FeatureId,
                            Sequence = seq,
                            Value = value,
                            Text = $"{sub.FeatureId} #{seq}: {value:F4}",
                            TimestampMs = NowMs(),
                        },
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
