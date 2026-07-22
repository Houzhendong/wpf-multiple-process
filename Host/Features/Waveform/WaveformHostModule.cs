using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace WpfMultiProcess.Host.Features.Waveform;

/// <summary>waveform feature 的主进程侧模块:声明展示元数据,把 WaveformServiceImpl
/// 挂到共享 UDS 端点上(和 CommonService、TableService 共用同一个 Kestrel 通道)。</summary>
public sealed class WaveformHostModule : IFeatureHostModule
{
    public string FeatureId => "waveform";

    public FeatureDescriptor Descriptor { get; } = new()
    {
        Title = "实时波形",
        AccentColor = "#007ACC",
        Settings = new Dictionary<string, string>
        {
            ["data_interval_ms"] = "50",
            ["heartbeat_interval_ms"] = "2000",
        },
    };

    public void Map(IEndpointRouteBuilder endpoints) => endpoints.MapGrpcService<WaveformServiceImpl>();
}
