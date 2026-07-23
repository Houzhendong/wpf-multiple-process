using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Child;

/// <summary>
/// 子进程窗口(纯视图容器,从原 ChildShell 拆分出来):Win32 摆位/激活拦截部分原样
/// 保留——WindowStyle=None、不进任务栏、初始位置屏幕外、SourceInitialized 加
/// WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW、拦 WM_MOUSEACTIVATE 返回 MA_NOACTIVATE、
/// 点击触发 <see cref="ActivateRequested"/>(由 ChildShell 订阅,fire-and-forget 上报
/// 换取主进程 Activate() 补偿)。顶部框架状态条(标题/心跳文本 + "模拟卡死 10s" 按钮)
/// 是框架级调试 affordance,和具体 feature 无关,因此留在这里而不是下沉到 feature 视图。
///
/// 真正的会话编排(channel/session_id/心跳协议/UiSaturationMeter)全部下沉到
/// ChildShell,这里只管"窗口长什么样、Win32 怎么摆位、feature 视图往哪塞"——feature
/// 视图隐藏时停止重绘("hide-stops-render")由各 feature 视图自己通过
/// Win32.IsWindowVisible(Hwnd) 判断,不在这个类里。
/// </summary>
public sealed class ChildWindow : Window
{
    private readonly TextBlock _title = new()
    {
        Margin = new Thickness(8, 4, 8, 4), FontWeight = FontWeights.Bold, Foreground = Brushes.White,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _heartbeat = new()
    {
        Margin = new Thickness(0, 4, 8, 4), Foreground = Brushes.DarkGray, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly ContentControl _viewHost = new();

    /// <summary>SourceInitialized 里拿到的自己窗口句柄,随开流请求带给主进程。</summary>
    public nint Hwnd { get; private set; }

    /// <summary>Hwnd 已经落地、Win32 摆位已经做完时触发一次——bootstrap 在这里才能开始
    /// 建 channel/ChildShell(需要 Hwnd 随开流请求带给主进程)。</summary>
    public event Action? SourceReady;

    /// <summary>子窗口是 WS_EX_NOACTIVATE,点击不会自己抢激活;这里把"被点了"这件事
    /// 转发出去,ChildShell 订阅后 fire-and-forget 上报 RequestActivate。</summary>
    public event Action? ActivateRequested;

    public ChildWindow()
    {
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
        Content = BuildLayout();

        SourceInitialized += (_, _) => OnSourceInitialized();

        // 点击子窗口本身不应该抢激活(WS_EX_NOACTIVATE 拦不到的场景兜底见下),
        // 但主进程侧仍希望"我被点了"能把宿主提到前面,所以点击时转发一次事件。
        PreviewMouseDown += (_, _) => ActivateRequested?.Invoke();
    }

    /// <summary>bootstrap 建好 feature 视图后调用,塞进中间区域。</summary>
    public void SetContent(FrameworkElement view) => _viewHost.Content = view;

    /// <summary>ChildShell.SendPong 在 UI 线程调用,刷新心跳状态条文本。</summary>
    public void SetHeartbeatText(string text) => _heartbeat.Text = text;

    /// <summary>ChildShell 收到 Register 流第一条 Reply 后调用(已在 UI 线程),设标题/主题色。</summary>
    public void ApplyReplyMeta(string title, string accentColorHex)
    {
        _title.Text = title;
        var accent = (Color)ColorConverter.ConvertFromString(accentColorHex);
        _title.Foreground = new SolidColorBrush(accent);
    }

    private UIElement BuildLayout()
    {
        var hangButton = new Button
        {
            Content = "模拟卡死 10s",
            Margin = new Thickness(4, 4, 8, 4),
            Padding = new Thickness(10, 3, 10, 3),
        };
        hangButton.Click += (_, _) => SimulateHang();

        var texts = new StackPanel { Orientation = Orientation.Horizontal };
        texts.Children.Add(_title);
        texts.Children.Add(_heartbeat);

        var statusBar = new DockPanel
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
            LastChildFill = false,
        };
        DockPanel.SetDock(hangButton, Dock.Right);
        statusBar.Children.Add(hangButton);
        statusBar.Children.Add(texts);

        var root = new DockPanel();
        DockPanel.SetDock(statusBar, Dock.Top);
        root.Children.Add(statusBar);
        root.Children.Add(_viewHost);
        return root;
    }

    /// <summary>
    /// 框架级调试钩子,适用任何 feature:直接 Thread.Sleep 阻塞 UI 线程 10 秒。
    /// 阻塞期间 stream 后台线程仍在收 Ping,但 Dispatcher.BeginInvoke 排的 Pong 发不出去,
    /// 主进程侧就能观察到心跳超时;10 秒后自然恢复,无需额外动作。
    /// </summary>
    private void SimulateHang() => Thread.Sleep(TimeSpan.FromSeconds(10));

    private void OnSourceInitialized()
    {
        // 不再和宿主建立 owner 关系(见 OverlayHost 说明),子窗口自身改为不可激活的
        // 工具窗:WS_EX_NOACTIVATE 让点击不抢激活焦点、不扰乱 OverlayHost 手动维护的
        // Z 序;WS_EX_TOOLWINDOW 保持不进 Alt-Tab/任务栏。
        Hwnd = new WindowInteropHelper(this).Handle;
        nint exStyle = Win32.GetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE,
            exStyle | (nint)(Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));

        // WS_EX_NOACTIVATE 本身已经能挡掉大部分激活,这里再挂一层 WM_MOUSEACTIVATE
        // 钩子保底:点击仍然正常派发鼠标消息(按钮能点),只是不激活窗口。
        HwndSource.FromHwnd(Hwnd)?.AddHook(OnWndProc);

        SourceReady?.Invoke();
    }

    /// <summary>
    /// 拦 WM_MOUSEACTIVATE 返回 MA_NOACTIVATE:鼠标消息照常派发(按钮能点),
    /// 但不激活本窗口——配合 WS_EX_NOACTIVATE 双保险,避免点击子窗口时把它
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
}
