using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using WpfMultiProcess.Host;
using WpfMultiProcess.Host.Session;

namespace WpfMultiProcess.Demo.Host.Features.Waveform;

/// <summary>waveform feature 的主进程侧接缝:声明展示元数据,每次 OpenFeature 现造一个
/// 独立的 WaveformSession(同一 feature 可以多开),把 WaveformServiceImpl 挂到共享 UDS
/// 端点上(和 CommonService、TableService 共用同一个 Kestrel 通道)。</summary>
public sealed class WaveformFeature : IFeatureHost
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

    public Session CreateSession(FeatureInstanceContext ctx) =>
        new WaveformSession(ctx.SessionId, FeatureId, ctx.FeatureIndex);

    public void MapService(IEndpointRouteBuilder endpoints) => endpoints.MapGrpcService<WaveformServiceImpl>();
}
