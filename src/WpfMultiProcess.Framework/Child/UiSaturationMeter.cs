using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WpfMultiProcess.Ipc.Common;

namespace WpfMultiProcess.Child;

/// <summary>
/// 框架级、feature-无关:子进程 UI 线程"饱和度"遥测(挂在 ChildShell 上,和具体
/// feature 视图无关)。这里的"饱和度"不是 CPU 占用率,而是 UI 线程还有没有空闲
/// 容量及时执行低优先级回调——分两部分:
///
/// A) 探针(权威值,后台线程 <see cref="ProbeLoop"/>):专用后台线程用 Stopwatch
///    自驱动一个约 15ms 步长的循环,维持"恰好一个"未决的
///    <c>Dispatcher.BeginInvoke(Background, ...)</c> 探针——post 时刻记下来,只要探针
///    还没被执行,过了 grace(~8ms,滤掉正常的排队抖动)之后每一步都把这一步的墙钟
///    切片计入 <c>_busyMs</c>;探针一旦被执行(说明 UI 线程那一刻有空)立刻重新 post
///    下一个,维持"任意时刻至多一个在飞"。UI 线程只是比较忙(排了很多更高优先级的活)
///    时,Background 级探针迟迟排不上,<c>_busyMs</c> 跟着涨;UI 线程彻底卡死(比如
///    Thread.Sleep)时,探针永远不会被执行,整段卡死墙钟原样计入——且这个累加动作是
///    后台线程自己做的,完全不依赖 UI 线程,所以卡死"进行中"就能报出接近 100% 的饱和度,
///    不需要等卡死结束。每满一个窗口 W(1s)按 busy/窗口墙钟 算一次百分比上报,随后清零。
///
/// B) hooks(归因,<see cref="Start"/> 里在 UI 线程注册 <c>Dispatcher.Hooks</c>,后台线程
///    每窗口读取快照):统计 dispatcher 自己认为忙了多久(dispatcherBusyPct)、最长排队
///    延迟(maxQueueLatencyMs,Posted→Started 间隔的窗口内最大值)、最长单次操作
///    (longestOpMs)、操作数(opCount)。这套数据能定性"忙在哪个操作上",但如果 UI 线程
///    卡在一次 DispatcherOperation 内部(比如 Thread.Sleep 就是在按钮 Click 这个
///    DispatcherOperation 里跑),Completed 要等卡死结束才触发——所以后台线程每次取
///    快照时,若当前还有一个操作没完成(_depth&gt;0),把"到目前为止"的时长也临时计入
///    这次快照,让归因侧在卡死进行中也能看出苗头;但不追求这样算出来的时长在窗口间
///    可加,真正权威、能保证"卡死进行中就报出接近 100%"的是 A 探针,不是这里。
///
/// 两部分的窗口切分 + 上报都在后台线程完成(fire-and-forget unary,和 UI 线程的
/// Pong 共用同一个 CommonServiceClient——都是普通 unary,gRPC 客户端支持并发调用,
/// 不是往同一条 stream 里写,没有并发写坑)。UI 线程只做两件轻量的事:被探针的
/// BeginInvoke 排队执行、以及 hooks 回调里写几个累加字段——即使 UI 线程卡死,子进程
/// 依然能把饱和度报给主进程,这正是这套探针存在的全部意义。
/// </summary>
public sealed class UiSaturationMeter : IDisposable
{
    private const double StepMs = 15;
    private const double GraceMs = 8;
    private const double WindowMs = 1000;

    private readonly Dispatcher _dispatcher;
    private readonly CommonService.CommonServiceClient _common;
    private readonly string _sessionId;
    private readonly ILogger<UiSaturationMeter> _logger;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>OperationPosted→OperationStarted 之间的排队延迟计算:key 是
    /// DispatcherOperation 本身,value 是 post 时刻的 Stopwatch tick(装箱 long)。
    /// 只在 UI 线程读写,ConditionalWeakTable 顺带避免万一某个操作从未 Started/Aborted
    /// 时的内存堆积。</summary>
    private readonly ConditionalWeakTable<DispatcherOperation, object> _postedAtTicks = new();

    private readonly Lock _hookLock = new();
    private Thread? _thread;

