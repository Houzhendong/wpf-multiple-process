using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using WpfMultiProcess.Host;

namespace WpfMultiProcess.Demo.Host.Features.Table;

/// <summary>table feature 的主进程侧接缝:声明展示元数据,把 TableServiceImpl 挂到
/// 共享 UDS 端点上。TableSession 不在这里创建——由 TableServiceImpl.Register 收到
/// 开流请求时自己 new 出来,再交给 SessionManager.Register 校验接入。</summary>
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

    public void MapService(IEndpointRouteBuilder endpoints) => endpoints.MapGrpcService<TableServiceImpl>();
}
