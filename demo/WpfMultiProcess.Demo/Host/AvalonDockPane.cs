using System.Windows;
using AvalonDock.Layout;
using WpfMultiProcess.Host;

namespace WpfMultiProcess.Demo.Host;

/// <summary>IDockPane 的 AvalonDock 实现:包一个调用方已经建好、已经 Add 进
/// DockingManager 某个 LayoutDocumentPane 的 LayoutDocument。造 LayoutDocument /
/// 决定挂在哪个 pane 下面,是 MainWindow 自己的职责(见 MainWindow 里"新建波形/
/// 新建表格"按钮的处理逻辑)——这个类只做 IDockPane 接口到 LayoutDocument 具体
/// API 的转发。真实项目要换成 Infragistics XamDockManager,只需要另写一个
/// IDockPane 实现——SessionManager/OverlayHost 都不用改一行。</summary>
internal sealed class AvalonDockPane : IDockPane
{
    private readonly LayoutDocument _document;

    public AvalonDockPane(LayoutDocument document)
    {
        _document = document;
        document.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        document.IsActiveChanged += (_, _) =>
        {
            if (document.IsActive) Activated?.Invoke(this, EventArgs.Empty);
        };
    }

    public string PaneId => _document.ContentId;
    public bool IsOpen => _document.Parent is not null;

    public event EventHandler? Closed;
    public event EventHandler? Activated;

    public void SetContent(FrameworkElement content) => _document.Content = content;
    public void Close() => _document.Close();
    public void Activate() => _document.IsActive = true;
}
