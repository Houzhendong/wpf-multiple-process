using Grpc.Core;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Host;

/// <summary>共享 CommonService 的薄壳实现,运行在 Kestrel 线程池上;两个 unary 都只做
/// 按 session_id 路由到 SessionManager,状态和事件全在 SessionManager(以及各自的
/// Session 子类)里,这里不持有任何状态。会话建立本身已经并入各 feature 自己的
/// Register 开流请求(见 demo 的 WaveformServiceImpl / TableServiceImpl),不再需要
/// 独立的 Register/RegisterWindow 这两个 RPC。ReportUiStats 是子进程
/// UiSaturationMeter 后台线程每窗口上报一次的 UI 线程饱和度遥测。两个 unary 都只是
/// 薄薄转发一层,不记日志,避免每次心跳/UI 统计上报都刷屏,因此不需要注入 ILogger。
///
/// 原来这里还有一个 RequestActivate 实现(转发给 SessionManager.OnActivate,顺带
/// 注入了 ILogger 记一条 Debug 日志);子窗口全部改为可激活后那条补偿链整条删掉了,
/// 这个类跟着少了一个 override,ILogger 依赖也一并去掉,不留没人用的构造参数。</summary>
public sealed class CommonServiceImpl(SessionManager sessionManager) : CommonService.CommonServiceBase
{
    public override Task<Ack> Pong(PongRequest request, ServerCallContext context)
    {
        sessionManager.OnPong(request.SessionId, request);
        return Task.FromResult(new Ack { Ok = true });
    }

    public override Task<Ack> ReportUiStats(UiStatsRequest request, ServerCallContext context)
    {
        sessionManager.OnUiStats(request);
        return Task.FromResult(new Ack { Ok = true });
    }
}
