using System.Windows;

namespace WpfMultiProcess.Child.Features.Waveform;

/// <summary>waveform feature 的子进程侧模块:唯一职责是按会话生成对应视图。</summary>
public sealed class WaveformChildModule : IFeatureChildModule
{
    public string FeatureId => "waveform";

    public FrameworkElement CreateView(ChildContext ctx) => new WaveformView(ctx);
}
