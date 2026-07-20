using System.IO;
using Grpc.Core;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Host;

/// <summary>gRPC 服务实现,运行在 Kestrel 线程池上;所有状态交由 HostCoordinator。</summary>
public sealed class IpcService(HostCoordinator coordinator) : HostIpc.HostIpcBase
{
    public override async Task Subscribe(SubscribeRequest request,
        IServerStreamWriter<ServerMessage> responseStream, ServerCallContext context)
    {
        var sub = coordinator.AddSubscriber(request.FeatureId, request.Pid);
        try
        {
            await foreach (var msg in sub.Queue.Reader.ReadAllAsync(context.CancellationToken))
                await responseStream.WriteAsync(msg, context.CancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            coordinator.RemoveSubscriber(request.FeatureId, sub);
        }
    }

    public override Task<InitParamsReply> GetInitParams(InitParamsRequest request, ServerCallContext context)
    {
        var reply = request.FeatureId switch
        {
            "waveform" => new InitParamsReply { Title = "实时波形", AccentColor = "#007ACC" },
            "table" => new InitParamsReply { Title = "随机游走", AccentColor = "#68217A" },
            _ => new InitParamsReply { Title = request.FeatureId, AccentColor = "#CA5100" },
        };
        reply.Settings.Add("data_interval_ms", "50");
        reply.Settings.Add("heartbeat_interval_ms", "2000");
        return Task.FromResult(reply);
    }

    public override Task<Ack> ReportInteraction(InteractionRequest request, ServerCallContext context)
    {
        coordinator.OnInteraction(request.FeatureId, request.Action, request.PayloadJson);
        return Task.FromResult(new Ack { Ok = true });
    }

    public override Task<Ack> RegisterWindow(RegisterWindowRequest request, ServerCallContext context)
    {
        coordinator.OnWindowRegistered(request.FeatureId, (nint)request.Hwnd, request.Pid);
        return Task.FromResult(new Ack { Ok = true });
    }

    public override Task<Ack> Pong(PongRequest request, ServerCallContext context)
    {
        coordinator.OnPong(request);
        return Task.FromResult(new Ack { Ok = true });
    }
}
