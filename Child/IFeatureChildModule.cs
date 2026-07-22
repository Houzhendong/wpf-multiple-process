using System.Windows;

namespace WpfMultiProcess.Child;

/// <summary>feature 作者要实现的子进程侧接缝:根据已建好的 ChildContext 构造自己的视图,
/// 视图内部自行开该 feature 的 gRPC stream(Register 带 session_id/hwnd/pid)并做 demux
/// (Reply → ctx.Shell.ApplyReply;Control.Ping → ctx.Shell.OnPing;Control.Shutdown →
/// ctx.Shell.RequestClose;业务数据帧 → 更新自己的 UI)。</summary>
public interface IFeatureChildModule
{
    string FeatureId { get; }
    FrameworkElement CreateView(ChildContext ctx);
}
