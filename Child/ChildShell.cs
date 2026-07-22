using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Grpc.Net.Client;
using WpfMultiProcess.Ipc;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Child;

/// <summary>
/// 子进程窗口(演进自原 ChildWindow):Win32 摆位/激活拦截部分原样保留——
/// WindowStyle=None、不进任务栏、初始位置屏幕外、SourceInitialized 加
/// WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW、拦 WM_MOUSEACTIVATE 返回 MA_NOACTIVATE、
/// 点击 fire-and-forget 上报 RequestActivate、Closed 释放资源。
///
/// 子进程不再有独立的 ChildSession 对象:session_id 由主进程生成、随 --session
/// 启动参数传入(见 Program.CmdLine),ChildShell 直接持有 gRPC 通道 + session_id +
/// 自己的 hwnd,并在同一个 channel 上额外建一个 CommonService 客户端——Pong/
/// RequestActivate 这两个 feature-无关的 unary 就用它,是普通的 fire-and-forget
/// unary 调用,不是往 bidi stream 里写,没有并发写同一条 stream 的坑。真正的数据流
/// 由 feature 视图自己开(见 IFeatureChildModule.CreateView(ChildContext)),demux
/// 出来的 Control 转发回这里的 OnPing/RequestClose,ApplyReply 用 Register 流第一条
/// Reply 设标题/主题色。顶部框架状态条(标题/心跳文本 + "模拟卡死 10s" 按钮)是框架级
/// 调试 affordance,与具体 feature 无关,因此留在这里而不是下沉到 feature 视图。
/// </summary>
public sealed class ChildShell : Window
{
    private readonly CmdLine _opts;
    private readonly ChildFeatureRegistry _registry;
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

    private GrpcChannel? _channel;
    private CommonService.CommonServiceClient? _common;

    /// <summary>SourceInitialized 里拿到的自己窗口句柄,随开流请求带给主进程(不再单独
    /// 有一次 RegisterWindow RPC)。</summary>
    public nint Hwnd { get; private set; }

    public string SessionId => _opts.SessionId;

    public ChildShell(CmdLine opts, ChildFeatureRegistry registry)
    {
        _opts = opts;
        _registry = registry;

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
        Closed += (_, _) => _channel?.Dispose();

        // 点击子窗口本身不应该抢激活(WS_EX_NOACTIVATE 拦不到的场景兜底见下),
        // 但主进程侧仍希望"我被点了"能把宿主提到前面,所以点击时 fire-and-forget
        // 上报一次 RequestActivate,不阻塞输入本身。
        PreviewMouseDown += (_, _) => RequestActivate();
    }

    /// <summary>子窗口是 WS_EX_NOACTIVATE,点击不会自己抢激活,fire-and-forget 上报一次
    /// 换取主进程 Activate() 补偿。普通 unary 调用,和 Pong 共用同一个 CommonService
    /// 客户端、同一个 channel,没有 bidi 写并发的坑。</summary>
    public void RequestActivate()
    {
        if (_common is null) return;
        _ = RequestActivateCoreAsync(_common);
    }

    private async Task RequestActivateCoreAsync(CommonService.CommonServiceClient common)
    {
        try { await common.RequestActivateAsync(new ActivateRequest { SessionId = SessionId }).ResponseAsync; }
        catch { /* 主进程不可达时忽略,不影响窗口自身交互 */ }
    }

    /// <summary>feature 视图从自己 stream 里 demux 出 Control.Ping 后调用:在 UI 线程
    /// 更新心跳状态条,并回一个 Pong unary——回 Pong 发生在 UI 线程,证明 UI 未卡死。</summary>
    public void OnPing(Ping ping)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _heartbeat.Text = $"心跳 #{ping.Seq}  {DateTime.Now:HH:mm:ss}";
            if (_common is null) return;
            _ = _common.PongAsync(new PongRequest
            {
                SessionId = SessionId,
                Seq = ping.Seq,
                PingTimestampMs = ping.TimestampMs,
                PongTimestampMs = NowMs(),
            }).ResponseAsync;
        });
    }

    /// <summary>feature 视图从自己 stream 里 demux 出 Control.Shutdown 后调用。</summary>
    public void RequestClose() => Dispatcher.BeginInvoke(Close);

    /// <summary>feature 视图收到 Register 流第一条 Reply 后调用,设标题/主题色。</summary>
    public void ApplyReply(RegisterReply reply)
    {
        _title.Text = reply.Title;
        var accent = (Color)ColorConverter.ConvertFromString(reply.AccentColor);
        _title.Foreground = new SolidColorBrush(accent);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
        try
        {
            // 0) 不再和宿主建立 owner 关系(见 OverlayHost 说明),子窗口自身改为不可
            // 激活的工具窗:WS_EX_NOACTIVATE 让点击不抢激活焦点、不扰乱 OverlayHost
            // 手动维护的 Z 序;WS_EX_TOOLWINDOW 保持不进 Alt-Tab/任务栏。
            Hwnd = new WindowInteropHelper(this).Handle;
            nint exStyle = Win32.GetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE,
                exStyle | (nint)(Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));

            // WS_EX_NOACTIVATE 本身已经能挡掉大部分激活,这里再挂一层 WM_MOUSEACTIVATE
            // 钩子保底:点击仍然正常派发鼠标消息(按钮能点),只是不激活窗口。
            HwndSource.FromHwnd(Hwnd)?.AddHook(OnWndProc);

            // 1) 建 UDS 通道,并在同一个 channel 上额外建一个 CommonService 客户端
            // (Pong/RequestActivate 这两个 feature-无关 unary 用)。
            _channel = GrpcUds.CreateChannel(_opts.SocketPath);
            _common = new CommonService.CommonServiceClient(_channel);

            // 2) 把 ChildContext 交给 feature 模块生成视图,塞进中间区域;该 feature
            // 自己的 gRPC stream(Register 带 session_id/hwnd/pid,返回带 Reply/Control
            // oneof 的强类型 stream)由视图自己开、自己 demux,连不上时视图自己关窗口。
            var ctx = new ChildContext(_channel, _opts.SessionId, this);
            var module = _registry.Get(_opts.FeatureId);
            _viewHost.Content = module.CreateView(ctx);
        }
        catch
        {
            Close(); // 连不上主进程,直接退出
        }
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
