using System.IO;
using Grpc.Core;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Waveform;

namespace WpfMultiProcess.Demo.Host.Features.Waveform;

/// <summary>
/// waveform feature 专属 gRPC service:会话建立(StreamRequest 带 session_id/hwnd/pid,
/// 经 SessionManager.TryOpen&lt;WaveformDown&gt; 校验并接上 Subscription)后先下发一条
/// Reply,再把 Subscription 里的帧(数据帧由 WaveformSession 自己的 producer 推,心跳/关闭
/// 由 SessionManager 推)原样转发进 gRPC stream;专属 unary GetStatistics 经
/// SessionManager.FindSession 直接读 WaveformSession 的统计快照。
/// </summary>
public sealed class WaveformServiceImpl(SessionManager sessionManager) : WaveformService.WaveformServiceBase
{
    public override async Task Register(StreamRequest request,
        IServerStreamWriter<WaveformDown> down, ServerCallContext context)
    {
        // 未知的 session_id(没被 SessionManager.OpenFeature 建立过)或 feature 对不上、
        // 或该会话不是 WaveformSession,一律拒绝——直接结束这次 RPC,不写 Reply。
        if (!sessionManager.TryOpen<WaveformDown>(request.SessionId, "waveform", request.Pid, (nint)request.Hwnd,
                out var sub) || sub is null)
            return;

        await down.WriteAsync(new WaveformDown { Reply = sessionManager.ReplyOf("waveform") },
            context.CancellationToken);

        try
        {
            await foreach (var env in sub.Reader.ReadAllAsync(context.CancellationToken))
                await down.WriteAsync(env, context.CancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            sessionManager.DetachStream(sub);
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
