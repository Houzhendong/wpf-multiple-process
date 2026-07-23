using Grpc.Core;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Common;
using WpfMultiProcess.Ipc.Table;

namespace WpfMultiProcess.Demo.Host.Features.Table;

/// <summary>
/// table feature 专属 gRPC service:会话建立同 waveform——Register 收到开流请求后
/// 自己 new 一个新的 TableSession,交给 SessionManager.Register 校验通过才下发
/// Reply,再把 IServerStreamWriter 的所有权交给 TableSession.ServeAsync,结束时经
/// SessionManager.Unregister 对称清理;专属 unary Sort——直接改 TableSession 里的
/// 排序状态,下一帧立即生效,子进程侧不需要自己再排一遍。
/// </summary>
public sealed class TableServiceImpl(SessionManager sessionManager) : TableService.TableServiceBase
{
    private const string FeatureId = "table";

    public override async Task Register(StreamRequest request,
        IServerStreamWriter<TableDown> down, ServerCallContext context)
    {
        var session = new TableSession(request.SessionId, FeatureId);

        if (!sessionManager.Register(session, request.Pid, (nint)request.Hwnd))
            return;

        await down.WriteAsync(new TableDown { Reply = sessionManager.ReplyOf(FeatureId) }, context.CancellationToken);

        try
        {
            await session.ServeAsync(down, context.CancellationToken);
        }
        finally
        {
            sessionManager.Unregister(session);
        }
    }

    public override Task<Ack> Sort(SortRequest request, ServerCallContext context)
    {
        var session = sessionManager.FindSession<TableSession>(request.SessionId);
        session?.ApplySort(request.Field, request.Ascending);
        sessionManager.LogEvent($"table#{session?.FeatureIndex}",
            $"Sort by {request.Field} {(request.Ascending ? "asc" : "desc")}");
        return Task.FromResult(new Ack { Ok = true });
    }
}
