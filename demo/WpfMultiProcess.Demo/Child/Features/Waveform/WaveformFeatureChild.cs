using System.Windows;
using WpfMultiProcess.Child;
using WpfMultiProcess.Ipc.Waveform;

namespace WpfMultiProcess.Demo.Child.Features.Waveform;

/// <summary>waveform feature 的子进程侧模块(MVVM 版):构造 ViewModel(自建 client、发起
/// Register 开流请求带 session_id/hwnd/pid)+ 构造绑定该 ViewModel 的 View。</summary>
public sealed class WaveformFeatureChild : IFeatureChild
{
    public string FeatureId => "waveform";

    public FeatureViewModel CreateViewModel(ChildContext ctx)
    {
        var client = new WaveformService.WaveformServiceClient(ctx.Channel);
        var call = client.Register(new StreamRequest
        {
            SessionId = ctx.SessionId,
            Hwnd = ctx.Shell.Hwnd,
            Pid = Environment.ProcessId,
        });
        return new WaveformViewModel(ctx.Shell, client, call);
    }

    public FrameworkElement CreateView(FeatureViewModel viewModel) => new WaveformView((WaveformViewModel)viewModel);
}
