using System.Diagnostics;
using System.Windows;

namespace WpfMultiProcess.Child;

public static class ChildProgram
{
    public static void Run(CmdLine opts)
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

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new ChildShell(opts, new ChildFeatureRegistry()));
    }
}
