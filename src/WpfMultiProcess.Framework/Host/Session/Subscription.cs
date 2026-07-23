using System.Threading.Channels;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Host.Session;

/// <summary>
/// 一个会话对应的推送队列,feature-无关的部分抽成这个基类:SessionHub 的心跳循环
/// 只认得 Shared.Control,不知道也不需要知道具体 feature 的 envelope 类型是什么。
/// </summary>
public abstract class Subscription(string sessionId, string featureId)
{
    public string SessionId { get; } = sessionId;
    public string FeatureId { get; } = featureId;

    /// <summary>把会话层的 Control(Ping/Shutdown)写入本 feature 自己的 stream —— 这就是
    /// "主进程侧 wrapControl"接缝:每个 feature 的 TEnv 是什么由 wrap 委托决定,
    /// SessionHub 本身完全不感知 TEnv。</summary>
    public abstract void WriteControl(Control control);

    public abstract void Complete();
}

/// <summary>具体 feature 的订阅:持有一个有界 Channel&lt;TEnv&gt;,满了丢最旧的一帧,
/// 避免子进程消费不过来时把主进程内存拖垮。</summary>
public sealed class Subscription<TEnv>(string sessionId, string featureId, Func<Control, TEnv> wrap)
    : Subscription(sessionId, featureId)
{
    private readonly Channel<TEnv> _channel = Channel.CreateBounded<TEnv>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<TEnv> Reader => _channel.Reader;

    public override void WriteControl(Control control) => _channel.Writer.TryWrite(wrap(control));

    /// <summary>feature 自己的业务数据帧,直接写进同一条 stream。</summary>
    public void WriteData(TEnv env) => _channel.Writer.TryWrite(env);

    public override void Complete() => _channel.Writer.TryComplete();
}