    // ---- 探针状态:_probePending 后台线程写 true / UI 线程(探针回调)写 false;
    // _probePostedAtMs 只由后台线程读写,两边各自单向,不需要额外同步。----
    private volatile bool _probePending;
    private double _probePostedAtMs;

    // ---- hooks 累加状态(UI 线程在 hook 回调里写,后台线程在 _hookLock 下读取+清零)----
    private int _depth;
    private long _opStartTicks;
    private double _completedBusyMsWindow;
    private double _maxQueueLatencyMsWindow;
    private double _longestOpMsWindow;
    private int _opCountWindow;

    public UiSaturationMeter(Dispatcher dispatcher, CommonService.CommonServiceClient common, string sessionId,
        ILogger<UiSaturationMeter> logger)
    {
        _dispatcher = dispatcher;
        _common = common;
        _sessionId = sessionId;
        _logger = logger;
    }

    /// <summary>必须在 UI 线程调用(ChildShell.SourceInitialized 之后,channel/client
    /// 建好时):先挂 Dispatcher.Hooks,再拉起探针+上报的后台线程。</summary>
    public void Start()
    {
        var hooks = _dispatcher.Hooks;
        hooks.OperationPosted += OnOperationPosted;
        hooks.OperationStarted += OnOperationStarted;
        hooks.OperationCompleted += OnOperationCompleted;
        hooks.OperationAborted += OnOperationAborted;

        _thread = new Thread(() => ProbeLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "UiSaturationProbe",
        };
        _thread.Start();
    }

    /// <summary>ChildShell.Closed 时调用:停探针线程 + 摘掉 hooks。</summary>
    public void Dispose()
    {
        _cts.Cancel();

        var hooks = _dispatcher.Hooks;
        hooks.OperationPosted -= OnOperationPosted;
        hooks.OperationStarted -= OnOperationStarted;
        hooks.OperationCompleted -= OnOperationCompleted;
        hooks.OperationAborted -= OnOperationAborted;

        _cts.Dispose();
    }

    // ==================== A. 探针:权威饱和度(后台线程) ====================

    private void ProbeLoop(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        double windowStartMs = 0;
        double busyMs = 0;
        PostProbe(0);

        while (!ct.IsCancellationRequested)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(StepMs));
            if (ct.IsCancellationRequested) break;
            double now = sw.Elapsed.TotalMilliseconds;

            if (_probePending)
            {
                // 探针还没被执行:超过 grace 之后,这一步的墙钟切片算作"不可响应"。
                // UI 卡死时这个分支会连续命中直到卡死结束,把整段卡死墙钟积进 busyMs。
                if (now - _probePostedAtMs > GraceMs)
                    busyMs += StepMs;
            }
            else
            {
                // 探针已经被 UI 线程执行完:说明那一刻 UI 线程有空,立刻发下一个,
                // 维持"任意时刻至多一个未决探针"。
                PostProbe(now);
            }

