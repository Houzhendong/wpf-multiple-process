using Grpc.Core;
using Microsoft.Extensions.Logging;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Host;

/// <summary>共享 CommonService 的薄壳实现,运行在 Kestrel 线程池上;三个 unary 都只做
/// 按 session_id 路由到 SessionManager,状态和事件全在 SessionManager(以及各自的
/// Session 子类)里,这里不持有任何状态。会话建立本身已经并入各 feature 自己的
/// Register 开流请求(见 demo 的 WaveformServiceImpl / TableServiceImpl),不再需要
/// 独立的 Register/RegisterWindow 这两个 RPC。ReportUiStats 是子进程
/// UiSaturationMeter 后台线程每窗口上报一次的 UI 线程饱和度遥测。ILogger 由 ASP.NET
/// Core DI 自动注入(这个类本身就是经 builder.Services.AddSingleton&lt;CommonServiceImpl&gt;()
/// 交给容器构造的),只用于诊断——正常路径三个 unary 都只是薄薄转发一层,不记日志,
/// 避免每次心跳/UI 统计上报都刷屏。</summary>
public sealed class CommonServiceImpl(SessionManager sessionManager, ILogger<CommonServiceImpl> logger) : CommonService.CommonServiceBase
{
    public override Task<Ack> Pong(PongRequest request, ServerCallContext context)
    {
        sessionManager.OnPong(request.SessionId, request);
        return Task.FromResult(new Ack { Ok = true });
    }

    public override Task<Ack> RequestActivate(ActivateRequest request, ServerCallContext context)
    {
        logger.LogDebug("RequestActivate: sessionId={SessionId}", request.SessionId);
        sessionManager.OnActivate(request.SessionId);
        return Task.FromResult(new Ack { Ok = true });
    }

    public override Task<Ack> ReportUiStats(UiStatsRequest request, ServerCallContext context)
    {
        sessionManager.OnUiStats(request);
        return Task.FromResult(new Ack { Ok = true });
    }
}
