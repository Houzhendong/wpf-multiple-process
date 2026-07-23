using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Child;

/// <summary>
/// 子进程通用入口:宿主应用自己解析命令行、判断"这是不是子进程"之后,把结果拼成
/// <see cref="ChildStartOptions"/>、自己注册的 <see cref="IFeatureChild"/> 列表,连同一个
/// 自己造好的 <see cref="IHost"/>(典型用法是 <c>Host.CreateApplicationBuilder()</c> 接
/// 上想要的日志 provider、注册好一个 <see cref="Application"/> 单例后 Build())一起交给
/// <see cref="Run"/>。这个 IHost 首先是 DI/日志容器——WPF 消息循环仍然是这里的
/// <c>Application.Run</c> 在跑,不委托给 IHost 的 hosted service 生命周期,所以只需要
/// <see cref="IHost.Start"/>/<see cref="IHost.StopAsync"/> 让容器就绪/收尾,不需要
/// IHostedService。同时它也是 <see cref="Application"/> 实例的来源——这里不自己
/// <c>new Application()</c>,而是从 <c>host.Services.GetRequiredService&lt;Application&gt;()</c>
/// 取,好让调库方在自己的 Program.cs 里按需要注册自定义 Application 子类/附加资源
/// (ResourceDictionary、全局异常处理等),框架不替调库方决定这些。
///
/// 这里负责:孤儿自杀看护(主进程意外死亡时自己退出,避免留下摸不到主进程、又没人管
/// 的孤儿子进程——和 FeatureViewModel 流断开时关窗口是两道独立的保险)、拉起
/// ChildWindow、等它的 Win32 摆位做完后再建 channel/ChildShell、按 featureId 找到对应的
/// IFeatureChild 组出 ViewModel+View、跑 WPF 消息循环。诊断日志(孤儿自杀、未知
/// featureId、bootstrap 异常)经 host 的 <see cref="ILoggerFactory"/> 输出,和
/// SessionManager.FeatureLog 那条"业务事件转发到主窗口 UI"的通道完全分开——这里纯粹是
/// 给开发者/运维看的诊断信息,不经 gRPC 传回主进程。
/// </summary>
public static class ChildProgram
{
    public static void Run(IHost host, ChildStartOptions opts, IReadOnlyList<IFeatureChild> features)
    {
        host.Start();
        var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(ChildProgram));

        try
        {
            RunCore(host, loggerFactory, logger, opts, features);
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
            host.Dispose();
        }
    }

    private static void RunCore(IHost host, ILoggerFactory loggerFactory, ILogger logger,
        ChildStartOptions opts, IReadOnlyList<IFeatureChild> features)
    {
        // 主进程意外死亡时自杀,避免孤儿进程
        if (opts.HostPid > 0)
        {
            try
            {
                var hostProcess = Process.GetProcessById(opts.HostPid);
                hostProcess.EnableRaisingEvents = true;
                hostProcess.Exited += (_, _) =>
                {
                    logger.LogInformation("host process pid={HostPid} exited, self-terminating", opts.HostPid);
                    Environment.Exit(0);
                };
            }
            catch
            {
                logger.LogWarning("host process pid={HostPid} already gone at startup, exiting immediately", opts.HostPid);
                return; // 主进程已不在
            }
        }

        var feature = features.FirstOrDefault(f => f.FeatureId == opts.FeatureId);
        if (feature is null)
        {
            logger.LogError("unknown featureId: {FeatureId}", opts.FeatureId);
            MessageBox.Show($"未注册的 featureId: {opts.FeatureId}", "WpfMultiProcess");
            Environment.Exit(2);
            return;
        }

        var window = new ChildWindow();
        window.SourceReady += () => Bootstrap(window, opts, feature, loggerFactory, logger);

        // Application 不在这里 new——从 host.Services 里取,让调库方(典型是自己的
        // Program.cs)决定怎么构造/配置它,方便附加自己的资源(ResourceDictionary、
        // 自定义 Application 子类等)。框架只负责跑 Application.Run(window)。
        var app = host.Services.GetRequiredService<Application>();
        app.Run(window);
    }

    private static void Bootstrap(ChildWindow window, ChildStartOptions opts, IFeatureChild feature,
        ILoggerFactory loggerFactory, ILogger logger)
    {
        try
        {
            // 建 UDS 通道 + ChildShell(channel/session 身份/心跳协议编排),立即拉起
            // UiSaturationMeter(框架级、feature-无关的 UI 线程饱和度遥测)。
            var channel = GrpcUds.CreateChannel(opts.SocketPath);
            var shell = new ChildShell(window, channel, opts.SessionId, opts.FeatureId, opts.FeatureIndex, loggerFactory);
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
        catch (Exception ex)
        {
            logger.LogError(ex, "bootstrap failed for featureId={FeatureId} sessionId={SessionId}, closing child window",
                opts.FeatureId, opts.SessionId);
            window.Close(); // 连不上主进程,直接退出
        }
    }
}
