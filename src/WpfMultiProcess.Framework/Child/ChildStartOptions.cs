namespace WpfMultiProcess.Child;

/// <summary>子进程启动所需的固定参数,由宿主应用自己的命令行解析(如 demo 的 CmdLine)
/// 拼出来交给 <see cref="ChildProgram.Run"/>——是否为子进程这一判断本身是宿主应用的
/// CLI 关注点,不在这个类型里。</summary>
public sealed record ChildStartOptions(string FeatureId, int FeatureIndex, string SessionId, string SocketPath, int HostPid);
