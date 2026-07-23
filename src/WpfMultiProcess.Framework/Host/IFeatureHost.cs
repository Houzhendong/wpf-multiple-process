using Microsoft.AspNetCore.Routing;

namespace WpfMultiProcess.Host;

/// <summary>调库方要实现的主进程侧接缝(原 IFeatureHostModule 演进):声明自己的
/// id/展示元数据,把自己的 gRPC service 挂到共享的 UDS 端点上(同一个 Kestrel、同一个
/// 通道,多个 service)。会话本身不再由这里创建——子进程连上来发起 Register 时,
/// 由调库方自己的 gRPC service 实现(MapService 挂的那个)现造自己的 Session 子类,
/// 再经 SessionManager.Register 校验接入,详见 <see cref="global::WpfMultiProcess.Host.Session.SessionManager"/>。</summary>
public interface IFeatureHost
{
    string FeatureId { get; }
    FeatureDescriptor Descriptor { get; }

    void MapService(IEndpointRouteBuilder endpoints);
}

/// <summary>Register 时回给子进程的展示元数据,由 SessionManager.ReplyOf 从注册的
/// IFeatureHost 列表里查出。</summary>
public sealed class FeatureDescriptor
{
    public required string Title { get; init; }
    public required string AccentColor { get; init; }
    public IDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}
