using Microsoft.AspNetCore.Routing;

namespace WpfMultiProcess.Host;

/// <summary>feature 作者要实现的主进程侧接缝:声明自己的 id/展示元数据,并把自己的
/// gRPC service 挂到共享的 UDS 端点上(同一个 Kestrel、同一个通道,多个 service)。</summary>
public interface IFeatureHostModule
{
    string FeatureId { get; }
    FeatureDescriptor Descriptor { get; }
    void Map(IEndpointRouteBuilder endpoints);
}

/// <summary>Register 时回给子进程的展示元数据,由 SessionHub.ReplyOf 从 HostFeatureRegistry 查出。</summary>
public sealed class FeatureDescriptor
{
    public required string Title { get; init; }
    public required string AccentColor { get; init; }
    public IDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}
