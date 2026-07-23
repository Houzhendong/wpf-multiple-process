using Microsoft.AspNetCore.Routing;

namespace WpfMultiProcess.Host;

/// <summary>调库方要实现的主进程侧接缝(原 IFeatureHostModule 演进):声明自己的
/// id/展示元数据,按 <see cref="SessionManager.OpenFeature"/> 传入的实例上下文
/// 造一个自己的 <see cref="Session"/> 子类,并把自己的 gRPC service 挂到共享的
/// UDS 端点上(同一个 Kestrel、同一个通道,多个 service)。</summary>
public interface IFeatureHost
{
    string FeatureId { get; }
    FeatureDescriptor Descriptor { get; }

    /// <summary>SessionManager.OpenFeature 每次调用都会现造一个新的 Session 实例
    /// (同一 feature 可以多开,每次都是独立的会话/独立的子进程)。</summary>
    global::WpfMultiProcess.Host.Session.Session CreateSession(FeatureInstanceContext ctx);

    void MapService(IEndpointRouteBuilder endpoints);
}

/// <summary>SessionManager.OpenFeature 分配好 session_id/featureIndex 之后,
/// 传给 IFeatureHost.CreateSession 的上下文。</summary>
public sealed record FeatureInstanceContext(string SessionId, int FeatureIndex);

/// <summary>Register 时回给子进程的展示元数据,由 SessionManager.ReplyOf 从注册的
/// IFeatureHost 列表里查出。</summary>
public sealed class FeatureDescriptor
{
    public required string Title { get; init; }
    public required string AccentColor { get; init; }
    public IDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}
