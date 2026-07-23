using System.Windows;
using Microsoft.Extensions.Logging;
using WpfMultiProcess.Child;
using WpfMultiProcess.Demo.Child.Features.Table;
using WpfMultiProcess.Demo.Child.Features.Waveform;
using WpfMultiProcess.Demo.Host;
// 项目里已经有个 WpfMultiProcess.Demo.Host 命名空间(HostProgram/MainWindow 所在),
// 和 Microsoft.Extensions.Hosting.Host 这个静态类同名——起个别名避免二义性。
using GenericHost = Microsoft.Extensions.Hosting.Host;

namespace WpfMultiProcess.Demo;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var opts = CmdLine.Parse(args);
        if (opts.IsChild)
        {
            var startOptions = new ChildStartOptions(opts.FeatureId, opts.FeatureIndex, opts.SessionId, opts.SocketPath, opts.HostPid);

            // ChildProgram.Run 只把这个 IHost 当 DI/日志容器用(WPF 消息循环仍由它内部的
            // Application.Run 驱动,不靠 IHost 的 hosted service 生命周期),所以这里
            // 不需要注册任何 hosted service,只挂日志——AddDebug() 输出到 Visual Studio
            // "输出"窗口,子进程是 WinExe 无控制台,不用 Console provider。
            var hostBuilder = GenericHost.CreateApplicationBuilder();
            hostBuilder.Logging.ClearProviders();
            hostBuilder.Logging.AddDebug();
            var host = hostBuilder.Build();

            // ChildProgram.Run 拥有 host 的生命周期(Start/StopAsync/Dispose 都在它内部
            // 的 try/finally 里做),这里不用 using,避免和它内部的 Dispose 重复。
            ChildProgram.Run(host, startOptions, [new WaveformFeatureChild(), new TableFeatureChild()]);
        }
        else
        {
            HostProgram.Run();
        }
    }
}

/// <summary>命令行参数。主进程无参数;子进程形如:
/// WpfMultiProcess.Demo.exe --child --feature=waveform --index=0 --session=&lt;guid&gt;
/// --socket=C:\...\wpfmp-1234.sock --hostpid=1234
/// session_id/index 都由主进程在拉起子进程前生成(见 SessionManager.OpenFeature),作
/// 启动参数传入,子进程开 feature 流时原样带上,不再靠一次 Register RPC 向主进程换取。
/// index 是同一 feature 第几次被打开的实例(0 起),支持同一 feature 多开——标题/日志
/// 里的 " #N" 就来自这里。</summary>
public sealed record CmdLine(bool IsChild, string FeatureId, int FeatureIndex, string SessionId, string SocketPath, int HostPid)
{
    public static CmdLine Parse(string[] args)
    {
        bool isChild = false;
        string feature = "", session = "", socket = "";
        int hostPid = 0, index = 0;
        foreach (var a in args)
        {
            if (a == "--child") isChild = true;
            else if (a.StartsWith("--feature=")) feature = a["--feature=".Length..];
            else if (a.StartsWith("--index=") && int.TryParse(a["--index=".Length..], out var i)) index = i;
            else if (a.StartsWith("--session=")) session = a["--session=".Length..];
            else if (a.StartsWith("--socket=")) socket = a["--socket=".Length..];
            else if (a.StartsWith("--hostpid=") && int.TryParse(a["--hostpid=".Length..], out var p)) hostPid = p;
        }
        if (isChild && (feature.Length == 0 || session.Length == 0 || socket.Length == 0))
        {
            MessageBox.Show("子进程缺少 --feature / --session / --socket 参数", "WpfMultiProcess");
            Environment.Exit(2);
        }
        return new CmdLine(isChild, feature, index, session, socket, hostPid);
    }
}
