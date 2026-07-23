using WpfMultiProcess.Child;
using WpfMultiProcess.Ipc.Waveform;

namespace WpfMultiProcess.Demo.Child.Features.Waveform;

/// <summary>
/// waveform feature 的子进程视图模型:持有最近 300 点的曲线数据(环形缓冲)+ "统计"
/// 命令(调用 WaveformService.GetStatistics)。<see cref="Dispatch"/> 做 oneof 三路分派——
/// Reply/Control 两路直接落到基类(自动处理好 UI 线程编排),数据帧一路自己
/// Dispatcher.BeginInvoke 再调 OnData,因为只有这里知道 Frame 要落到哪个绑定属性上。
///
/// 曲线重绘走 <see cref="FrameReceived"/> 事件而不是 ObservableCollection+转换器:
/// 300 点环形缓冲的高频重绘(50ms 一帧)更适合命令式的 Polyline.Points 整体替换,和原
/// WaveformView 保持同样的重绘策略,由 View 自己订阅 FrameReceived 决定何时/是否重绘
/// (隐藏时用 Win32.IsWindowVisible 跳过重绘)。
/// </summary>
public sealed class WaveformViewModel : FeatureViewModel<WaveformDown>
{
    private const int MaxPoints = 300;

    private readonly WaveformService.WaveformServiceClient _client;
    private readonly List<double> _values = new();
    private string _statsText = "";

    public WaveformViewModel(ChildShell shell, WaveformService.WaveformServiceClient client,
        Grpc.Core.AsyncServerStreamingCall<WaveformDown> call) : base(shell, call)
    {
        _client = client;
    }

    /// <summary>最近一帧数据落地时触发(已在 UI 线程),View 订阅后决定是否重绘。</summary>
    public event Action? FrameReceived;

    public IReadOnlyList<double> Values => _values;

    public string StatsText
    {
        get => _statsText;
        private set { _statsText = value; OnPropertyChanged(nameof(StatsText)); }
    }

    public async Task ShowStatisticsAsync()
    {
        try
        {
            var reply = await _client.GetStatisticsAsync(
                new StatsRequest { SessionId = Shell.SessionId }, deadline: DateTime.UtcNow.AddSeconds(5));
            StatsText = $"min={reply.Min:F3} max={reply.Max:F3} avg={reply.Avg:F3} count={reply.Count}";
        }
        catch (Exception ex)
        {
            StatsText = $"统计失败: {ex.Message}";
        }
    }

    protected override void Dispatch(WaveformDown env)
    {
        switch (env.KindCase)
        {
            case WaveformDown.KindOneofCase.Reply:
                OnReply(env.Reply);
                break;
            case WaveformDown.KindOneofCase.Ping:
                HandlePing(env.Ping);
                break;
            case WaveformDown.KindOneofCase.Shutdown:
                HandleShutdown(env.Shutdown);
                break;
            case WaveformDown.KindOneofCase.Frame:
                Shell.Window.Dispatcher.BeginInvoke(() => OnData(env));
                break;
        }
    }

    protected override void OnData(WaveformDown data)
    {
        _values.Add(data.Frame.Value);
        if (_values.Count > MaxPoints) _values.RemoveAt(0);
        FrameReceived?.Invoke();
    }
}
