using WpfMultiProcess.Host.Features.Table;
using WpfMultiProcess.Host.Features.Waveform;

namespace WpfMultiProcess.Host;

/// <summary>全部已注册 feature 模块的清单。新增一个 feature 只需要在这里多塞一行 ——
/// MainWindow 的 tab 列表、子进程启动列表、SessionHub.ReplyOf 的 descriptor 查询都从这里派生。</summary>
public sealed class HostFeatureRegistry
{
    public IReadOnlyList<IFeatureHostModule> Modules { get; }

    public HostFeatureRegistry(WaveformHostModule waveform, TableHostModule table)
    {
        Modules = [waveform, table];
    }

    public FeatureDescriptor DescriptorOf(string featureId) =>
        Modules.First(m => m.FeatureId == featureId).Descriptor;
}
