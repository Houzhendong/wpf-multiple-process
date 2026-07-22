using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Grpc.Core;
using WpfMultiProcess.Ipc;
using WpfMultiProcess.Ipc.Waveform;
using Control = WpfMultiProcess.Ipc.Common.Control;

namespace WpfMultiProcess.Child.Features.Waveform;

/// <summary>
/// waveform feature 的子进程视图:Polyline 波形渲染(300 点环形缓冲,隐藏(被切走的 tab)
/// 时用 Win32.IsWindowVisible 停止重绘,避免拖慢主进程异步 SetWindowPos 位置纠正的处理),
/// 加一个"统计"按钮调用 WaveformService.GetStatistics。
///
/// 会话建立(带 hwnd/pid)并进这次 Register 开流请求,不再单独有一次 RegisterWindow RPC。
/// 流 demux(unwrap 接缝的落点):down.Reply → ctx.Shell.ApplyReply;
/// down.Control(Ping/Shutdown) → ctx.Shell.OnPing / ctx.Shell.RequestClose;
/// down.Frame → 更新曲线。
/// </summary>
public sealed class WaveformView : DockPanel
{
    private const int MaxPoints = 300;

    private readonly ChildContext _ctx;
    private readonly WaveformService.WaveformServiceClient _client;
    private readonly Polyline _line = new() { StrokeThickness = 1.5, Stroke = Brushes.DeepSkyBlue };
    private readonly Canvas _canvas = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly TextBlock _statsText = new() { Margin = new Thickness(8, 0, 8, 4), Foreground = Brushes.Gray };
    private readonly List<double> _values = new();
    private readonly CancellationTokenSource _cts = new();

    private bool _needsRedraw;

    public WaveformView(ChildContext ctx)
    {
        _ctx = ctx;
        _client = new WaveformService.WaveformServiceClient(ctx.Channel);

        _canvas.Children.Add(_line);
        _canvas.SizeChanged += (_, _) => RedrawLine();

        var statsButton = new Button
        {
            Content = "统计",
            Margin = new Thickness(8, 4, 4, 4),
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        statsButton.Click += async (_, _) => await ShowStatisticsAsync();

        var footer = new DockPanel();
        DockPanel.SetDock(statsButton, Dock.Left);
        footer.Children.Add(statsButton);
        footer.Children.Add(_statsText);

        DockPanel.SetDock(footer, Dock.Bottom);
        Children.Add(footer);
        Children.Add(_canvas);

        // Win32 级隐藏(SWP_HIDEWINDOW)通常会同步反映到 WPF 的窗口 IsVisible,但隐藏
        // 期间被跳过的重绘不能指望"下一次可见"一定先经过 OnFrame —— 保底在宿主窗口的
        // IsVisibleChanged 上再补一次检查。
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window w)
                w.IsVisibleChanged += (_, _) => TryRedrawIfPending();
        };
        Unloaded += (_, _) => _cts.Cancel();

        _ = StreamAsync(_cts.Token);
    }

    private void TryRedrawIfPending()
    {
        if (_needsRedraw && Win32.IsWindowVisible(_ctx.Shell.Hwnd))
        {
            RedrawLine();
            _needsRedraw = false;
        }
    }

    private async Task ShowStatisticsAsync()
    {
        try
        {
            var reply = await _client.GetStatisticsAsync(
                new StatsRequest { SessionId = _ctx.SessionId }, deadline: DateTime.UtcNow.AddSeconds(5));
            _statsText.Text = $"min={reply.Min:F3} max={reply.Max:F3} avg={reply.Avg:F3} count={reply.Count}";
        }
        catch (Exception ex)
        {
            _statsText.Text = $"统计失败: {ex.Message}";
        }
    }

    private async Task StreamAsync(CancellationToken ct)
    {
        try
        {
            using var call = _client.Register(new StreamRequest
            {
                SessionId = _ctx.SessionId,
                Hwnd = _ctx.Shell.Hwnd,
                Pid = Environment.ProcessId,
            }, cancellationToken: ct);
            await foreach (var down in call.ResponseStream.ReadAllAsync(ct))
            {
                switch (down.KindCase)
                {
                    case WaveformDown.KindOneofCase.Reply:
                        _ = Dispatcher.BeginInvoke(() => _ctx.Shell.ApplyReply(down.Reply));
                        break;
                    case WaveformDown.KindOneofCase.Control:
                        OnControl(down.Control);
                        break;
                    case WaveformDown.KindOneofCase.Frame:
                        var frame = down.Frame;
                        _ = Dispatcher.BeginInvoke(() => OnFrame(frame));
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // 主进程不可达(stream 断开):关掉整个子窗口,双保险之一见 ChildProgram 的孤儿自杀。
            _ = Dispatcher.BeginInvoke(() => Window.GetWindow(this)?.Close());
        }
    }

    private void OnControl(Control control)
    {
        switch (control.KindCase)
        {
            case Control.KindOneofCase.Ping:
                _ctx.Shell.OnPing(control.Ping);
                break;
            case Control.KindOneofCase.Shutdown:
                _ctx.Shell.RequestClose();
                break;
        }
    }

    private void OnFrame(WaveformFrame frame)
    {
        _values.Add(frame.Value);
        if (_values.Count > MaxPoints) _values.RemoveAt(0);

        if (!Win32.IsWindowVisible(_ctx.Shell.Hwnd))
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