            double windowElapsed = now - windowStartMs;
            if (windowElapsed >= WindowMs)
            {
                double saturationPct = Math.Clamp(100.0 * busyMs / windowElapsed, 0, 100);
                var (dispatcherBusyMs, maxQueueLatencyMs, longestOpMs, opCount) = SnapshotHooks();
                double dispatcherBusyPct = Math.Clamp(100.0 * dispatcherBusyMs / windowElapsed, 0, 100);

                Report(saturationPct, dispatcherBusyPct, maxQueueLatencyMs, longestOpMs, opCount);

                busyMs = 0;
                windowStartMs = now;
            }
        }
    }

    private void PostProbe(double nowMs)
    {
        _probePostedAtMs = nowMs;
        _probePending = true;
        // Background 优先级:低于输入/渲染等正常交互优先级,只有 UI 线程真正有
        // 空闲容量时才会被派发到——这正是"饱和度"要测的东西。
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () => _probePending = false);
    }

    // ==================== B. hooks:归因(UI 线程回调) ====================

    private void OnOperationPosted(object? sender, DispatcherHookEventArgs e) =>
        _postedAtTicks.AddOrUpdate(e.Operation, Stopwatch.GetTimestamp());

    private void OnOperationStarted(object? sender, DispatcherHookEventArgs e)
    {
        long nowTicks = Stopwatch.GetTimestamp();

        if (_postedAtTicks.TryGetValue(e.Operation, out var postedObj))
        {
            _postedAtTicks.Remove(e.Operation);
            double latencyMs = TicksToMs(nowTicks - (long)postedObj);
            lock (_hookLock)
            {
                if (latencyMs > _maxQueueLatencyMsWindow) _maxQueueLatencyMsWindow = latencyMs;
            }
        }

        lock (_hookLock)
        {
            // 用 depth 计数应对嵌套操作(某个操作内部又同步泵了一层消息循环):
            // 只在 depth 从 0→1 时记 start,避免嵌套部分被重复计时。
            if (_depth == 0)
            {
                _opStartTicks = nowTicks;
                _opCountWindow++;
            }
            _depth++;
        }
    }

    private void OnOperationCompleted(object? sender, DispatcherHookEventArgs e) => OnOperationEnded();
    private void OnOperationAborted(object? sender, DispatcherHookEventArgs e) => OnOperationEnded();

    private void OnOperationEnded()
    {
        long nowTicks = Stopwatch.GetTimestamp();
        lock (_hookLock)
        {
            if (_depth == 0) return; // 防御:理论上不该在 depth==0 时收到 Completed/Aborted
            if (--_depth == 0)
            {
                double durMs = TicksToMs(nowTicks - _opStartTicks);
                _completedBusyMsWindow += durMs;
                if (durMs > _longestOpMsWindow) _longestOpMsWindow = durMs;
            }
        }
    }

    /// <summary>后台线程每窗口调用一次:取走当前窗口的 hooks 累加值并清零。</summary>
    private (double busyMs, double maxQueueLatencyMs, double longestOpMs, int opCount) SnapshotHooks()
    {
        long nowTicks = Stopwatch.GetTimestamp();
        lock (_hookLock)
        {
            double busyMs = _completedBusyMsWindow;
            double longestOpMs = _longestOpMsWindow;

            // 若操作仍在飞(UI 线程正卡在某个 DispatcherOperation 里,比如 10s
            // 的 Thread.Sleep),把"到目前为止"的时长也临时计入这次快照,不等
            // Completed 触发,让归因侧在卡死进行中也能看出苗头;不清零 _opStartTicks,
            // 真正 Completed 时仍会按完整时长再记一次到 longestOpMs,不追求可加性。
            if (_depth > 0)
            {
                double inFlightMs = TicksToMs(nowTicks - _opStartTicks);
                busyMs += inFlightMs;
                if (inFlightMs > longestOpMs) longestOpMs = inFlightMs;
            }

            var result = (busyMs, _maxQueueLatencyMsWindow, longestOpMs, _opCountWindow);
            _completedBusyMsWindow = 0;
            _maxQueueLatencyMsWindow = 0;
            _longestOpMsWindow = 0;
            _opCountWindow = 0;
            return result;
        }
    }

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    // ==================== 上报 ====================

    private void Report(double saturationPct, double dispatcherBusyPct, double maxQueueLatencyMs, double longestOpMs, int opCount)
    {
        var req = new UiStatsRequest
        {
            SessionId = _sessionId,
            SaturationPct = saturationPct,
            DispatcherBusyPct = dispatcherBusyPct,
            MaxQueueLatencyMs = maxQueueLatencyMs,
            LongestOpMs = longestOpMs,
            OpCount = opCount,
        };
        _ = ReportAsync(req);
    }

    /// <summary>fire-and-forget,直接从探针所在的后台线程发出(不是 UI 线程),
    /// UI 卡死时这次调用完全不受影响,照样能报出去。</summary>
    private async Task ReportAsync(UiStatsRequest req)
    {
        try { await _common.ReportUiStatsAsync(req).ResponseAsync; }
        catch (Exception ex)
        {
            // 主进程不可达时忽略,下个窗口继续尝试——Debug 级别记一笔纯诊断日志,
            // 不改变"静默容忍、下个窗口重试"这个既有行为。
            _logger.LogDebug(ex, "ReportUiStats failed for session {SessionId}", _sessionId);
        }
    }
}
