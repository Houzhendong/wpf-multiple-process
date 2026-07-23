using System.Windows;
using WpfMultiProcess.Child;
using WpfMultiProcess.Ipc.Table;

namespace WpfMultiProcess.Demo.Child.Features.Table;

/// <summary>table feature 的子进程侧模块(MVVM 版):构造 ViewModel(自建 client、发起
/// Register 开流请求带 session_id/hwnd/pid)+ 构造绑定该 ViewModel 的 View。</summary>
public sealed class TableFeatureChild : IFeatureChild
{
    public string FeatureId => "table";

    public FeatureViewModel CreateViewModel(ChildContext ctx)
    {
        var client = new TableService.TableServiceClient(ctx.Channel);
        var call = client.Register(new StreamRequest
        {
            SessionId = ctx.SessionId,
            Hwnd = ctx.Shell.Hwnd,
            Pid = Environment.ProcessId,
        });
        return new TableViewModel(ctx.Shell, client, call);
    }

    public FrameworkElement CreateView(FeatureViewModel viewModel) => new TableView((TableViewModel)viewModel);
}
