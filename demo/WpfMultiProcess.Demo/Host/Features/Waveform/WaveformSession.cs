using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Waveform;

namespace WpfMultiProcess.Demo.Host.Features.Waveform;

/// <summary>waveform 会话的宿主侧状态:50ms 一帧正弦波 producer + min/max/avg/count 统计,
/// 原来挂在 WaveformServiceImpl 内部的 per-session Producer 字典,现在下沉成 Session 自己
/// 的状态——OnConnected(子进程 hwnd 落地、Subscription 已接上)时起 producer,
/// OnDisconnected 时停,GetStatistics unary 经 SessionManager.FindSession 直接读
/// Snapshot(),不需要额外的会话查找表。</summary>
public sealed class WaveformSession : Session<WaveformDown>
{
    private readonly Lock _statsLock = new();
    private CancellationTokenSource? _produceCts;
    private Task? _produceTask;
    private long _count;
    private double _min = double.MaxValue, _max = double.MinValue, _sum;

    public WaveformSession(string sessionId, string featureId, int featureIndex)
        : base(sessionId, featureId, featureIndex, control => new WaveformDown { Control = control })
    {
    }

    // 跨程序集 override "protected internal" 成员时,C# 只允许声明为 "protected"
    // (internal 那部分在别的程序集里本来就无意义/无法表达)。
    protected override void OnConnected(nint hwnd)
    {
        _produceCts = new CancellationTokenSource();
        _produceTask = ProduceAsync(_produceCts.Token);
    }

    protected override void OnDisconnected() => _produceCts?.Cancel();

    private async Task ProduceAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        long seq = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                seq++;
                double value = Math.Sin(seq * 0.12) * (0.6 + 0.4 * Math.Sin(seq * 0.011));
                RecordSample(value);
                PushData(new WaveformDown
                {
                    Frame = new WaveformFrame
                    {
                        Seq = seq,
                        Value = value,
                        TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    },
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    private void RecordSample(double value)
    {
        lock (_statsLock)
        {
            _count++;
            _sum += value;
            if (value < _min) _min = value;
            if (value > _max) _max = value;
        }
    }

    public StatsReply Snapshot()
    {
        lock (_statsLock)
        {
            return new StatsReply
            {
                Min = _count == 0 ? 0 : _min,
                Max = _count == 0 ? 0 : _max,
                Avg = _count == 0 ? 0 : _sum / _count,
                Count = _count,
            };
        }
    }
}
