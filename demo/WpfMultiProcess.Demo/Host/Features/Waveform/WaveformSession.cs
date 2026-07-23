using System.IO;
using System.Threading.Channels;
using Grpc.Core;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Common;
using WpfMultiProcess.Ipc.Waveform;

namespace WpfMultiProcess.Demo.Host.Features.Waveform;

/// <summary>waveform 会话的宿主侧状态:自建一个 <c>Channel&lt;WaveformDown&gt;</c> 作为
/// 自己的数据管道(心跳/关闭/数据帧都经它统一推给 gRPC stream),外加 50ms 一帧正弦波
/// producer + min/max/avg/count 统计。原来这条管道由框架的 Subscription&lt;TDown&gt;
/// 代持,现在完全下沉成 Session 自己的私有状态——SendHeartbeat/SendClose 直接把
/// Ping/Shutdown 包成 WaveformDown 写进自己的 channel,ServeAsync 把 channel 的
/// reader 和 gRPC 的 writer 一起交给 <see cref="SessionPump.PumpAsync{T}"/>。</summary>
public sealed class WaveformSession : Session
{
    private readonly Channel<WaveformDown> _channel = Channel.CreateBounded<WaveformDown>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly Lock _statsLock = new();
    private CancellationTokenSource? _produceCts;
    private Task? _produceTask;
    private long _count;
    private double _min = double.MaxValue, _max = double.MinValue, _sum;

    public WaveformSession(string sessionId, string featureId) : base(sessionId, featureId)
    {
    }

    public override void SendHeartbeat(Ping ping) =>
        _channel.Writer.TryWrite(new WaveformDown { Ping = ping });

    public override void SendClose(Shutdown shutdown) =>
        _channel.Writer.TryWrite(new WaveformDown { Shutdown = shutdown });

    public override async Task ServeAsync<T>(IServerStreamWriter<T> writer, CancellationToken ct)
    {
        if (writer is not IServerStreamWriter<WaveformDown> typed)
            throw new InvalidOperationException(
                $"WaveformSession.ServeAsync 只支持 IServerStreamWriter<WaveformDown>,实际传入 {typeof(T)}");
        try
        {
            await SessionPump.PumpAsync(_channel.Reader, typed, ct);
        }
        catch (IOException) { }
    }

    // 跨程序集 override "protected internal" 成员时,C# 只允许声明为 "protected"
    // (internal 那部分在别的程序集里本来就无意义/无法表达)。
    protected override void OnConnected(nint hwnd)
    {
        _produceCts = new CancellationTokenSource();
        _produceTask = ProduceAsync(_produceCts.Token);
    }

    protected override void OnDisconnected() => _produceCts?.Cancel();

    public override void Dispose()
    {
        _produceCts?.Cancel();
        _channel.Writer.TryComplete();
    }

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
                _channel.Writer.TryWrite(new WaveformDown
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
