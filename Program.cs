using System.Windows;
using WpfMultiProcess.Child;
using WpfMultiProcess.Host;

namespace WpfMultiProcess;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var opts = CmdLine.Parse(args);
        if (opts.IsChild)
            ChildProgram.Run(opts);
        else
            HostProgram.Run();
    }
}

/// <summary>命令行参数。主进程无参数;子进程形如:
/// WpfMultiProcess.exe --child --feature=waveform --session=&lt;guid&gt; --socket=C:\...\wpfmp-1234.sock --hostpid=1234
/// session_id 由主进程在拉起子进程前生成(见 MainWindow.LaunchChild),作启动参数传入,
/// 子进程开 feature 流时原样带上,不再靠一次 Register RPC 向主进程换取。</summary>
public sealed record CmdLine(bool IsChild, string FeatureId, string SessionId, string SocketPath, int HostPid)
{
    public static CmdLine Parse(string[] args)
    {
        bool isChild = false;
        string feature = "", session = "", socket = "";
        int hostPid = 0;
        foreach (var a in args)
        {
            if (a == "--child") isChild = true;
            else if (a.StartsWith("--feature=")) feature = a["--feature=".Length..];
            else if (a.StartsWith("--session=")) session = a["--session=".Length..];
            else if (a.StartsWith("--socket=")) socket = a["--socket=".Length..];
            else if (a.StartsWith("--hostpid=") && int.TryParse(a["--hostpid=".Length..], out var p)) hostPid = p;
        }
        if (isChild && (feature.Length == 0 || session.Length == 0 || socket.Length == 0))
        {
            MessageBox.Show("子进程缺少 --feature / --session / --socket 参数", "WpfMultiProcess");
            Environment.Exit(2);
        }
        return new CmdLine(isChild, feature, session, socket, hostPid);
    }
}
