using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Demo.Child.Features.Waveform;

/// <summary>
/// waveform feature 的子进程视图(MVVM 拆分后纯 View,绑定 WaveformViewModel):
/// Polyline 波形渲染(300 点环形缓冲,隐藏(被切走的 tab)时用 Win32.IsWindowVisible
/// 停止重绘,避免拖慢主进程异步 SetWindowPos 位置纠正的处理),加一个"统计"按钮调用
/// ViewModel.ShowStatisticsAsync。曲线数据/流 demux 全部下沉到 WaveformViewModel,这里
/// 只管画。
///
/// "键盘测试"输入框是问题 1(子窗口键盘输入)的验证 affordance:子窗口早先用
/// WS_EX_NOACTIVATE + 拦截 WM_MOUSEACTIVATE 禁止激活时,点这个 TextBox 根本拿不到
/// 焦点,连字符都打不进去;子窗口改为完全可激活后,点击→系统激活→TextBox 拿到键盘
/// 焦点→正常输入,这里能不能正常打字就是最直观的回归验证点。挂
/// AutomationProperties.AutomationId 是为了 UI Automation 测试脚本能定位到它。
/// </summary>
public sealed class WaveformView : UserControl
{
    private const int MaxPoints = 300;

    private readonly WaveformViewModel _vm;
    private readonly Polyline _line = new() { StrokeThickness = 1.5, Stroke = Brushes.DeepSkyBlue };
    private readonly Canvas _canvas = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly TextBlock _statsText = new() { Margin = new Thickness(8, 0, 8, 4), Foreground = Brushes.Gray };

    private bool _needsRedraw;

    public WaveformView(WaveformViewModel vm)
    {
        _vm = vm;
        DataContext = vm;

        _canvas.Children.Add(_line);
        _canvas.SizeChanged += (_, _) => RedrawLine();

        var statsButton = new Button
        {
            Content = "统计",
            Margin = new Thickness(8, 4, 4, 4),
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        statsButton.Click += async (_, _) => await _vm.ShowStatisticsAsync();
        _statsText.SetBinding(TextBlock.TextProperty, new Binding(nameof(WaveformViewModel.StatsText)));

        var keyboardTestLabel = new TextBlock
        {
            Text = "键盘测试:",
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
        };
        var keyboardTestBox = new TextBox
        {
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 8, 4),
        };
        AutomationProperties.SetAutomationId(keyboardTestBox, "WaveformKeyboardTestBox");
        var keyboardTestPanel = new StackPanel { Orientation = Orientation.Horizontal };
        keyboardTestPanel.Children.Add(keyboardTestLabel);
        keyboardTestPanel.Children.Add(keyboardTestBox);

        var footer = new DockPanel();
        DockPanel.SetDock(statsButton, Dock.Left);
        DockPanel.SetDock(keyboardTestPanel, Dock.Right);
        footer.Children.Add(statsButton);
        footer.Children.Add(keyboardTestPanel);
        footer.Children.Add(_statsText);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_canvas);
        Content = root;

        vm.FrameReceived += OnFrameReceived;

        // Win32 级隐藏(SWP_HIDEWINDOW)通常会同步反映到 WPF 的窗口 IsVisible,但隐藏
        // 期间被跳过的重绘不能指望"下一次可见"一定先经过 OnFrameReceived —— 保底在宿主
        // 窗口的 IsVisibleChanged 上再补一次检查。
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window w)
                w.IsVisibleChanged += (_, _) => TryRedrawIfPending();
        };
        Unloaded += (_, _) => vm.Dispose();
    }

    private void OnFrameReceived()
    {
        if (!Win32.IsWindowVisible(_vm.Hwnd))
        {
            _needsRedraw = true;
            return;
        }
        RedrawLine();
        _needsRedraw = false;
    }

    private void TryRedrawIfPending()
    {
        if (_needsRedraw && Win32.IsWindowVisible(_vm.Hwnd))
        {
            RedrawLine();
            _needsRedraw = false;
        }
    }

    private void RedrawLine()
    {
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        var values = _vm.Values;
        if (w <= 0 || h <= 0 || values.Count < 2) return;

        double min = values.Min(), max = values.Max();
        double span = Math.Max(max - min, 1e-9);
        var points = new PointCollection(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            double x = i * w / (MaxPoints - 1);
            double y = h - (values[i] - min) / span * (h - 8) - 4;
            points.Add(new Point(x, y));
        }
        _line.Points = points;
    }
}
