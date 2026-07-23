using Grpc.Core;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Waveform;

namespace WpfMultiProcess.Demo.Host.Features.Waveform;

/// <summary>
/// waveform feature 专属 gRPC service:会话建立整个反过来了——Register 收到开流请求
/// (StreamRequest 带 session_id/hwnd/pid)后,自己 new 一个新的 WaveformSession,
/// 交给 SessionManager.Register 校验(sessionId 是不是 OpenFeature 预登记过的、
/// featureId 对不对、有没有被重复注册),通过才下发 Reply,再把 gRPC 的
/// IServerStreamWriter 的所有权整个交给 WaveformSession.ServeAsync(数据帧由它自己
/// 的 producer 推,心跳/关闭也由它自己的 SendHeartbeat/SendClose 写进同一条管道);
/// 结束时经 SessionManager.Unregister 对称清理。专属 unary GetStatistics 经
/// SessionManager.FindSession 直接读 WaveformSession 的统计快照。
/// </summary>
public sealed class WaveformServiceImpl(SessionManager sessionManager) : WaveformService.WaveformServiceBase
{
    private const string FeatureId = "waveform";

    public override async Task Register(StreamRequest request,
        IServerStreamWriter<WaveformDown> down, ServerCallContext context)
    {
        var session = new WaveformSession(request.SessionId, FeatureId);

        // 未知的 session_id(没被 SessionManager.OpenFeature 预登记过)、feature 对不上、
        // 或这个 sessionId 已经被别的会话注册过,一律拒绝——直接结束这次 RPC,不写 Reply。
        if (!sessionManager.Register(session, request.Pid, (nint)request.Hwnd))
            return;

        await down.WriteAsync(new WaveformDown { Reply = sessionManager.ReplyOf(FeatureId) },
            context.CancellationToken);

        try
        {
            await session.ServeAsync(down, context.CancellationToken);
        }
        finally
        {
            sessionManager.Unregister(session);
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
