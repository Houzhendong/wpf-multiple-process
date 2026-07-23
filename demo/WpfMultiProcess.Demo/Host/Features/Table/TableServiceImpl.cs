using System.IO;
using Grpc.Core;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Common;
using WpfMultiProcess.Ipc.Table;

namespace WpfMultiProcess.Demo.Host.Features.Table;

/// <summary>
/// table feature 专属 gRPC service:会话建立(同 waveform,经
/// SessionManager.TryOpen&lt;TableDown&gt;)+ 数据流转发 + 专属 unary Sort——直接改
/// TableSession 里的排序状态,下一帧立即生效,子进程侧不需要自己再排一遍。
/// </summary>
public sealed class TableServiceImpl(SessionManager sessionManager) : TableService.TableServiceBase
{
    public override async Task Register(StreamRequest request,
        IServerStreamWriter<TableDown> down, ServerCallContext context)
    {
        if (!sessionManager.TryOpen<TableDown>(request.SessionId, "table", request.Pid, (nint)request.Hwnd,
                out var sub) || sub is null)
            return;

        await down.WriteAsync(new TableDown { Reply = sessionManager.ReplyOf("table") }, context.CancellationToken);

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

    public override Task<Ack> Sort(SortRequest request, ServerCallContext context)
    {
        var session = sessionManager.FindSession<TableSession>(request.SessionId);
        session?.ApplySort(request.Field, request.Ascending);
        sessionManager.LogEvent($"table#{session?.FeatureIndex}",
            $"Sort by {request.Field} {(request.Ascending ? "asc" : "desc")}");
        return Task.FromResult(new Ack { Ok = true });
    }
}
