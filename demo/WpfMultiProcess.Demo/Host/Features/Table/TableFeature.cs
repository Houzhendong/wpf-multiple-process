using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using WpfMultiProcess.Host;
using WpfMultiProcess.Host.Session;

namespace WpfMultiProcess.Demo.Host.Features.Table;

/// <summary>table feature 的主进程侧接缝:声明展示元数据,每次 OpenFeature 现造一个独立的
/// TableSession,把 TableServiceImpl 挂到共享 UDS 端点上。</summary>
public sealed class TableFeature : IFeatureHost
{
    public string FeatureId => "table";

    public FeatureDescriptor Descriptor { get; } = new()
    {
        Title = "数据表格",
        AccentColor = "#68217A",
        Settings = new Dictionary<string, string>
        {
            ["data_interval_ms"] = "50",
            ["heartbeat_interval_ms"] = "2000",
        },
    };

    public Session CreateSession(FeatureInstanceContext ctx) =>
        new TableSession(ctx.SessionId, FeatureId);

    public void MapService(IEndpointRouteBuilder endpoints) => endpoints.MapGrpcService<TableServiceImpl>();
}
