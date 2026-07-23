using System.Windows;

namespace WpfMultiProcess.Host;

/// <summary>
/// 框架对"dock 容器"的最小抽象——形状特意贴着 Infragistics.Windows.DockManager
/// XamDockManager 设计(见下方映射说明),但这里零依赖它,也零依赖 AvalonDock:
/// SessionManager.OpenFeature 只认这个接口,真正用什么 dock 库由调库方在宿主
/// 应用里实现并注入。demo 用 AvalonDock 实现(见 demo/Host/AvalonDockWorkspace.cs);
/// 真实项目要换成 XamDockManager 只需要另写一个 IDockWorkspace 实现,SessionManager/
/// OverlayHost 都不用改一行。
///
/// 子窗口的 overlay 定位(SetWindowPos + 手动 Z 序)完全靠 PresentationSource +
/// GA_ROOT + WM_WINDOWPOSCHANGED,和这里的 dock 抽象无关——见 OverlayHost,
/// 它只是被当作一个普通 FrameworkElement 塞进 IDockPane.Content。
/// </summary>
public interface IDockWorkspace
{
    /// <summary>新建一个 dock pane 承载 content(一个 OverlayHost)。
    /// 映射到 XamDockManager:大致相当于新建一个 ContentPane 并 Add 进
    /// 某个 SplitPane/TabGroupPane。</summary>
    IDockPane AddPane(string paneId, string title, FrameworkElement content);
}

/// <summary>
/// 一个 dock pane 的句柄。映射到 XamDockManager.ContentPane:
///   - <see cref="Closed"/>   ↔ ContentPane 的 Closed 事件(用户点了关闭按钮/Close())
///   - <see cref="Activated"/> ↔ ContentPane.IsActivePane 变为 true(對應
///     XamDockManager 的 ActivePaneChanged/PaneActivated)
///   - <see cref="IsOpen"/>   ↔ ContentPane 是否仍在 dock 树里(未被 Closed 移除)
///   - <see cref="Close"/>/<see cref="Activate"/> ↔ ContentPane.Close()/Activate()
/// AvalonDock 侧则映射到 LayoutDocument(见 demo/Host/AvalonDockWorkspace.cs)。
/// </summary>
public interface IDockPane
{
    string PaneId { get; }
    FrameworkElement Content { get; }

    /// <summary>用户关闭这个 pane(点击关闭按钮或调用 Close())时触发一次。
    /// SessionManager 订阅它来触发 CloseSession——pane 关闭即子进程退出。</summary>
    event EventHandler? Closed;

    /// <summary>这个 pane 被激活(切到前台/选中对应 tab)时触发。</summary>
    event EventHandler? Activated;

    bool IsOpen { get; }
    void Close();
    void Activate();
}
