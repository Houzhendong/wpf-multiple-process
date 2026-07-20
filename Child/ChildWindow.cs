using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Child;

/// <summary>
/// 子进程窗口:无边框、不进任务栏。SourceInitialized 后把 HWND 上报给主进程,
/// 由主进程 SetOwner 并 overlay 到 dock pane 的占位区域。
/// </summary>
public sealed class ChildWindow : Window
{
    private const int MaxPoints = 300;

    private readonly ChildIpcClient _ipc;
    private readonly Polyline _line = new() { StrokeThickness = 1.5 };
    private readonly Canvas _canvas = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly TextBlock _title = new() { Margin = new Thickness(8, 4, 8, 0), FontWeight = FontWeights.Bold };
    private readonly TextBlock _latest = new() { Margin = new Thickness(8, 0, 8, 2), Foreground = Brushes.Gray };
    private readonly TextBlock _heartbeat = new() { Margin = new Thickness(8, 0, 8, 4), Foreground = Brushes.DarkGray };
    private readonly List<double> _values = new();

    private nint _hwndSelf;
    // 隐藏(被切走的 tab)期间收到的数据帧跳过重绘,标记补一次画;避免
    // 每 50ms 一次的全量 Polyline 重建占满 UI 线程、拖慢主进程异步 post
    // 过来的 SetWindowPos 位置纠正请求的处理(见 OverlayHost 的脏检查注释)。
    private bool _needsRedraw;

