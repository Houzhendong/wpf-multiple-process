using System.Threading.Channels;

namespace WpfMultiProcess.Host.Session;

/// <summary>
/// 一个会话对应的推送队列,feature-无关的部分抽成这个基类:SessionManager 的心跳/
/// 无响应检测循环只需要按 sessionId 找到它、判断"这个会话是否已连接",不需要知道
/// 具体 feature 的 envelope 类型是什么。
/// </summary>
public abstract class Subscription(string sessionId, string featureId)
{
    public string SessionId { get; } = sessionId;
    public string FeatureId { get; } = featureId;

    public abstract void Complete();
}

/// <summary>具体 feature 的订阅:持有一个有界 Channel&lt;TEnv&gt;,满了丢最旧的一帧,
/// 避免子进程消费不过来时把主进程内存拖垮。心跳/关闭(Ping/Shutdown)现在由
/// Session&lt;TDown&gt; 自己 wrap 成 TEnv 后一样经 <see cref="WriteData"/> 写进来,
/// 这里不再需要单独的 WriteControl 接缝。</summary>
public sealed class Subscription<TEnv>(string sessionId, string featureId)
    : Subscription(sessionId, featureId)
{
    private readonly Channel<TEnv> _channel = Channel.CreateBounded<TEnv>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<TEnv> Reader => _channel.Reader;

    /// <summary>feature 自己的业务数据帧,或者 Session&lt;TDown&gt; wrap 好的心跳/关闭帧,
    /// 都经这一个方法写进同一条 stream。</summary>
    public void WriteData(TEnv env) => _channel.Writer.TryWrite(env);

    public override void Complete() => _channel.Writer.TryComplete();
}
