using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Themes;
using WpfMultiProcess.Host.Session;

namespace WpfMultiProcess.Host;

/// <summary>主窗口:VS 风格 DockingManager,每个已注册 feature 模块一个 LayoutDocument,
/// 内容是 OverlayHost 占位。feature 列表不再硬编码,来自 HostFeatureRegistry —— 新增一个
/// feature 只需要往 registry 里多塞一个模块,这里自动多出一个 tab + 自动拉起对应子进程。</summary>
public sealed class MainWindow : Window
{
    private readonly SessionHub _hub;
    private readonly HostFeatureRegistry _registry;
    private readonly string _socketPath;
    private readonly Dictionary<string, OverlayHost> _hosts = new();
    private readonly Dictionary<string, Process> _children = new();
    private readonly ObservableCollection<string> _log = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 2, 6, 2) };
    private static readonly SolidColorBrush StatusBarNormalBrush = new(Color.FromRgb(0x00, 0x7A, 0xCC));
    private static readonly SolidColorBrush StatusBarUnresponsiveBrush = new(Color.FromRgb(0xC4, 0x2B, 0x1E));
    private Border _statusBar = null!;
    private LayoutDocumentPane _docPane = null!;
    private DockingManager _dockingManager = null!;

    public MainWindow(SessionHub hub, HostFeatureRegistry registry, string socketPath)
    {
        _hub = hub;
        _registry = registry;
        _socketPath = socketPath;

        Title = $"WpfMultiProcess 主进程 (pid {Environment.ProcessId})";
        Width = 1100;
        Height = 700;
        Content = BuildLayout();

        WireHub();
        Loaded += (_, _) => { foreach (var m in _registry.Modules) LaunchChild(m.FeatureId); };
        Closing += (_, _) => ShutdownChildren();

        // 测试钩子:F9 把当前选中(或第一个)feature 的 LayoutDocument 在
        // 浮动/停靠间切换,用于验证 OverlayHost 在 dock↔float 切换后能正确
        // 把子窗口重新 overlay 到新的宿主窗口。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F9) return;
            e.Handled = true;
            ToggleFloatDock();
        };
    }

    private void ToggleFloatDock()
    {
        // 注意:浮动之后原来的 LayoutDocument 会从 _docPane.Children 里移出、
        // 挂到 LayoutRoot 的浮动窗口节点下,所以不能只在 _docPane 里找 ——
        // 要在整棵 Layout 树(Descendents())里找,浮动/停靠状态下都能定位到它。
        var allDocs = _dockingManager.Layout.Descendents().OfType<LayoutDocument>().ToList();
        var doc = _docPane.SelectedContent as LayoutDocument
                  ?? allDocs.FirstOrDefault(d => d.IsActive)
                  ?? allDocs.FirstOrDefault();
        if (doc is null)
        {
            Log("F9: 没有可切换的 LayoutDocument");
            return;
        }

        if (doc.IsFloating)
        {
            Log($"[{doc.ContentId}] F9: Dock() 恢复停靠");
            doc.Dock();
        }
        else
        {
            Log($"[{doc.ContentId}] F9: Float() 浮动");
            doc.Float();
        }
    }

    private UIElement BuildLayout()
    {
        var dock = new DockingManager { Theme = new Vs2013DarkTheme() };
        _dockingManager = dock;
        _docPane = new LayoutDocumentPane();

        var logList = new ListBox { ItemsSource = _log, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        var logAnchorable = new LayoutAnchorable { Title = "事件日志", ContentId = "log", Content = logList };
        var bottomPane = new LayoutAnchorablePane(logAnchorable) { DockHeight = new GridLength(180) };

        dock.Layout = new LayoutRoot
        {
            RootPanel = new LayoutPanel(new LayoutPanel(_docPane) { Orientation = Orientation.Horizontal })
            {
                Orientation = Orientation.Vertical,
                Children = { bottomPane },
            },
        };

        foreach (var m in _registry.Modules)
        {
            var host = new OverlayHost();
            _hosts[m.FeatureId] = host;
            _docPane.Children.Add(new LayoutDocument
            {
                Title = m.Descriptor.Title,
                ContentId = m.FeatureId,
                Content = host,
                CanClose = false,
            });
        }

        _statusBar = new Border
        {
            Background = StatusBarNormalBrush,
            Child = _status,
        };
        _status.Foreground = Brushes.White;
        _status.Text = "等待子进程连接…";

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(dock, 0);
        Grid.SetRow(_statusBar, 1);
        grid.Children.Add(dock);
        grid.Children.Add(_statusBar);
        return grid;
    }

    private void WireHub()
    {
        // SessionHub 事件来自 gRPC 线程池,这里统一调度回 UI 线程
        _hub.SessionConnected += (f, pid) =>
            Dispatcher.BeginInvoke(() => Log($"[{f}] 子进程 pid {pid} 已注册会话"));

        _hub.SessionDisconnected += f =>
            Dispatcher.BeginInvoke(() =>
            {
                Log($"[{f}] 会话断开");
                if (_hosts.TryGetValue(f, out var h)) h.DetachChild();
            });

        _hub.WindowRegistered += (f, hwnd) =>
            Dispatcher.BeginInvoke(() =>
            {
                Log($"[{f}] 收到 HWND 0x{hwnd:X},执行 overlay");
                if (_hosts.TryGetValue(f, out var h)) h.AttachChild(hwnd);
            });

        _hub.ActivateRequested += f =>
            Dispatcher.BeginInvoke(() =>
            {
                // 子窗口现在是 WS_EX_NOACTIVATE,点击不会自己抢激活/提 Z 序,
                // 这里补偿式地把主窗口(宿主)激活一下,换取"点了有反应"的体验。
                // 注:浮动窗格场景下激活的是主窗口而非浮动窗口本身,因为这里
                // 拿到的永远是 MainWindow;可接受,不做进一步区分。
                // 每次点击都会触发,不写事件日志,避免刷屏。
                Activate();
            });

        _hub.FeatureLog += (f, message) =>
            Dispatcher.BeginInvoke(() => Log($"[{f}] {message}"));

        _hub.PongReceived += (f, seq, rtt) =>
            Dispatcher.BeginInvoke(() => _status.Text =
                $"心跳 #{seq} [{f}] UI 线程往返 {rtt:F0} ms    {DateTime.Now:HH:mm:ss}");

        _hub.UiUnresponsive += (f, ms) =>
            Dispatcher.BeginInvoke(() =>
            {
                Log($"[{f}] ⚠ UI 线程无响应 ({ms / 1000.0:F1} 秒无 Pong)");
                _statusBar.Background = StatusBarUnresponsiveBrush;
                _status.Text = $"[{f}] UI 线程无响应 ({ms / 1000.0:F1} 秒无 Pong)    {DateTime.Now:HH:mm:ss}";
            });

        _hub.UiRecovered += f =>
            Dispatcher.BeginInvoke(() =>
            {
                Log($"[{f}] UI 线程已恢复");
                _statusBar.Background = StatusBarNormalBrush;
            });
    }

    private void LaunchChild(string featureId)
    {
        // session_id 由主进程生成、当启动参数传给子进程(不再靠子进程 Register RPC 换取);
        // Prepare 预登记这个 session_id 属于哪个 feature,子进程开自己 feature 的流时
        // SessionHub.TryOpen 据此校验。
        string sessionId = Guid.NewGuid().ToString();
        _hub.Prepare(sessionId, featureId);

        var exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--child");
        psi.ArgumentList.Add($"--feature={featureId}");
        psi.ArgumentList.Add($"--session={sessionId}");
        psi.ArgumentList.Add($"--socket={_socketPath}");
        psi.ArgumentList.Add($"--hostpid={Environment.ProcessId}");

        var proc = Process.Start(psi)!;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            Log($"[{featureId}] 子进程退出 (code {proc.ExitCode})");
            if (_hosts.TryGetValue(featureId, out var h)) h.DetachChild();
        });
        _children[featureId] = proc;
        Log($"[{featureId}] 已启动子进程 pid {proc.Id}");
    }

    private void ShutdownChildren()
    {
        _hub.PushShutdownAll();
        foreach (var proc in _children.Values)
        {
            try
            {
                if (!proc.WaitForExit(1500)) proc.Kill();
            }
            catch { /* 已退出 */ }
        }
    }

    private void Log(string line)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        while (_log.Count > 500) _log.RemoveAt(0);
    }
}
