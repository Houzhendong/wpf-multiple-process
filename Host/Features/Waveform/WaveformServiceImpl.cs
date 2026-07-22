using System.Collections.Concurrent;
using System.IO;
using Grpc.Core;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Common;
using WpfMultiProcess.Ipc.Waveform;

namespace WpfMultiProcess.Host.Features.Waveform;

/// <summary>
/// waveform feature 专属 gRPC service:会话建立(StreamRequest 带 session_id/hwnd/pid,
/// 校验通过后先下发一条 Reply,再是数据流:Reply/Control/WaveformFrame 共用一个
/// envelope,wrap 接缝见 Register 里的 Subscription 构造)+ 专属 unary GetStatistics。
/// </summary>
public sealed class WaveformServiceImpl(SessionHub hub) : WaveformService.WaveformServiceBase
{
    /// <summary>一个会话对应的数据生产者:50ms 一帧正弦波,顺带累计 min/max/avg,
    /// 供 GetStatistics 直接读,不用另开一份历史缓存。</summary>
    private sealed class Producer
    {
        public readonly CancellationTokenSource Cts = new();
        private readonly Lock _statsLock = new();
        private long _count;
        private double _min = double.MaxValue, _max = double.MinValue, _sum;

        public void RecordSample(double value)
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

    private readonly ConcurrentDictionary<string, Producer> _producers = new();

    public override async Task Register(StreamRequest request,
        IServerStreamWriter<WaveformDown> down, ServerCallContext context)
    {
        // 会话建立现在并进这次开流请求:未知的 session_id(没被 MainWindow.Prepare 预登记过)
        // 或者 feature 对不上,一律拒绝——直接结束这次 RPC,不建订阅、不起 producer。
        if (!hub.TryOpen(request.SessionId, "waveform", request.Pid, (nint)request.Hwnd))
            return;

        await down.WriteAsync(new WaveformDown { Reply = hub.ReplyOf("waveform") }, context.CancellationToken);

        var sub = new Subscription<WaveformDown>(request.SessionId, "waveform",
            control => new WaveformDown { Control = control });
        hub.AttachStream(sub);

        var producer = new Producer();
        _producers[request.SessionId] = producer;
        var produceTask = ProduceAsync(sub, producer, producer.Cts.Token);

        try
        {
            await foreach (var env in sub.Reader.ReadAllAsync(context.CancellationToken))
                await down.WriteAsync(env, context.CancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            producer.Cts.Cancel();
            try { await produceTask; } catch { /* 已取消 */ }
            _producers.TryRemove(request.SessionId, out _);
            hub.DetachStream(sub);
        }
    }

    private static async Task ProduceAsync(Subscription<WaveformDown> sub, Producer producer, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        long seq = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                seq++;
                double value = Math.Sin(seq * 0.12) * (0.6 + 0.4 * Math.Sin(seq * 0.011));
                producer.RecordSample(value);
                sub.WriteData(new WaveformDown
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

    public override Task<StatsReply> GetStatistics(StatsRequest request, ServerCallContext context)
    {
        var reply = _producers.TryGetValue(request.SessionId, out var producer)
            ? producer.Snapshot()
            : new StatsReply();
        hub.LogEvent("waveform",
            $"GetStatistics → min={reply.Min:F3} max={reply.Max:F3} avg={reply.Avg:F3} count={reply.Count}");
        return Task.FromResult(reply);
    }
}
