using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Themes;
using WpfMultiProcess.Host;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Demo.Host;

/// <summary>主窗口:VS 风格 DockingManager,dock pane 完全动态——不再预置固定 tab,
/// 每次 SessionManager.OpenFeature 都经 AvalonDockWorkspace 现建一个 LayoutDocument。
/// 两段式初始化:构造函数只搭 UI 外壳(dock/log/status bar)并暴露
/// <see cref="Workspace"/>,HostProgram 拿它去 new SessionManager,再回调
/// <see cref="AttachSessionManager"/> 接上事件、首次自动打开 waveform/table、挂
/// Closing→CloseAll——这样 SessionManager 需要的 IDockWorkspace 和 DI 容器需要的
/// SessionManager 单例之间不会出现构造顺序死锁。</summary>
public sealed class MainWindow : Window
{
    private readonly ObservableCollection<string> _log = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 2, 6, 2) };
    // 每个会话一行,展示 UiSaturationMeter 最新一次上报;独立于上面的心跳 RTT 状态条,
    // 两条通道互不覆盖。按 session_id 记录,多开同一 feature 时互不覆盖。
    private readonly TextBlock _uiStats = new() { Margin = new Thickness(6, 0, 6, 2), FontSize = 11 };
    private readonly Dictionary<string, string> _uiStatsLines = new();
    private readonly Dictionary<string, bool> _uiStatsHigh = new();
    private readonly Dictionary<string, long> _uiStatsLastLogMs = new();
    private readonly Dictionary<string, int> _sessionIndex = new();
    private static readonly SolidColorBrush StatusBarNormalBrush = new(Color.FromRgb(0x00, 0x7A, 0xCC));
    private static readonly SolidColorBrush StatusBarUnresponsiveBrush = new(Color.FromRgb(0xC4, 0x2B, 0x1E));
    /// <summary>持续高位判定阈值:探针 saturation_pct 超过这个百分比才算"饱和",
    /// 和心跳的 UnresponsiveThresholdMs 是两个独立维度(一个测 UI 有没有空闲容量,
    /// 一个测有没有彻底失联)。</summary>
    private const double HighSaturationPct = 80;
    /// <summary>持续高位期间,事件日志最多每隔这么久重复记一条,避免刷屏。</summary>
    private const long HighSaturationLogIntervalMs = 2000;

    private readonly Border _statusBar;
    private readonly LayoutDocumentPane _docPane;
    private readonly DockingManager _dockingManager;

    private SessionManager? _sessionManager;

    /// <summary>HostProgram 拿这个去 new SessionManager——先于 SessionManager 存在,
    /// 因为 AvalonDock 的 LayoutDocumentPane 在这里的构造函数里就已经建好了。</summary>
    public IDockWorkspace Workspace { get; }

    public MainWindow()
    {
        Title = $"WpfMultiProcess Demo 主进程 (pid {Environment.ProcessId})";
        Width = 1100;
        Height = 700;

        _dockingManager = new DockingManager { Theme = new Vs2013DarkTheme() };
        _docPane = new LayoutDocumentPane();
        Workspace = new AvalonDockWorkspace(_docPane);

        _statusBar = BuildStatusBar();
        Content = BuildLayout();

        // 测试钩子:F9 把当前选中(或第一个)pane 在浮动/停靠间切换,用于验证
        // OverlayHost 在 dock↔float 切换后能正确把子窗口重新 overlay 到新的宿主窗口。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F9) return;
            e.Handled = true;
            ToggleFloatDock();
        };
    }

    /// <summary>HostProgram 在 SessionManager 造好、gRPC service 都挂上、Kestrel 已经
    /// Start() 之后调用:接上事件、打开默认的两个 feature 实例、挂关闭时的 CloseAll。</summary>
    public void AttachSessionManager(SessionManager sessionManager, IReadOnlyList<string> autoOpenFeatureIds)
    {
        _sessionManager = sessionManager;
        WireSessionManager(sessionManager);

        Loaded += (_, _) =>
        {
            foreach (var featureId in autoOpenFeatureIds)
                sessionManager.OpenFeature(featureId);
        };
        Closing += (_, _) => sessionManager.CloseAll();
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
            Log("F9: 没有可切换的 pane");
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

    private Border BuildStatusBar()
    {
        var statusStack = new StackPanel { Orientation = Orientation.Vertical };
        statusStack.Children.Add(_status);
        statusStack.Children.Add(_uiStats);
        _status.Foreground = Brushes.White;
        _status.Text = "等待子进程连接…";
        _uiStats.Foreground = Brushes.White;
        _uiStats.Text = "UI 饱和度:等待上报…";
        return new Border { Background = StatusBarNormalBrush, Child = statusStack };
    }

    private UIElement BuildLayout()
    {
        var logList = new ListBox { ItemsSource = _log, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
        var logAnchorable = new LayoutAnchorable { Title = "事件日志", ContentId = "log", Content = logList };
        var bottomPane = new LayoutAnchorablePane(logAnchorable) { DockHeight = new GridLength(180) };

        _dockingManager.Layout = new LayoutRoot
        {
            RootPanel = new LayoutPanel(new LayoutPanel(_docPane) { Orientation = Orientation.Horizontal })
            {
                Orientation = Orientation.Vertical,
                Children = { bottomPane },
            },
        };

        var newWaveform = new Button { Content = "新建波形", Margin = new Thickness(6, 4, 0, 4), Padding = new Thickness(10, 3, 10, 3) };
        newWaveform.Click += (_, _) => _sessionManager?.OpenFeature("waveform");
        var newTable = new Button { Content = "新建表格", Margin = new Thickness(6, 4, 0, 4), Padding = new Thickness(10, 3, 10, 3) };
        newTable.Click += (_, _) => _sessionManager?.OpenFeature("table");

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(newWaveform);
        toolbar.Children.Add(newTable);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(_dockingManager, 1);
        Grid.SetRow(_statusBar, 2);
        grid.Children.Add(toolbar);
        grid.Children.Add(_dockingManager);
        grid.Children.Add(_statusBar);
        return grid;
    }

    private void WireSessionManager(SessionManager sessionManager)
    {
        // SessionManager 事件来自 gRPC 线程池 / Process.Exited 线程池,这里统一调度回 UI 线程
        sessionManager.SessionOpened += (sessionId, featureId, index) =>
            Dispatcher.BeginInvoke(() =>
            {
                _sessionIndex[sessionId] = index;
                Log($"[{SessionTag(sessionId, featureId)}] 打开新实例");
            });

        sessionManager.SessionClosed += (sessionId, featureId) =>
            Dispatcher.BeginInvoke(() =>
            {
                Log($"[{SessionTag(sessionId, featureId)}] 会话已关闭");
                _sessionIndex.Remove(sessionId);
                _uiStatsLines.Remove(sessionId);
                _uiStatsHigh.Remove(sessionId);
                _uiStatsLastLogMs.Remove(sessionId);
                RefreshUiStatsLine();
            });

        sessionManager.SessionConnected += (sessionId, featureId, pid) =>
            Dispatcher.BeginInvoke(() => Log($"[{SessionTag(sessionId, featureId)}] 子进程 pid {pid} 已注册会话"));

        sessionManager.SessionDisconnected += (sessionId, featureId) =>
            Dispatcher.BeginInvoke(() => Log($"[{SessionTag(sessionId, featureId)}] 会话断开"));

        sessionManager.WindowRegistered += (sessionId, featureId, hwnd) =>
            Dispatcher.BeginInvoke(() => Log($"[{SessionTag(sessionId, featureId)}] 收到 HWND 0x{hwnd:X},执行 overlay"));

        sessionManager.ActivateRequested += (_, _) =>
            Dispatcher.BeginInvoke(() =>
            {
                // 子窗口现在是 WS_EX_NOACTIVATE,点击不会自己抢激活/提 Z 序,
                // 这里补偿式地把主窗口(宿主)激活一下,换取"点了有反应"的体验。
                // 每次点击都会触发,不写事件日志,避免刷屏。
                Activate();
            });

        sessionManager.FeatureLog += (tag, message) =>
            Dispatcher.BeginInvoke(() => Log($"[{tag}] {message}"));

        sessionManager.PongReceived += (sessionId, featureId, seq, rtt) =>
            Dispatcher.BeginInvoke(() => _status.Text =
                $"心跳 #{seq} [{SessionTag(sessionId, featureId)}] UI 线程往返 {rtt:F0} ms    {DateTime.Now:HH:mm:ss}");

        sessionManager.UiUnresponsive += (sessionId, featureId, ms) =>
            Dispatcher.BeginInvoke(() =>
            {
                Log($"[{SessionTag(sessionId, featureId)}] ⚠ UI 线程无响应 ({ms / 1000.0:F1} 秒无 Pong)");
                _statusBar.Background = StatusBarUnresponsiveBrush;
                _status.Text = $"[{SessionTag(sessionId, featureId)}] UI 线程无响应 ({ms / 1000.0:F1} 秒无 Pong)    {DateTime.Now:HH:mm:ss}";
            });

        sessionManager.UiRecovered += (sessionId, featureId) =>
            Dispatcher.BeginInvoke(() =>
            {
                Log($"[{SessionTag(sessionId, featureId)}] UI 线程已恢复");
                _statusBar.Background = StatusBarNormalBrush;
            });

        sessionManager.UiStatsReceived += (sessionId, featureId, stats) =>
            Dispatcher.BeginInvoke(() => OnUiStats(sessionId, featureId, stats));
    }

    private string SessionTag(string sessionId, string featureId) =>
        _sessionIndex.TryGetValue(sessionId, out var idx) ? $"{featureId}#{idx}" : featureId;

    /// <summary>UiSaturationMeter 每窗口(约 1s)上报一次;每个会话单独一行,拼成一条
    /// 文本显示,不覆盖上面的心跳 RTT 状态条。saturation_pct 持续 &gt;80% 时在事件日志
    /// 记一条,只在状态翻转或每 ~2s 记一次,避免刷屏。</summary>
    private void OnUiStats(string sessionId, string featureId, UiStatsRequest stats)
    {
        string tag = SessionTag(sessionId, featureId);
        _uiStatsLines[sessionId] =
            $"[{tag}] UI 饱和 {stats.SaturationPct:F0}% " +
            $"(dispatcher 忙 {stats.DispatcherBusyPct:F0}%, 队列 {stats.MaxQueueLatencyMs:F0}ms, 最长 {stats.LongestOpMs:F0}ms)";
        RefreshUiStatsLine();

        bool isHigh = stats.SaturationPct > HighSaturationPct;
        bool wasHigh = _uiStatsHigh.TryGetValue(sessionId, out var prev) && prev;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long lastLog = _uiStatsLastLogMs.GetValueOrDefault(sessionId);
        if (isHigh && (!wasHigh || now - lastLog > HighSaturationLogIntervalMs))
        {
            Log($"[{tag}] ⚠ UI 饱和度 {stats.SaturationPct:F0}% (持续高位)");
            _uiStatsLastLogMs[sessionId] = now;
        }
        _uiStatsHigh[sessionId] = isHigh;
    }

    private void RefreshUiStatsLine() =>
        _uiStats.Text = _uiStatsLines.Count == 0 ? "UI 饱和度:等待上报…" : string.Join("    ", _uiStatsLines.Values);

    private void Log(string line)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        while (_log.Count > 500) _log.RemoveAt(0);
    }
}
