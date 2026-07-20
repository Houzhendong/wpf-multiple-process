using System.IO;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Host;

/// <summary>主进程入口:先起 Kestrel(gRPC over UDS),再跑 WPF 消息循环。</summary>
public static class HostProgram
{
    public static void Run()
    {
        string socketPath = GrpcUds.GetSocketPath(Environment.ProcessId);
        if (File.Exists(socketPath))
            File.Delete(socketPath);

        var coordinator = new HostCoordinator();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(coordinator);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2));

        var web = builder.Build();
        web.MapGrpcService<IpcService>();
        web.Start();

        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new MainWindow(coordinator, socketPath));
        }
        finally
        {
            coordinator.Dispose();
            web.StopAsync().GetAwaiter().GetResult();
            try { File.Delete(socketPath); } catch { /* 套接字文件残留无碍 */ }
        }
    }
}
