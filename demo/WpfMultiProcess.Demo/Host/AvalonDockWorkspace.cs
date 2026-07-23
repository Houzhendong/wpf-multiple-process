using System.Windows;
using AvalonDock.Layout;
using WpfMultiProcess.Host;

namespace WpfMultiProcess.Demo.Host;

/// <summary>IDockWorkspace 的 AvalonDock 实现:AddPane 建一个 LayoutDocument 塞进
/// DockingManager 唯一的 LayoutDocumentPane。真实项目要换成 Infragistics
/// XamDockManager,只需要另写一个 IDockWorkspace 实现——SessionManager/OverlayHost
/// 都不用改一行。</summary>
public sealed class AvalonDockWorkspace(LayoutDocumentPane docPane) : IDockWorkspace
{
    public IDockPane AddPane(string paneId, string title, FrameworkElement content)
    {
        var document = new LayoutDocument { Title = title, ContentId = paneId, Content = content };
        docPane.Children.Add(document);
        return new AvalonDockPane(document);
    }
}

/// <summary>映射到 AvalonDock.LayoutDocument:Closed/IsActiveChanged 转发成
/// IDockPane.Closed/Activated,Close()/Activate() 转发成 LayoutDocument 同名操作。</summary>
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
    public FrameworkElement Content => (FrameworkElement)_document.Content;
    public bool IsOpen => _document.Parent is not null;

    public event EventHandler? Closed;
    public event EventHandler? Activated;

    public void Close() => _document.Close();
    public void Activate() => _document.IsActive = true;
}
