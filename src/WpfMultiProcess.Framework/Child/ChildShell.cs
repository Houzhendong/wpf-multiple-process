using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Child;

/// <summary>
/// 子进程会话编排(从原 ChildShell 拆分出来的非视图部分——原来的 ChildShell 是一个
/// Window 子类,身兼"窗口长什么样"和"会话怎么编排"两件事,现在拆成 ChildWindow(纯
/// 视图容器)+ 这个类):持有 ChildWindow + gRPC 通道 + session_id/featureId/
/// featureIndex + 在同一个 channel 上额外建的 CommonService 客户端(Pong/
/// ReportUiStats 这几个 feature-无关的 unary 用它,都是普通 fire-and-forget unary
/// 调用,不是往 bidi stream 里写,没有并发写同一条 stream 的坑),以及框架级、
/// feature-无关的 <see cref="UiSaturationMeter"/>(UI 线程饱和度探针+归因遥测)。
///
/// 原来这里还有一个 RequestActivate:子窗口是 WS_EX_NOACTIVATE 时点击不会自己抢
/// 激活,靠这个 unary 上报"我被点了"换取主进程 Activate() 补偿。子窗口改为全部
/// 可激活后(见 ChildWindow),点击会被系统正常处理,这条补偿链存在的理由消失,
/// 整条删掉了(CommonService.proto 的 rpc、CommonServiceImpl 的实现、
/// SessionManager.OnActivate/ActivateRequested、这里的 RequestActivate 方法、
/// ChildWindow 的 PreviewMouseDown 上报、demo MainWindow 的订阅,全部一起删)。
///
/// 真正的数据流由 feature 自己的 <see cref="FeatureViewModel{TDown}"/> 开,demux 出来的
/// Reply/Ping/Shutdown(XxxDown oneof 的三个 feature-无关分支)转发回这里对应的方法:
/// <see cref="SendPong"/>(Ping→UI 线程刷新心跳文本+回 Pong)、<see cref="RequestClose"/>
/// (Shutdown→关窗口)、<see cref="ApplyReply"/>(Register 流第一条 Reply→设标题/主题色)。
/// </summary>
public sealed class ChildShell : IDisposable
{
    private readonly CommonService.CommonServiceClient _common;
    private readonly ILoggerFactory _loggerFactory;
    private UiSaturationMeter? _uiSaturation;

    public ChildWindow Window { get; }
    public GrpcChannel Channel { get; }
    public string SessionId { get; }
    public string FeatureId { get; }
    public int FeatureIndex { get; }

    /// <summary>诊断日志(和 SessionManager.FeatureLog 那条"业务事件转发到主窗口 UI"的
    /// 通道分开)——FeatureViewModel 也复用这个 logger 记 Shutdown 之类的框架级事件,
    /// 分类名固定是 ChildShell,因为这几件事本质上都是"这个会话的编排层"在做的诊断。</summary>
    public ILogger Logger { get; }

    /// <summary>子窗口自己的 Win32 句柄,随开流请求带给主进程(不再单独有一次
    /// RegisterWindow RPC)。</summary>
    public nint Hwnd => Window.Hwnd;

    public ChildShell(ChildWindow window, GrpcChannel channel, string sessionId, string featureId,
        int featureIndex, ILoggerFactory loggerFactory)
    {
        Window = window;
        Channel = channel;
        SessionId = sessionId;
        FeatureId = featureId;
        FeatureIndex = featureIndex;
        _common = new CommonService.CommonServiceClient(channel);
        _loggerFactory = loggerFactory;
        Logger = loggerFactory.CreateLogger<ChildShell>();
    }

    /// <summary>UiSaturationMeter 是框架级、feature-无关的:探针(后台线程)+
    /// Dispatcher.Hooks(UI 线程注册)一起构成"UI 线程饱和度"遥测,和具体 feature 视图
    /// 完全无关,bootstrap 建好 channel/CommonService 客户端后立即拉起,窗口 Closed 时
    /// 停掉。</summary>
    public void StartUiSaturationSampling()
    {
        _uiSaturation = new UiSaturationMeter(Window.Dispatcher, _common, SessionId, _loggerFactory.CreateLogger<UiSaturationMeter>());
        _uiSaturation.Start();
    }

    /// <summary>feature 视图模型从自己 stream 里 demux 出 Ping 后调用:在 UI 线程
    /// 更新心跳状态条,并回一个 Pong unary——回 Pong 发生在 UI 线程,证明 UI 未卡死。</summary>
    public void SendPong(Ping ping)
    {
        Window.Dispatcher.BeginInvoke(() =>
        {
            Window.SetHeartbeatText($"心跳 #{ping.Seq}  {DateTime.Now:HH:mm:ss}");
            _ = _common.PongAsync(new PongRequest
            {
                SessionId = SessionId,
                Seq = ping.Seq,
                PingTimestampMs = ping.TimestampMs,
                PongTimestampMs = NowMs(),
            }).ResponseAsync;
        });
    }

    /// <summary>feature 视图模型从自己 stream 里 demux 出 Shutdown 后调用。</summary>
    public void RequestClose() => Window.Dispatcher.BeginInvoke(Window.Close);

    /// <summary>feature 视图模型收到 Register 流第一条 Reply 后调用,设标题/主题色。</summary>
    public void ApplyReply(RegisterReply reply) => Window.ApplyReplyMeta(reply.Title, reply.AccentColor);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public void Dispose()
    {
        _uiSaturation?.Dispose();
        Channel.Dispose();
    }
}
