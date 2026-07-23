using System.Diagnostics;
using System.Windows;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Child;

/// <summary>
/// 子进程通用入口:宿主应用自己解析命令行、判断"这是不是子进程"之后,把结果拼成
/// <see cref="ChildStartOptions"/> 连同自己注册的 <see cref="IFeatureChild"/> 列表一起
/// 交给 <see cref="Run"/>。这里负责:孤儿自杀看护(主进程意外死亡时自己退出,避免
/// 留下摸不到主进程、又没人管的孤儿子进程——和 FeatureViewModel 流断开时关窗口是
/// 两道独立的保险)、拉起 ChildWindow、等它的 Win32 摆位做完后再建 channel/ChildShell、
/// 按 featureId 找到对应的 IFeatureChild 组出 ViewModel+View、跑 WPF 消息循环。
/// </summary>
public static class ChildProgram
{
    public static void Run(ChildStartOptions opts, IReadOnlyList<IFeatureChild> features)
    {
        // 主进程意外死亡时自杀,避免孤儿进程
        if (opts.HostPid > 0)
        {
            try
            {
                var host = Process.GetProcessById(opts.HostPid);
                host.EnableRaisingEvents = true;
                host.Exited += (_, _) => Environment.Exit(0);
            }
            catch
            {
                return; // 主进程已不在
            }
        }

        var feature = features.FirstOrDefault(f => f.FeatureId == opts.FeatureId);
        if (feature is null)
        {
            MessageBox.Show($"未注册的 featureId: {opts.FeatureId}", "WpfMultiProcess");
            Environment.Exit(2);
            return;
        }

        var window = new ChildWindow();
        window.SourceReady += () => Bootstrap(window, opts, feature);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(window);
    }

    private static void Bootstrap(ChildWindow window, ChildStartOptions opts, IFeatureChild feature)
    {
        try
        {
            // 建 UDS 通道 + ChildShell(channel/session 身份/心跳协议编排),立即拉起
            // UiSaturationMeter(框架级、feature-无关的 UI 线程饱和度遥测)。
            var channel = GrpcUds.CreateChannel(opts.SocketPath);
            var shell = new ChildShell(window, channel, opts.SessionId, opts.FeatureId, opts.FeatureIndex);
            shell.StartUiSaturationSampling();

            // 把 ChildContext 交给 feature 生成 ViewModel+View,塞进中间区域;该
            // feature 自己的 gRPC stream(Register 带 session_id/hwnd/pid,返回带
            // Reply/Control oneof 的强类型 stream)由 ViewModel 自己开、自己 demux,
            // 连不上/断流时 ViewModel 自己关窗口。
            var ctx = new ChildContext(channel, opts.SessionId, opts.FeatureIndex, shell);
            var viewModel = feature.CreateViewModel(ctx);
            var view = feature.CreateView(viewModel);
            window.SetContent(view);
            viewModel.Start();

            window.Closed += (_, _) =>
            {
                viewModel.Dispose();
                shell.Dispose();
            };
        }
        catch
        {
            window.Close(); // 连不上主进程,直接退出
        }
    }
}
