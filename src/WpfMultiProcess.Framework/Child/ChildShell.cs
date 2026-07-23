using Grpc.Net.Client;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Child;

/// <summary>
/// 子进程会话编排(从原 ChildShell 拆分出来的非视图部分——原来的 ChildShell 是一个
/// Window 子类,身兼"窗口长什么样"和"会话怎么编排"两件事,现在拆成 ChildWindow(纯
/// 视图容器)+ 这个类):持有 ChildWindow + gRPC 通道 + session_id/featureId/
/// featureIndex + 在同一个 channel 上额外建的 CommonService 客户端(Pong/
/// RequestActivate/ReportUiStats 这几个 feature-无关的 unary 用它,都是普通
/// fire-and-forget unary 调用,不是往 bidi stream 里写,没有并发写同一条 stream 的坑),
/// 以及框架级、feature-无关的 <see cref="UiSaturationMeter"/>(UI 线程饱和度探针+归因
/// 遥测)。
///
/// 真正的数据流由 feature 自己的 <see cref="FeatureViewModel{TDown}"/> 开,demux 出来的
/// Reply/Control 转发回这里对应的方法:<see cref="SendPong"/>(Control.Ping→UI 线程刷新
/// 心跳文本+回 Pong)、<see cref="RequestClose"/>(Control.Shutdown→关窗口)、
/// <see cref="ApplyReply"/>(Register 流第一条 Reply→设标题/主题色)。
/// </summary>
public sealed class ChildShell : IDisposable
{
    private readonly CommonService.CommonServiceClient _common;
    private UiSaturationMeter? _uiSaturation;

    public ChildWindow Window { get; }
    public GrpcChannel Channel { get; }
    public string SessionId { get; }
    public string FeatureId { get; }
    public int FeatureIndex { get; }

    /// <summary>子窗口自己的 Win32 句柄,随开流请求带给主进程(不再单独有一次
    /// RegisterWindow RPC)。</summary>
    public nint Hwnd => Window.Hwnd;

    public ChildShell(ChildWindow window, GrpcChannel channel, string sessionId, string featureId, int featureIndex)
    {
        Window = window;
        Channel = channel;
        SessionId = sessionId;
        FeatureId = featureId;
        FeatureIndex = featureIndex;
        _common = new CommonService.CommonServiceClient(channel);

        // 点击子窗口本身不应该抢激活(WS_EX_NOACTIVATE 拦不到的场景兜底见 ChildWindow),
        // 但主进程侧仍希望"我被点了"能把宿主提到前面,所以点击时 fire-and-forget
        // 上报一次 RequestActivate,不阻塞输入本身。
        Window.ActivateRequested += RequestActivate;
    }

    /// <summary>UiSaturationMeter 是框架级、feature-无关的:探针(后台线程)+
    /// Dispatcher.Hooks(UI 线程注册)一起构成"UI 线程饱和度"遥测,和具体 feature 视图
    /// 完全无关,bootstrap 建好 channel/CommonService 客户端后立即拉起,窗口 Closed 时
    /// 停掉。</summary>
    public void StartUiSaturationSampling()
    {
        _uiSaturation = new UiSaturationMeter(Window.Dispatcher, _common, SessionId);
        _uiSaturation.Start();
    }

    /// <summary>子窗口是 WS_EX_NOACTIVATE,点击不会自己抢激活,fire-and-forget 上报一次
    /// 换取主进程 Activate() 补偿。普通 unary 调用,和 Pong 共用同一个 CommonService
    /// 客户端、同一个 channel,没有 bidi 写并发的坑。</summary>
    public void RequestActivate() => _ = RequestActivateCoreAsync();

    private async Task RequestActivateCoreAsync()
    {
        try { await _common.RequestActivateAsync(new ActivateRequest { SessionId = SessionId }).ResponseAsync; }
        catch { /* 主进程不可达时忽略,不影响窗口自身交互 */ }
    }

    /// <summary>feature 视图模型从自己 stream 里 demux 出 Control.Ping 后调用:在 UI 线程
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

    /// <summary>feature 视图模型从自己 stream 里 demux 出 Control.Shutdown 后调用。</summary>
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
