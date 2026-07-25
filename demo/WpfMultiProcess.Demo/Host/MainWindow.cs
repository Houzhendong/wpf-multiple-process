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
/// 每次开一个 feature 实例都是 MainWindow 自己现建一个 LayoutDocument + AvalonDockPane
/// (见 <see cref="OpenFeatureInstance"/>),再调 SessionManager.OpenFeature 把
/// featureIndex 和造好的 pane 一起传进去——featureIndex 由 <see cref="_nextIndex"/>
/// 按 featureId 各自维护,菜单多开第二个同 feature 实例时自然递增。两段式初始化:
/// 构造函数只搭 UI 外壳(dock/log/status bar),HostProgram 先 new SessionManager
/// (不再需要 MainWindow 这边任何东西),再回调 <see cref="AttachSessionManager"/>
/// 接上事件、首次自动打开 waveform/table、挂 Closing→CloseAll。</summary>
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
    // featureId -> 下一个可用 featureIndex,多开菜单自己维护(SessionManager 不再
    // 分配 index,由调用方决定并传入 OpenFeature)。
    private readonly Dictionary<string, int> _nextIndex = new();
    private static readonly Dictionary<string, string> FeatureTitles = new()
    {
        ["waveform"] = "实时波形",
        ["table"] = "数据表格",
    };
    private static readonly SolidColorBrush StatusBarNormalBrush = new(Color.FromRgb(0x00, 0x7A, 0xCC));
    private static readonly SolidColorBrush StatusBarUnresponsiveBrush = new(Color.FromRgb(0xC4, 0x2B, 0x1E));
    /// <summary>持续高位判定阈值:探针 saturation_pct 超过这个百分比才算"饱和",
    /// 和心跳的 UnresponsiveThresholdMs 是两个独立维度(一个测 UI 有没有空闲容量,
    /// 一个测有没有彻底失联)。</summary>
    private const double HighSaturationPct = 80;
    /// <summary>持续高位期间,事件日志最多每隔这么久重复记一条,避免刷屏。</summary>
    private const long HighSaturationLogIntervalMs = 2000;

    private readonly Border _statusBar;
    // 两个左右并排的 LayoutDocumentPane,验证问题 2(平铺布局下 Z 序收敛):波形和表格
    // 各自固定分到一个 pane,一开就同时可见、同一个宿主根 HWND 下有两个 overlay 同时
    // 显示,是最直接触发"两个 overlay 互抢宿主正上方"这个 bug 的场景。选"按 featureId
    // 固定分栏"而不是"新开的永远进右边"之类更复杂的布局策略,是因为这是验证 Z 序链
    // 协调者最简单、最容易复现/观察的稳定布局,不需要额外 UI 操作。
    private readonly LayoutDocumentPane _docPane;
    private readonly LayoutDocumentPane _docPane2;
    private readonly DockingManager _dockingManager;

    private SessionManager? _sessionManager;

    public MainWindow()
    {
        Title = $"WpfMultiProcess Demo 主进程 (pid {Environment.ProcessId})";
        Width = 1100;
        Height = 700;

        _dockingManager = new DockingManager { Theme = new Vs2013DarkTheme() };
        _docPane = new LayoutDocumentPane();
        _docPane2 = new LayoutDocumentPane();

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
                OpenFeatureInstance(featureId);
        };
        Closing += (_, _) => sessionManager.CloseAll();
    }

    /// <summary>多开一个 feature 新实例:自己维护每个 featureId 的下一个 index、
    /// 建一个新的 LayoutDocument 加进 _docPane、包成 AvalonDockPane,再连同 index/pane
    /// 一起交给 SessionManager.OpenFeature——同一个 featureId 反复调用会递增出
    /// #0、#1、#2……独立的会话。</summary>
    private void OpenFeatureInstance(string featureId)
    {
        if (_sessionManager is null) return;

        int index = _nextIndex.TryGetValue(featureId, out var cur) ? cur + 1 : 0;
        _nextIndex[featureId] = index;

        string title = FeatureTitles.TryGetValue(featureId, out var t) ? t : featureId;
        var document = new LayoutDocument { Title = $"{title} #{index}", ContentId = Guid.NewGuid().ToString() };
        // 平铺布局(见字段注释):table 固定分到右边那个 pane,其余(waveform 等)
        // 都进左边,两个 pane 同时可见时同一宿主根 HWND 下会有两个 overlay 同时显示。
        var targetPane = featureId == "table" ? _docPane2 : _docPane;
        targetPane.Children.Add(document);
        var pane = new AvalonDockPane(document);

        _sessionManager.OpenFeature(featureId, index, pane);
    }

    private void ToggleFloatDock()
    {
        // 注意:浮动之后原来的 LayoutDocument 会从 _docPane.Children 里移出、
        // 挂到 LayoutRoot 的浮动窗口节点下,所以不能只在 _docPane 里找 ——
        // 要在整棵 Layout 树(Descendents())里找,浮动/停靠状态下都能定位到它。
        var allDocs = _dockingManager.Layout.Descendents().OfType<LayoutDocument>().ToList();
        var doc = _docPane.SelectedContent as LayoutDocument
                  ?? _docPane2.SelectedContent as LayoutDocument
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

        // 左右两个 LayoutDocumentPane 水平并排(见字段注释),启动即平铺、两个 pane
        // 同时可见——不需要额外的“强制平铺”按钮或用户操作,复现问题 2 的最简布局。
        // LayoutPanel 没有接受多个子节点的构造函数,只能先建空的再 Add。
        var horizontalPanel = new LayoutPanel { Orientation = Orientation.Horizontal };
        horizontalPanel.Children.Add(_docPane);
        horizontalPanel.Children.Add(_docPane2);

        _dockingManager.Layout = new LayoutRoot
        {
            RootPanel = new LayoutPanel(horizontalPanel)
            {
                Orientation = Orientation.Vertical,
                Children = { bottomPane },
            },
        };

        var newWaveform = new Button { Content = "新建波形", Margin = new Thickness(6, 4, 0, 4), Padding = new Thickness(10, 3, 10, 3) };
        newWaveform.Click += (_, _) => OpenFeatureInstance("waveform");
        var newTable = new Button { Content = "新建表格", Margin = new Thickness(6, 4, 0, 4), Padding = new Thickness(10, 3, 10, 3) };
        newTable.Click += (_, _) => OpenFeatureInstance("table");

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

        // 子进程意外退出:pane 不关闭,SessionManager 已经把 OverlayHost 换成默认错误页
        // (标题+明细+重试按钮),这里只往事件日志记一行,不需要额外处理——默认错误页
        // 就是要展示给用户看的东西,demo 不自定义 FaultContentFactory。
        sessionManager.SessionFaulted += (sessionId, featureId, exitCode) =>
            Dispatcher.BeginInvoke(() => Log(
                $"[{SessionTag(sessionId, featureId)}] ⚠ 子进程意外退出 (exit code {exitCode} / 0x{exitCode:X8}),窗格保留,可点击\"重试\""));

        sessionManager.WindowRegistered += (sessionId, featureId, hwnd) =>
            Dispatcher.BeginInvoke(() => Log($"[{SessionTag(sessionId, featureId)}] 收到 HWND 0x{hwnd:X},执行 overlay"));

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