    public ChildWindow(CmdLine opts)
    {
        _ipc = new ChildIpcClient(opts.FeatureId, opts.SocketPath);

        // 无边框工具窗:由主进程摆位,自己不参与任务栏/Alt-Tab
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Left = -32000; // 上报 HWND 前藏在屏幕外,避免闪烁
        Top = -32000;
        Width = 200;
        Height = 150;
        Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26));
        Content = BuildLayout(opts.FeatureId);

        SourceInitialized += async (_, _) => await OnSourceInitializedAsync();
        Closed += async (_, _) => await _ipc.DisposeAsync();

        // 点击子窗口本身不应该抢激活(WS_EX_NOACTIVATE 拦不到的场景兜底见下),
        // 但主进程侧仍希望"我被点了"能把宿主提到前面,所以点击时 fire-and-forget
        // 上报一次 focus_request,不阻塞输入本身。
        PreviewMouseDown += (_, _) => _ = ReportFocusRequestAsync();

        // Win32 级隐藏(SWP_HIDEWINDOW)通常会同步反映到 WPF 的 IsVisible,
        // 但隐藏期间被跳过的重绘不能指望"下一次可见"一定先经过这里 —— 保底
        // 用 IsWindowVisible 直接问一遍再决定是否需要补画。
        IsVisibleChanged += (_, _) => TryRedrawIfPending();
    }

    private void TryRedrawIfPending()
    {
        if (_needsRedraw && Win32.IsWindowVisible(_hwndSelf))
        {
            RedrawLine();
            _needsRedraw = false;
        }
    }

    private async Task ReportFocusRequestAsync()
    {
        try { await _ipc.ReportInteractionAsync("focus_request", "{}"); }
        catch { /* 主进程不可达时忽略,不影响窗口自身交互 */ }
    }

    private UIElement BuildLayout(string featureId)
    {
        _title.Text = featureId;
        _title.Foreground = Brushes.White;

        _canvas.Children.Add(_line);
        _canvas.SizeChanged += (_, _) => RedrawLine();

        var button = new Button
        {
            Content = "上报交互",
            Margin = new Thickness(8, 4, 4, 8),
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        int clicks = 0;
        button.Click += async (_, _) =>
        {
            clicks++;
            // unary 上传交互结果
            await _ipc.ReportInteractionAsync("button_click", $"{{\"count\":{clicks}}}");
        };

        var hangButton = new Button
        {
            Content = "模拟卡死 10s",
            Margin = new Thickness(4, 4, 8, 8),
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        hangButton.Click += async (_, _) => await SimulateHangAsync();

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal };
        buttonRow.Children.Add(button);
        buttonRow.Children.Add(hangButton);

        var panel = new DockPanel();
        var header = new StackPanel();
        header.Children.Add(_title);
        header.Children.Add(_latest);
        header.Children.Add(_heartbeat);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttonRow, Dock.Bottom);
        panel.Children.Add(header);
        panel.Children.Add(buttonRow);
        panel.Children.Add(_canvas);
        return panel;
    }

    /// <summary>
    /// 先上报交互(让主进程日志先记下这次模拟),再直接 Thread.Sleep 阻塞 UI 线程 10 秒。
    /// 阻塞期间 stream 后台线程仍在收 Ping,但 Dispatcher.BeginInvoke 排的 Pong 发不出去,
    /// 主进程侧就能观察到心跳超时。10 秒后自然恢复,无需额外动作。
    /// </summary>
    private async Task SimulateHangAsync()
    {
        await _ipc.ReportInteractionAsync("simulate_hang", "{\"seconds\":10}");
        Thread.Sleep(TimeSpan.FromSeconds(10));
    }

    private async Task OnSourceInitializedAsync()
    {
        try
        {
            // 0) 不再和宿主建立 owner 关系(见 OverlayHost 顶部说明),子窗口自身
            // 改为不可激活的工具窗:WS_EX_NOACTIVATE 让点击不抢激活焦点、不扰乱
            // OverlayHost 手动维护的 Z 序;WS_EX_TOOLWINDOW 保持不进 Alt-Tab/任务栏。
            _hwndSelf = new WindowInteropHelper(this).Handle;
            nint exStyle = Win32.GetWindowLongPtr(_hwndSelf, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(_hwndSelf, Win32.GWL_EXSTYLE,
                exStyle | (nint)(Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));

            // WS_EX_NOACTIVATE 本身已经能挡掉大部分激活,这里再挂一层 WM_MOUSEACTIVATE
            // 钩子保底:点击仍然正常派发鼠标消息(按钮能点),只是不激活窗口。
            HwndSource.FromHwnd(_hwndSelf)?.AddHook(OnWndProc);

            // 1) unary 获取初始化参数
            var init = await _ipc.GetInitParamsAsync();
            _title.Text = init.Title;
            var accent = (Color)ColorConverter.ConvertFromString(init.AccentColor);
            _line.Stroke = new SolidColorBrush(accent);
            _title.Foreground = new SolidColorBrush(accent);

            // 2) 订阅 server stream(数据 + 心跳 + 关闭)
            _ipc.FeatureReceived += data => Dispatcher.BeginInvoke(() => OnFeature(data));
            _ipc.PingReceived += ping => Dispatcher.BeginInvoke(() =>
            {
                // 关键:回 Pong 发生在 UI 线程,证明 UI 未卡死
                _heartbeat.Text = $"心跳 #{ping.Sequence}  {DateTime.Now:HH:mm:ss}";
                _ipc.SendPong(ping);
            });
            _ipc.ShutdownRequested += () => Dispatcher.BeginInvoke(Close);
            _ipc.StreamFaulted += _ => Dispatcher.BeginInvoke(Close); // 主进程没了就退出
            _ipc.StartSubscription();

            // 3) 上报 HWND,主进程据此手动 overlay(不再 SetOwner,见 OverlayHost 说明)
            await _ipc.RegisterWindowAsync(_hwndSelf);
        }
        catch
        {
            Close(); // 连不上主进程,直接退出
        }
    }

    /// <summary>
    /// 拦 WM_MOUSEACTIVATE 返回 MA_NOACTIVATE:鼠标消息照常派发(按钮能点),
    /// 但不激活本窗口 —— 配合 WS_EX_NOACTIVATE 双保险,避免点击子窗口时把它
    /// 激活到系统 Z 序最前、扰乱 OverlayHost 手动维护的层叠顺序。
    /// </summary>
    private nint OnWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32.WM_MOUSEACTIVATE)
        {
            handled = true;
            return Win32.MA_NOACTIVATE;
        }
        return 0;
    }

    private void OnFeature(FeatureData data)
    {
        _latest.Text = data.Text;
        _values.Add(data.Value);
        if (_values.Count > MaxPoints) _values.RemoveAt(0);

        // 数据照常入队(环形截断),但被切走的 tab 不可见时跳过重绘 —— 见字段
        // 注释。用 Win32 IsWindowVisible 而不是 WPF IsVisible,避免依赖两套
        // 可见性状态同步的时序假设。
        if (!Win32.IsWindowVisible(_hwndSelf))
        {
            _needsRedraw = true;
            return;
        }
        RedrawLine();
        _needsRedraw = false;
    }

    private void RedrawLine()
    {
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        if (w <= 0 || h <= 0 || _values.Count < 2) return;

        double min = _values.Min(), max = _values.Max();
        double span = Math.Max(max - min, 1e-9);
        var points = new PointCollection(_values.Count);
        for (int i = 0; i < _values.Count; i++)
        {
            double x = i * w / (MaxPoints - 1);
            double y = h - (_values[i] - min) / span * (h - 8) - 4;
            points.Add(new Point(x, y));
        }
        _line.Points = points;
    }
}
