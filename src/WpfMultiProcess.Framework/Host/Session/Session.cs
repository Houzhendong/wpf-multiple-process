using Grpc.Core;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Host.Session;

/// <summary>
/// 一个子进程实例的抽象——框架"只操作 session":调库方继承这个类,自己决定数据管道
/// 怎么实现(典型做法是内部自建一个 <c>Channel&lt;TDown&gt;</c> + 一个把这个 channel
/// 泵进 gRPC stream 的循环,见 <see cref="SessionPump"/>),框架本身完全不知道 TDown
/// 是什么类型,只调用这里声明的这几个方法/钩子。
///
/// 不再有泛型的 <c>Session&lt;TDown&gt;</c>/<c>Subscription&lt;TDown&gt;</c>——那一套是
/// "框架代持数据管道"的设计,现在数据管道(Channel/队列 + 怎么把 Ping/Shutdown 包装成
/// 自己的 envelope)完全下沉给实现方,框架的契约收缩成:
///   - <see cref="SendHeartbeat"/>/<see cref="SendClose"/>:框架心跳循环/CloseSession
///     调用,实现方把 Ping/Shutdown 包成自己的 envelope 写进自己的管道;
///   - <see cref="ServeAsync{T}"/>:接收 <see cref="IServerStreamWriter{T}"/> 的所有权——
///     feature service 的 Register 处理器(实现方代码)在
///     <c>SessionManager.Register(session,...)</c> 成功、写完 Reply 后 await 它,直到
///     会话结束才返回。调用方(实现方自己的 service impl)传进来的 T 恒等于该实现方自己
///     的 TDown,实现里 override 时把 <paramref name="writer"/> cast 回具体类型即可
///     (framework 不做也不能做这个类型校验,cast 失败说明调用方自己传错了类型)。
///   - 生命周期钩子(<see cref="OnConnected"/>/<see cref="OnPong"/>/<see cref="OnUiStats"/>/
///     <see cref="OnDisconnected"/>)保留,时机不变。
///   - <see cref="IDisposable.Dispose"/>:实现方在这里退订/清理自己的数据管道;框架保证
///     会话结束(Unregister)时恰好调用一次——见 <see cref="DisposeOnce"/>,这是个幂等门,
///     同一个 Session 可能同时经 CloseSession 和 stream 的 finally 两条路径触发清理,
///     幂等门确保只有第一次真正调用到 <see cref="Dispose"/>。
/// </summary>
public abstract class Session : IDisposable
{
    private int _disposed;

    /// <summary>MainWindow/SessionManager 在拉起子进程之前生成的会话标识,作为 --session
    /// 启动参数传给子进程。</summary>
    public string SessionId { get; }

    public string FeatureId { get; }

    /// <summary>同一 feature 下第几次 OpenFeature 打开的实例(0 起),支持同一 feature
    /// 多开。由 SessionManager 在 Register 时从预登记表回填,构造时还不知道。</summary>
    public int FeatureIndex { get; internal set; }

    /// <summary>子进程开流请求带上来的 pid/hwnd,Register 校验通过后回填。</summary>
    public int Pid { get; internal set; }
    public nint Hwnd { get; internal set; }

    protected Session(string sessionId, string featureId)
    {
        SessionId = sessionId;
        FeatureId = featureId;
    }

    /// <summary>心跳:SessionManager 心跳循环里对每个已连接的会话调用,实现方把 Ping
    /// 包成自己的 envelope 写进自己的管道(管道满了要不要丢包、丢哪个,由实现方决定,
    /// 框架不做假设)。</summary>
    public abstract void SendHeartbeat(Ping ping);

    /// <summary>关闭:CloseSession / 主窗口关闭时调用,reason 由框架按触发源填好
    /// (见 SessionManager.CloseSession/CloseAll)。</summary>
    public abstract void SendClose(Shutdown shutdown);

    /// <summary>接收 <see cref="IServerStreamWriter{T}"/> 的所有权:feature service 的
    /// Register 处理器写完第一条 Reply 之后调用这个方法并 await 它,直到会话结束(流断开/
    /// 取消/正常关闭)才返回。典型实现是把自己内部 Channel 的 Reader 和这个 writer 一起
    /// 交给 <see cref="SessionPump.PumpAsync{T}"/>。</summary>
    public abstract Task ServeAsync<T>(IServerStreamWriter<T> writer, CancellationToken ct);

    /// <summary>SessionManager.Register 校验通过、hwnd 落地时调用(SessionManager 内部)。
    /// 典型用途:调库方在这里启动自己的数据 producer。</summary>
    protected internal virtual void OnConnected(nint hwnd) { }

    /// <summary>收到这个会话的 Pong 时调用(seq 回显、rttMs 是主进程发出 Ping 到子进程
    /// UI 线程回 Pong 的往返耗时)。</summary>
    protected internal virtual void OnPong(long seq, double rttMs) { }

    /// <summary>收到这个会话的 UiSaturationMeter 上报时调用。</summary>
    protected internal virtual void OnUiStats(UiStatsRequest stats) { }

    /// <summary>会话断开(子进程退出/stream 断开)时调用。典型用途:停掉自己的 producer。
    /// 注意这个钩子和 <see cref="Dispose"/> 不是一回事:OnDisconnected 只在"曾经连接过"
    /// 时触发,Dispose 无论有没有连接成功都保证恰好一次。</summary>
    protected internal virtual void OnDisconnected() { }

    /// <summary>实现方在这里退订/清理自己的数据管道(典型做法:Complete 自己的 Channel
    /// writer、取消自己的 producer CancellationTokenSource)。框架通过
    /// <see cref="DisposeOnce"/> 保证这个方法只会被真正调用一次。</summary>
    public virtual void Dispose() { }

    /// <summary>框架内部(SessionManager.Unregister/CloseSession)调用的幂等门:同一个
    /// 会话结束时可能有两条路径都想清理它(用户主动 CloseSession、feature service 的
    /// Register 流自然结束),这里保证 <see cref="Dispose"/> 只被调用一次。</summary>
    internal void DisposeOnce()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Dispose();
    }
}
