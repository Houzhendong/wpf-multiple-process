using System.Windows;

namespace WpfMultiProcess.Host;

/// <summary>
/// 框架对"dock pane"的最小抽象——形状特意贴着 Infragistics.Windows.DockManager
/// XamDockManager.ContentPane 设计(见下方映射说明),零依赖 AvalonDock/XamDockManager
/// 任何一个具体实现。原来还有一层 IDockWorkspace(AddPane 工厂,由框架负责"造 pane"
/// 这件事),现在整个删掉——造 pane(LayoutDocument/ContentPane 之类)是调库方自己
/// UI 代码的职责(demo 里是 MainWindow 自己 new LayoutDocument),
/// SessionManager.OpenFeature 只接收一个调用方已经造好的 IDockPane,调
/// <see cref="SetContent"/> 把 OverlayHost 塞进去——这样"同一菜单下多开出 N 个同
/// feature 实例,index 要和菜单自己维护的计数对应"这种场景,index 和 pane 都能由
/// 调用方一次性备齐再传给 OpenFeature,不需要框架倒过来猜。
///
/// 子窗口的 overlay 定位(SetWindowPos + 手动 Z 序)完全靠 PresentationSource +
/// GA_ROOT + WM_WINDOWPOSCHANGED,和这里的 dock 抽象无关——见 OverlayHost,
/// 它只是被当作一个普通 FrameworkElement 塞进 pane。
///
/// 映射到 XamDockManager.ContentPane:
///   - <see cref="SetContent"/> ↔ ContentPane.Content = ...
///   - <see cref="Closed"/>     ↔ ContentPane 的 Closed 事件(用户点了关闭按钮/Close())
///   - <see cref="Activated"/>  ↔ ContentPane.IsActivePane 变为 true(對應
///     XamDockManager 的 ActivePaneChanged/PaneActivated)
///   - <see cref="IsOpen"/>    ↔ ContentPane 是否仍在 dock 树里(未被 Closed 移除)
///   - <see cref="Close"/>/<see cref="Activate"/> ↔ ContentPane.Close()/Activate()
/// AvalonDock 侧映射到 LayoutDocument(见 demo/Host/AvalonDockPane.cs)。
/// </summary>
public interface IDockPane
{
    string PaneId { get; }

    /// <summary>把 OverlayHost(或任意 FrameworkElement)塞进这个 pane。
    /// SessionManager.OpenFeature 造好 OverlayHost 后调用一次。</summary>
    void SetContent(FrameworkElement content);

    /// <summary>用户关闭这个 pane(点击关闭按钮或调用 Close())时触发一次。
    /// SessionManager 订阅它来触发 CloseSession——pane 关闭即子进程退出。</summary>
    event EventHandler? Closed;

    /// <summary>这个 pane 被激活(切到前台/选中对应 tab)时触发。</summary>
    event EventHandler? Activated;

    bool IsOpen { get; }
    void Close();
    void Activate();
}
