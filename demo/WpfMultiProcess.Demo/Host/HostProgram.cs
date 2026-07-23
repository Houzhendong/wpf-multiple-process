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

/// <summary>主进程入口:先建 MainWindow(只搭 UI 外壳),再造 SessionManager(不再
/// 需要 MainWindow 的任何东西——featureIndex/IDockPane 现在由 MainWindow 在
/// OpenFeatureInstance 里自己决定/建好再传给 OpenFeature),然后起 Kestrel(gRPC over
/// UDS,同一通道上挂 CommonService + 每个 feature 自己的 service,构造函数都吃同一个
/// SessionManager 单例),最后回调 MainWindow.AttachSessionManager 接上事件、自动
/// 打开 waveform/table 各一个实例、跑 WPF 消息循环。</summary>
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
        var sessionManager = new SessionManager(launch, features);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(sessionManager);
        builder.Services.AddSingleton<CommonServiceImpl>();
        builder.Services.AddSingleton<WaveformServiceImpl>();
        builder.Services.AddSingleton<TableServiceImpl>();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2));

        var web = builder.Build();
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
