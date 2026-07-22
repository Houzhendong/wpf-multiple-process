using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace WpfMultiProcess.Host.Features.Table;

/// <summary>table feature 的主进程侧模块:声明展示元数据,把 TableServiceImpl
/// 挂到共享 UDS 端点上。</summary>
public sealed class TableHostModule : IFeatureHostModule
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

    public void Map(IEndpointRouteBuilder endpoints) => endpoints.MapGrpcService<TableServiceImpl>();
}
