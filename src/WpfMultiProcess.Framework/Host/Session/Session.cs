using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Host.Session;

/// <summary>
/// 一个子进程实例的抽象(非泛型基类,调库方不直接继承这个,而是继承下面的
/// <see cref="Session{TDown}"/>)。持有会话身份(session_id/featureId/featureIndex)
/// 和已知的 pid/hwnd,并把"给这个会话发心跳/关闭"统一成 <see cref="SendHeartbeat"/>/
/// <see cref="SendClose"/> 两个不感知具体 envelope 类型的方法——真正的写入由
/// <see cref="Session{TDown}"/> 经 <c>Subscription&lt;TDown&gt;</c> 实现。
///
/// 生命周期虚方法(<see cref="OnConnected"/>/<see cref="OnPong"/>/<see cref="OnUiStats"/>/
/// <see cref="OnDisconnected"/>)供调库方在自己的 Session 子类里 override,承接原来散落在
/// SessionHub/各 ServiceImpl 里的"会话连上了/收到 Pong 了/该起 producer 了"这类时机。
/// </summary>
public abstract class Session
{
    /// <summary>MainWindow/SessionManager 在拉起子进程之前生成的会话标识,作为 --session
    /// 启动参数传给子进程。</summary>
    public string SessionId { get; }

    public string FeatureId { get; }

    /// <summary>同一 feature 下第几次 OpenFeature 打开的实例(0 起),支持同一 feature
    /// 多开。</summary>
    public int FeatureIndex { get; }

    /// <summary>子进程开流请求带上来的 pid/hwnd,TryOpen 校验通过后回填。</summary>
    public int Pid { get; internal set; }
    public nint Hwnd { get; internal set; }

    protected Session(string sessionId, string featureId, int featureIndex)
    {
        SessionId = sessionId;
        FeatureId = featureId;
        FeatureIndex = featureIndex;
    }

    /// <summary>把会话层的 Control(Ping/Shutdown)包装成本会话 feature 自己的 envelope
    /// 类型再写入 stream——具体包装/写入由 <see cref="Session{TDown}"/> 经
    /// Subscription&lt;TDown&gt; 完成,这里只声明接缝,仅框架内部(SessionManager 的
    /// 心跳循环/PushShutdownAll)调用。</summary>
    internal abstract void WriteControl(Control control);

    /// <summary>心跳:SessionManager 心跳循环里对每个会话调用。</summary>
    public void SendHeartbeat(Ping ping) => WriteControl(new Control { Ping = ping });

    /// <summary>关闭:CloseSession / 主窗口关闭时对每个会话调用。</summary>
    public void SendClose() => WriteControl(new Control { Shutdown = new Shutdown() });

    /// <summary>子进程开流请求 TryOpen 校验通过、hwnd 落地时调用(SessionManager 内部)。
    /// 典型用途:调库方在这里启动自己的数据 producer。</summary>
    protected internal virtual void OnConnected(nint hwnd) { }

    /// <summary>收到这个会话的 Pong 时调用(seq 回显、rttMs 是主进程发出 Ping 到子进程
    /// UI 线程回 Pong 的往返耗时)。</summary>
    protected internal virtual void OnPong(long seq, double rttMs) { }

    /// <summary>收到这个会话的 UiSaturationMeter 上报时调用。</summary>
    protected internal virtual void OnUiStats(UiStatsRequest stats) { }

    /// <summary>会话断开(子进程退出/stream 断开)时调用。典型用途:停掉自己的 producer。</summary>
    protected internal virtual void OnDisconnected() { }
}

/// <summary>
/// 持有具体 feature envelope 类型(TDown,即 XxxDown oneof 消息)的 Session 基类——
/// 调库方的 host 侧 Session 实现(如 demo 的 WaveformSession)继承这个类,既能
/// <see cref="PushData"/> 推自己的业务数据帧,也自动获得心跳/关闭包装。
///
/// 持有一个 <see cref="Subscription{TDown}"/> 引用(子进程开流、TryOpen 校验通过后由
/// SessionManager 绑定上来,见 <see cref="AttachSubscription"/>)和一个
/// wrapControl 委托——这正是主进程侧"把 Control 包成 TDown"的接缝,SessionManager
/// 本身完全不感知 TDown 是什么类型。
/// </summary>
public abstract class Session<TDown> : Session
{
    private readonly Func<Control, TDown> _wrapControl;
    private Subscription<TDown>? _subscription;

    protected Session(string sessionId, string featureId, int featureIndex, Func<Control, TDown> wrapControl)
        : base(sessionId, featureId, featureIndex)
    {
        _wrapControl = wrapControl;
    }

    /// <summary>SessionManager.TryOpen&lt;TDown&gt; 里调用:本 Session 是自己 wrapControl
    /// 委托的唯一持有者,由它现造一个新的 Subscription&lt;TDown&gt;(session_id/featureId
    /// 随会话本身,wrap 委托随 Session 子类的构造),SessionManager 自己完全不需要
    /// 知道 TDown 的 wrap 规则是什么。</summary>
    internal Subscription<TDown> CreateSubscription() => new(SessionId, FeatureId, _wrapControl);

    /// <summary>SessionManager.TryOpen 里,子进程开流请求校验通过后调用,把这个会话
    /// 接下来的推流通道接上。</summary>
    internal void AttachSubscription(Subscription<TDown> subscription) => _subscription = subscription;

    /// <summary>会话断开时(feature service 的 Register finally 块)由 SessionManager 调用,
    /// 避免断线重连场景下 PushData 写进一个已经没有读者的旧 Subscription。</summary>
    internal void DetachSubscription(Subscription<TDown> subscription)
    {
        if (ReferenceEquals(_subscription, subscription))
            _subscription = null;
    }

    internal override void WriteControl(Control control) => _subscription?.WriteControl(control);

    /// <summary>调库方在自己的 producer 里调用,推一帧业务数据。会话还没建立(子进程未连接)
    /// 或已断开时静默丢弃——同 Subscription 本身"满了丢最旧帧"的降级哲学一致。</summary>
    protected void PushData(TDown data) => _subscription?.WriteData(data);
}
