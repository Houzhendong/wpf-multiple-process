using System.IO;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WpfMultiProcess.Demo.Host.Features.Table;
using WpfMultiProcess.Demo.Host.Features.Waveform;
using WpfMultiProcess.Host;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Demo.Host;

/// <summary>主进程入口:先建 MainWindow(只搭 UI 外壳),再起 Kestrel(gRPC over UDS,
/// 同一通道上挂 CommonService + 每个 feature 自己的 service)。SessionManager 改为经
/// DI 工厂注册(不再在这里手工 new)——这样 ASP.NET Core 的 Generic Host 能顺手把
/// ILogger&lt;SessionManager&gt; 一起解析进去,不需要框架自己再另起一套 LoggerFactory。
/// 日志 provider 用 AddDebug()(输出到 Visual Studio"输出"窗口),不用默认的 Console
/// provider——主进程和子进程一样是 WinExe 无控制台。最后回调
/// MainWindow.AttachSessionManager 接上事件、自动打开 waveform/table 各一个实例、跑
/// WPF 消息循环。</summary>
public static class HostProgram
{
    public static void Run()
    {
        string socketPath = GrpcUds.GetSocketPath(Environment.ProcessId);
        if (File.Exists(socketPath))
            File.Delete(socketPath);

        IReadOnlyList<IFeatureHost> features = [new WaveformFeature(), new TableFeature()];

        var window = new MainWindow();
        var launch = new SessionLaunchOptions(Environment.ProcessPath!, socketPath);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Services.AddGrpc();
        // 经工厂注册而不是 AddSingleton(实例):让容器负责解析 ILogger<SessionManager>,
        // SessionManager 本身仍然只会被构造一次(单例生命周期)。
        builder.Services.AddSingleton(sp => new SessionManager(launch, features, sp.GetRequiredService<ILogger<SessionManager>>()));
        builder.Services.AddSingleton<CommonServiceImpl>();
        builder.Services.AddSingleton<WaveformServiceImpl>();
        builder.Services.AddSingleton<TableServiceImpl>();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2));

        var web = builder.Build();
        var sessionManager = web.Services.GetRequiredService<SessionManager>();
        web.MapGrpcService<CommonServiceImpl>();
        foreach (var feature in features)
            feature.MapService(web);
        web.Start();

        window.AttachSessionManager(sessionManager, ["waveform", "table"]);

        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(window);
        }
        finally
        {
            sessionManager.Dispose();
            web.StopAsync().GetAwaiter().GetResult();
            try { File.Delete(socketPath); } catch { /* 套接字文件残留无碍 */ }
        }
    }
}
