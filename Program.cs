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
/// WpfMultiProcess.exe --child --feature=waveform --socket=C:\...\wpfmp-1234.sock --hostpid=1234</summary>
public sealed record CmdLine(bool IsChild, string FeatureId, string SocketPath, int HostPid)
{
    public static CmdLine Parse(string[] args)
    {
        bool isChild = false;
        string feature = "", socket = "";
        int hostPid = 0;
        foreach (var a in args)
        {
            if (a == "--child") isChild = true;
            else if (a.StartsWith("--feature=")) feature = a["--feature=".Length..];
            else if (a.StartsWith("--socket=")) socket = a["--socket=".Length..];
            else if (a.StartsWith("--hostpid=") && int.TryParse(a["--hostpid=".Length..], out var p)) hostPid = p;
        }
        if (isChild && (feature.Length == 0 || socket.Length == 0))
        {
            MessageBox.Show("子进程缺少 --feature / --socket 参数", "WpfMultiProcess");
            Environment.Exit(2);
        }
        return new CmdLine(isChild, feature, socket, hostPid);
    }
}
