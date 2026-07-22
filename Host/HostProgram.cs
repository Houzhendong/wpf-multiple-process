using System.IO;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WpfMultiProcess.Host.Features.Table;
using WpfMultiProcess.Host.Features.Waveform;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Host;

/// <summary>主进程入口:先起 Kestrel(gRPC over UDS,同一通道上挂 CommonService +
/// 每个 feature 自己的 service),再跑 WPF 消息循环。</summary>
public static class HostProgram
{
    public static void Run()
    {
        string socketPath = GrpcUds.GetSocketPath(Environment.ProcessId);
        if (File.Exists(socketPath))
            File.Delete(socketPath);

        // registry 先于 hub 构造:hub.ReplyOf 需要查 registry 里的 FeatureDescriptor,
        // 两者都没有别的构造依赖,不走 DI 容器解析,直接 new 就够。
        var registry = new HostFeatureRegistry(new WaveformHostModule(), new TableHostModule());
        var hub = new SessionHub(registry);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(hub);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<CommonServiceImpl>();
        builder.Services.AddSingleton<WaveformServiceImpl>();
        builder.Services.AddSingleton<TableServiceImpl>();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2));

        var web = builder.Build();
        web.MapGrpcService<CommonServiceImpl>();
        foreach (var module in registry.Modules)
            module.Map(web);
        web.Start();

        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new MainWindow(hub, registry, socketPath));
        }
        finally
        {
            hub.Dispose();
            web.StopAsync().GetAwaiter().GetResult();
            try { File.Delete(socketPath); } catch { /* 套接字文件残留无碍 */ }
        }
    }
}
