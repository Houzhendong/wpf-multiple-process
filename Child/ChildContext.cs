using Grpc.Net.Client;

namespace WpfMultiProcess.Child;

/// <summary>
/// 子进程侧不再有独立的 ChildSession 对象——会话就是"一个已经建好的 gRPC 通道 +
/// host 生成的 session_id + 宿主 ChildShell"这三样东西的组合,原样交给 feature 视图。
/// feature 视图拿到 ChildContext 后自己用 Channel 建 feature client、自己发起
/// Register 开流请求(带 session_id/hwnd/pid),demux 出来的 Reply/Control 转发给
/// ctx.Shell 对应的方法(ApplyReply / OnPing / RequestClose)即可,不需要额外的
/// 会话对象来居中转发。
/// </summary>
public readonly record struct ChildContext(GrpcChannel Channel, string SessionId, ChildShell Shell);
