using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using WpfMultiProcess.Host;

namespace WpfMultiProcess.Demo.Host.Features.Waveform;

/// <summary>waveform feature 的主进程侧接缝:声明展示元数据,把 WaveformServiceImpl
/// 挂到共享 UDS 端点上(和 CommonService、TableService 共用同一个 Kestrel 通道)。
/// WaveformSession 不在这里创建——由 WaveformServiceImpl.Register 收到开流请求时
/// 自己 new 出来,再交给 SessionManager.Register 校验接入。</summary>
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

    public void MapService(IEndpointRouteBuilder endpoints) => endpoints.MapGrpcService<WaveformServiceImpl>();
}
