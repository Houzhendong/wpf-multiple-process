using Grpc.Core;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Waveform;

namespace WpfMultiProcess.Demo.Host.Features.Waveform;

/// <summary>
/// waveform feature 专属 gRPC service:会话建立(StreamRequest 带 session_id/hwnd/pid,
/// 经 SessionManager.TryOpen 校验、取回 OpenFeature 时就建好的 WaveformSession)后先下发
/// 一条 Reply,再把 gRPC 的 <see cref="IServerStreamWriter{T}"/> 的所有权整个交给
/// WaveformSession.ServeAsync(数据帧由它自己的 producer 推,心跳/关闭也由它自己的
/// SendHeartbeat/SendClose 写进同一条管道);专属 unary GetStatistics 经
/// SessionManager.FindSession 直接读 WaveformSession 的统计快照。
/// </summary>
public sealed class WaveformServiceImpl(SessionManager sessionManager) : WaveformService.WaveformServiceBase
{
    public override async Task Register(StreamRequest request,
        IServerStreamWriter<WaveformDown> down, ServerCallContext context)
    {
        // 未知的 session_id(没被 SessionManager.OpenFeature 建立过)或 feature 对不上,
        // 或该会话不是 WaveformSession,一律拒绝——直接结束这次 RPC,不写 Reply。
        if (!sessionManager.TryOpen(request.SessionId, "waveform", request.Pid, (nint)request.Hwnd, out var session)
            || session is not WaveformSession waveformSession)
            return;

        await down.WriteAsync(new WaveformDown { Reply = sessionManager.ReplyOf("waveform") },
            context.CancellationToken);

        try
        {
            await waveformSession.ServeAsync(down, context.CancellationToken);
        }
        finally
        {
            sessionManager.DetachStream(waveformSession);
        }
    }

    public override Task<StatsReply> GetStatistics(StatsRequest request, ServerCallContext context)
    {
        var session = sessionManager.FindSession<WaveformSession>(request.SessionId);
        var reply = session?.Snapshot() ?? new StatsReply();
        sessionManager.LogEvent($"waveform#{session?.FeatureIndex}",
            $"GetStatistics → min={reply.Min:F3} max={reply.Max:F3} avg={reply.Avg:F3} count={reply.Count}");
        return Task.FromResult(reply);
    }
}
