using WpfMultiProcess.Child.Features.Table;
using WpfMultiProcess.Child.Features.Waveform;

namespace WpfMultiProcess.Child;

/// <summary>子进程侧的 feature 模块清单,按 --feature= 取对应模块。</summary>
public sealed class ChildFeatureRegistry
{
    private readonly Dictionary<string, IFeatureChildModule> _modules;

    public ChildFeatureRegistry()
    {
        IFeatureChildModule waveform = new WaveformChildModule();
        IFeatureChildModule table = new TableChildModule();
        _modules = new Dictionary<string, IFeatureChildModule>
        {
            [waveform.FeatureId] = waveform,
            [table.FeatureId] = table,
        };
    }

    public IFeatureChildModule Get(string featureId) => _modules[featureId];
}
