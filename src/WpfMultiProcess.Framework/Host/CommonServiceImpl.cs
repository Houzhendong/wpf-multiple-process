using Grpc.Core;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Host;

/// <summary>共享 CommonService 的薄壳实现,运行在 Kestrel 线程池上;三个 unary 都只做
/// 按 session_id 路由到 SessionManager,状态和事件全在 SessionManager(以及各自的
/// Session 子类)里,这里不持有任何状态。会话建立本身已经并入各 feature 自己的
/// Register 开流请求(见 demo 的 WaveformServiceImpl / TableServiceImpl),不再需要
/// 独立的 Register/RegisterWindow 这两个 RPC。ReportUiStats 是子进程
/// UiSaturationMeter 后台线程每窗口上报一次的 UI 线程饱和度遥测。</summary>
public sealed class CommonServiceImpl(SessionManager sessionManager) : CommonService.CommonServiceBase
{
    public override Task<Ack> Pong(PongRequest request, ServerCallContext context)
    {
        sessionManager.OnPong(request.SessionId, request);
        return Task.FromResult(new Ack { Ok = true });
    }

    public override Task<Ack> RequestActivate(ActivateRequest request, ServerCallContext context)
    {
        sessionManager.OnActivate(request.SessionId);
        return Task.FromResult(new Ack { Ok = true });
    }

    public override Task<Ack> ReportUiStats(UiStatsRequest request, ServerCallContext context)
    {
        sessionManager.OnUiStats(request);
        return Task.FromResult(new Ack { Ok = true });
    }
}
