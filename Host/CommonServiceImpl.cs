using Grpc.Core;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Host;

/// <summary>共享 CommonService 的薄壳实现,运行在 Kestrel 线程池上;两个 unary 都只做
/// 按 session_id 路由到 SessionHub,状态和事件全在 SessionHub 里,这里不持有任何状态。
/// 会话建立本身已经并入各 feature 自己的 Register 开流请求(见 WaveformServiceImpl /
/// TableServiceImpl),不再需要 Register/RegisterWindow 这两个 RPC。</summary>
public sealed class CommonServiceImpl(SessionHub hub) : CommonService.CommonServiceBase
{
    public override Task<Ack> Pong(PongRequest request, ServerCallContext context)
    {
        hub.OnPong(request.SessionId, request);
        return Task.FromResult(new Ack { Ok = true });
    }

    public override Task<Ack> RequestActivate(ActivateRequest request, ServerCallContext context)
    {
        hub.OnActivate(request.SessionId);
        return Task.FromResult(new Ack { Ok = true });
    }
}
