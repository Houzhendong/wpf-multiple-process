using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Child;

/// <summary>
/// 子进程窗口(纯视图容器,从原 ChildShell 拆分出来):Win32 摆位部分原样保留——
/// WindowStyle=None、不进任务栏、初始位置屏幕外、SourceInitialized 加
/// WS_EX_TOOLWINDOW(仍然不进 Alt-Tab/任务栏)。顶部框架状态条(标题/心跳文本 +
/// "模拟卡死 10s" 按钮)是框架级调试 affordance,和具体 feature 无关,因此留在这里
/// 而不是下沉到 feature 视图。
///
/// 问题 1 修复(键盘输入):早期方案给子窗口加 WS_EX_NOACTIVATE + 拦截
/// WM_MOUSEACTIVATE→MA_NOACTIVATE,点击不激活、也不打扰 OverlayHost 手动维护的
/// Z 序,代价是子窗口永远拿不到键盘焦点——Windows 的键盘输入路由到"当前激活窗口
/// 所在线程的焦点控件",一个永远不会被激活的窗口里,TextBox/Tab 导航/快捷键全部
/// 失效。这个代价无法接受(子进程里跑的是真实业务 UI,不是纯展示),所以这里整个
/// 去掉 WS_EX_NOACTIVATE 和 WM_MOUSEACTIVATE 钩子,子窗口恢复成完全可激活——
/// 点击子窗口会被系统正常激活、拿到键盘焦点,配合 <see cref="ShowActivated"/> = false
/// 只是让它初次显示时不抢焦点。随之而来的连带影响:
///   1. 点击子窗口会把主窗口标题栏变成非活动状态(系统级激活语义决定的,没有 owner
///      关系就没有"一起高亮"这回事)——这是本设计(无 owner、跨进程嵌入)必须接受
///      的固有代价,不再用 RequestActivate 补偿(那条链路已整条删除,见
///      ChildShell/CommonService/SessionManager)。
///   2. 激活会把窗口提到系统 Z 序同级最前,这会扰乱 OverlayHost 手动维护的层叠
///      顺序——已经由 Host.OverlayZOrderCoordinator 通过"宿主
///      WM_WINDOWPOSCHANGED → 重新钉链条"的既有兜底路径处理,不需要子窗口这边
///      再做什么。
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
        // 不再和宿主建立 owner 关系(见 OverlayHost 说明);子窗口自身只保留
        // WS_EX_TOOLWINDOW(不进 Alt-Tab/任务栏),不再设置 WS_EX_NOACTIVATE——
        // 全部可激活,见类顶部注释。
        Hwnd = new WindowInteropHelper(this).Handle;
        nint exStyle = Win32.GetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE, exStyle | (nint)Win32.WS_EX_TOOLWINDOW);

        SourceReady?.Invoke();
    }
}
