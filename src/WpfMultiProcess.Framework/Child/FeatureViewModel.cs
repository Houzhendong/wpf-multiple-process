using System.ComponentModel;
using Grpc.Core;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Child;

/// <summary>
/// 子进程 feature 视图模型的非泛型基类。feature 作者不会直接继承这个类,而是继承下面
/// 携带具体 envelope 类型的 <see cref="FeatureViewModel{TDown}"/>——这个类存在的意义
/// 和 Host 侧 Session/Session&lt;TDown&gt; 的拆分完全一样:<see cref="IFeatureChild"/>、
/// ChildProgram 的 bootstrap 流程都只需要认识"一个 feature 视图模型"这个概念,不需要
/// 知道具体的 TDown 是什么。
///
/// 提供两个 feature-无关的 demux 落点:<see cref="HandleControl"/>(Ping→回 Pong 证明
/// UI 未卡死;Shutdown→关子窗口)、<see cref="OnReply"/>(默认把 Reply 里的标题/主题色
/// 转给 ChildWindow,可 override 附加逻辑,记得调用 base)。<see cref="Start"/>/
/// <see cref="Dispose"/> 是 ChildProgram bootstrap 用来驱动生命周期的接缝。
/// </summary>
public abstract class FeatureViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>本 feature 实例所在的 ChildShell(channel/session 身份/心跳协议编排)。</summary>
    protected ChildShell Shell { get; }

    /// <summary>子窗口自己的 Win32 句柄,feature 视图判断"是否可见"(hide-stops-render)
    /// 时要用到——直接转发自 Shell,免得每个 feature 视图都要自己拿 Shell.Hwnd。</summary>
    public nint Hwnd => Shell.Hwnd;

    protected FeatureViewModel(ChildShell shell) => Shell = shell;

    /// <summary>ChildProgram bootstrap 把 View 塞进 ChildWindow 之后调用一次,开始跑
    /// response-stream 循环。</summary>
    public void Start() => _ = RunAsync(_cts.Token);

    private protected abstract Task RunAsync(CancellationToken ct);

    /// <summary>Ping→回 Pong(在 UI 线程,证明 UI 未卡死)。proto 里 Control 包装已经去掉,
    /// Ping/Shutdown 现在是 XxxDown 的两个独立 oneof 分支,子类的 Dispatch 分派要各自
    /// 落到这两个方法上。</summary>
    protected void HandlePing(Ping ping) => Shell.SendPong(ping);

    /// <summary>Shutdown→关子窗口。reason 只用于诊断日志(M5 会接入 ILogger 后此处改为
    /// 结构化输出),不影响"收到就关窗口"这个行为本身。</summary>
    protected void HandleShutdown(Shutdown shutdown)
    {
        System.Diagnostics.Debug.WriteLine($"[FeatureViewModel] Shutdown reason={shutdown.Reason} detail={shutdown.Detail}");
        Shell.RequestClose();
    }

    /// <summary>Register 流第一条 Reply 落地时调用,默认设标题/主题色;override 时记得
    /// 调用 base.OnReply(reply) 以保留这个默认行为。</summary>
    protected virtual void OnReply(RegisterReply reply) => Shell.ApplyReply(reply);

    public virtual void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 携带具体 feature envelope 类型(TDown)的 FeatureViewModel 基类——feature 作者的
/// 子进程侧 ViewModel(如 demo 的 WaveformViewModel/TableViewModel)继承这个类:
/// 构造时传入已经发起的 <see cref="AsyncServerStreamingCall{TDown}"/>(由 feature 自己
/// 的 gRPC client 调 Register 得到),基类的 <see cref="RunAsync"/> 循环读这个 call 的
/// ResponseStream、对每个 envelope 调用抽象的 <see cref="Dispatch"/>——具体 oneof
/// 三路分派(Reply/Control/数据帧)由 override 决定,数据帧一路再调用抽象的
/// <see cref="OnData"/> 更新自己的绑定属性/集合。stream 断开(含主进程不可达)时关闭
/// 整个子窗口——双保险之一,另一个是 ChildProgram 的孤儿自杀。
/// </summary>
public abstract class FeatureViewModel<TDown> : FeatureViewModel
{
    private readonly AsyncServerStreamingCall<TDown> _call;

    protected FeatureViewModel(ChildShell shell, AsyncServerStreamingCall<TDown> call) : base(shell)
    {
        _call = call;
    }

    private protected override async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var env in _call.ResponseStream.ReadAllAsync(ct))
                Dispatch(env);
        }
        catch (OperationCanceledException)
        {
            // 正常 Dispose/关闭
        }
        catch (Exception)
        {
            // 主进程不可达/流异常终止:关掉子窗口,不留一个连不上的空壳子进程。
            _ = Shell.Window.Dispatcher.BeginInvoke(() => Shell.Window.Close());
        }
    }

    /// <summary>典型实现是按 KindCase 三路分派:Reply→OnReply(env.Reply);
    /// Control→HandleControl(env.Control);数据帧→自己决定是否 Dispatcher.BeginInvoke
    /// 再调 OnData(env)——注意这一路不像 HandleControl/OnReply 那样自动落到 UI 线程,
    /// 因为只有 feature 作者自己知道 OnData 里更新的是哪些绑定属性。</summary>
    protected abstract void Dispatch(TDown env);

    /// <summary>数据帧到达时调用,由 feature 作者更新自己的绑定属性/集合。</summary>
    protected abstract void OnData(TDown data);

    public override void Dispose()
    {
        _call.Dispose();
        base.Dispose();
    }
}
