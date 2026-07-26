using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Host;

/// <summary>
/// dock pane 里的空白占位控件。子进程窗口注册后,持续把它 overlay(SetWindowPos)
/// 在本控件的屏幕矩形上,并手动把它的 Z 序维持在宿主顶层窗口之上。
///
/// 不使用 owner 关系(SetWindowLongPtr(GWLP_HWNDPARENT))或 SetParent:
/// 跨进程建立 owner/parent 关系会让 Windows 隐式合并两个线程的输入队列
/// (效果等同 AttachThreadInput),一旦子进程 UI 线程卡死,主进程和其他
/// 子进程窗口的输入也会被一起冻住 —— 这正是本类要修复的 bug。改为:子窗口
/// 与宿主窗口完全没有 owner/parent 关系,只在宿主移动/缩放/Z 序变化时
/// (WM_WINDOWPOSCHANGED 钩子)以及本控件布局变化时,手动调用 SetWindowPos
/// 把子窗口的屏幕矩形和 Z 序都重新钉一遍,靠"持续纠正"而不是系统关系来
/// 保持 overlay 效果和层叠顺序。子窗口本身现在是完全可激活的(问题 1 修复,
/// 键盘输入需要),点击会被系统正常激活、提到系统 Z 序同级最前——这个"扰动"
/// 由本类的去抖 + 宿主 WM_WINDOWPOSCHANGED 钩子在下一轮触发时重新纠正回来,
/// 不需要额外处理。
///
/// 平铺布局(同一宿主同时显示多个 overlay,比如左右分栏)下曾经出现过多个
/// overlay 互抢 Z 序、永不收敛的问题:早期判据是"宿主正上方紧邻的必须是我自己",
/// 而这个位置同一时刻只有一个窗口能占住,两个 overlay 的判据永远不可能同时成立,
/// 于是每一轮触发(哪怕只是状态栏文字刷新引起的 LayoutUpdated)都会互相插队重发
/// SetWindowPos。修复很简单:把判据放宽成"我在宿主之上"(见
/// <see cref="IsChildAboveOwner"/>)——平铺的几个子窗口互不重叠,它们彼此之间谁上
/// 谁下没有任何视觉意义,只要都盖在宿主上方就是对的,多个 overlay 于是可以同时
/// 满足判据,矛盾消失。(曾经为此引入过一个"给同宿主下的 overlay 排一条确定性
/// Z 序链"的协调者,判据放宽之后它就没有存在意义了,已删除。)
///
/// 踩过的坑:仅仅去掉 owner 关系并不够 —— SetWindowPos/ShowWindow 对不同
/// 线程(含跨进程)的窗口默认会像 SendMessage 一样同步阻塞发送
/// WM_WINDOWPOSCHANGING/CHANGED 等消息,子窗口卡死时照样会拖住调用方所在的
/// 主进程 UI 线程(实测:子窗口卡死几秒后主窗口也开始对 SendMessageTimeout
/// 无响应)。必须给 SetWindowPos 加上 SWP_ASYNCWINDOWPOS,让系统改成把请求
/// post 给目标线程、调用方立即返回,才是真正不合并卡死影响的关键;隐藏子
/// 窗口也同理改用 SetWindowPos+SWP_HIDEWINDOW+SWP_ASYNCWINDOWPOS,而不是
/// 会同步阻塞的 ShowWindow(SW_HIDE)。
///
/// 宿主解析特意不用 Window.GetWindow(this):AvalonDock 把一个 LayoutDocument
/// 拖成浮动窗口时,只是把渲染挪到了新的 HwndSource(浮动窗口),WPF 的逻辑树
/// (Window.GetWindow 依赖 LogicalTreeHelper 链路)仍然挂在原来的
/// DockingManager/主窗口下、不会跟着切换 —— 实测浮动后 Window.GetWindow(this)
/// 仍返回主窗口。这里改用 PresentationSource 解析出真正承载渲染的 HwndSource,
/// 再用 GetAncestor(GA_ROOT) 走到顶层窗口句柄,这个句柄在浮动/停靠两种状态下
/// 都是准的(实测:浮动时该 HwndSource 的 RootVisual 是 AvalonDock 内部的一个
/// Border,GetAncestor 能正确一路走到浮动窗口本身的顶层 HWND)。
/// </summary>
public sealed class OverlayHost : Border
{
    private nint _childHwnd;
    private nint _ownerHwnd;
    private HwndSource? _ownerSource;

    /// <summary>"等待子进程窗口接入…" 占位内容,构造时造好存成字段——AttachChild 成功接上
    /// 子窗口、以及 RestartSession 重新拉起子进程之前,都要把 Child 打回这个中性状态(可能是
    /// 从过期的错误页切回来),不能每次都 new 一个新的 TextBlock。</summary>
    private readonly TextBlock _waitingContent;

    /// <summary>非 null 时,子进程意外退出后的错误页用调库方这里给的自定义内容;否则
    /// <see cref="ShowFault"/> 用框架内置的默认错误页(标题+明细+提示+重试按钮)。</summary>
    public Func<OverlayFaultInfo, FrameworkElement>? FaultContentFactory { get; set; }

    // 脏检查:记录上一次实际发给 SetWindowPos 的状态。tab 切换期间 AvalonDock
    // 一次切换会连续触发几十次 LayoutUpdated,而其中绝大多数并未改变本控件
    // 的屏幕矩形/可见性/Z 序,如果照样每次都发 SetWindowPos,大量重复/过时的
    // 请求会在子进程消息队列(SWP_ASYNCWINDOWPOS 是 post 过去的)里越积越多,
    // 最新的位置反而要排在最后面才会被处理——这正是"跟随明显变慢"的根因。
    // _lastVisible 为 false 时视为"状态未知",下一次 UpdatePlacement 无论
    // 算出什么值都必须发一次(用于强制刷新,见 ForceNextUpdate)。
    private bool _lastVisible;
    private int _lastX, _lastY, _lastCx, _lastCy;
    private bool _lastZOrderOk;

    // LayoutUpdated 去抖:同一轮消息循环里可能触发很多次,合并成一次即可,
    // 用 Dispatcher.BeginInvoke(Loaded) 排到"本轮布局全部落定之后"再执行。
    private bool _updatePending;

    /// <summary>验证/诊断用:所有 OverlayHost 实例累计实际发出的 SetWindowPos 调用
    /// 次数(含隐藏路径)。平铺布局静止后这个数字应该停止增长——这是 Z 序链收敛与否
    /// 的验收标准。刻意保持 internal + 无日志,不对外暴露、不刷屏,只在调试/验证时
    /// 用调试器或临时探针读取。</summary>
    internal static long SetWindowPosCallCount;

    public OverlayHost()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        _waitingContent = new TextBlock
        {
            Text = "等待子进程窗口接入…",
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = _waitingContent;

        Loaded += (_, _) => HookOwner();
        Unloaded += (_, _) => ScheduleUpdate();    // 被拖出/隐藏时先藏起子窗口
        LayoutUpdated += (_, _) => ScheduleUpdate();
        IsVisibleChanged += (_, _) => ScheduleUpdate();
    }

    /// <summary>
    /// 合并同一轮消息循环内的多次触发:已有一次待执行的调度就直接返回,
    /// 避免 tab 切换时几十个 LayoutUpdated 各排一次 BeginInvoke。
    /// </summary>
    private void ScheduleUpdate()
    {
        if (_updatePending)
        {
            return;
        }

        _updatePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _updatePending = false;
            UpdatePlacement();
        }));
    }

    /// <summary>强制下一次 UpdatePlacement 无视脏检查、一定发送一次。</summary>
    private void ForceNextUpdate() => _lastVisible = false;

    /// <summary>子进程上报 HWND 后调用(UI 线程)。</summary>
    public void AttachChild(nint childHwnd)
    {
        _childHwnd = childHwnd;
        // 重试成功接上新子窗口后,占位内容要从过期的错误页(或残留的等待态)回到中性的
        // 等待态——虽然会被子窗口盖住看不见,但这个会话之后再断开时会重新露出来,不能让
        // 上一次崩溃的错误页停留在那里。
        Child = _waitingContent;
        ForceNextUpdate();
        HookOwner();
        // HookOwner() 只在宿主发生变化时才会重新钉 Z 序;首次挂载时宿主可能
        // 早已确定(Loaded 已跑过),这里始终显式补一次 UpdatePlacement,确保
        // 子窗口的位置和 Z 序一定会被同步一次。低频关键路径,不走去抖调度。
        UpdatePlacement();
    }

    public void DetachChild()
    {
        if (_childHwnd != 0)
        {
            HideChildAsync(_childHwnd);
        }

        _childHwnd = 0;
        ForceNextUpdate();
    }

    /// <summary>子进程意外退出时(SessionManager 判定,见 SessionManager.HandleChildExited)
    /// 展示错误页 + 重试按钮,取代空白占位。经 Dispatcher.BeginInvoke 调用(触发源是线程池上的
    /// Process.Exited)。</summary>
    internal void ShowFault(OverlayFaultInfo info) =>
        Child = FaultContentFactory?.Invoke(info) ?? BuildDefaultFaultContent(info);

    /// <summary>RestartSession 重新拉起子进程之前调用,把占位内容从(可能过期的)错误页
    /// 打回中性等待态——新子进程真正连上来之前这段真空期,不能让错误页一直挂着。</summary>
    internal void ResetToWaiting() => Child = _waitingContent;

    /// <summary>框架内置的默认错误页:标题(红色加粗)+ 明细行(featureId/index/退出码,
    /// 十六进制形式方便对照常见的崩溃码如 0xC0000005)+ 灰色提示 + 重试按钮。</summary>
    private static FrameworkElement BuildDefaultFaultContent(OverlayFaultInfo info)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 360,
        };

        stack.Children.Add(new TextBlock
        {
            Text = "子进程意外退出",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x51, 0x51)),
            FontWeight = FontWeights.Bold,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"{info.FeatureId} #{info.FeatureIndex}    exit code {info.ExitCode} (0x{info.ExitCode:X8})",
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        });

        stack.Children.Add(new TextBlock
        {
            Text = "窗格保持打开,可以重试;关闭窗格会彻底结束这个实例。",
            Foreground = Brushes.Gray,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var retryButton = new Button
        {
            Content = "重试",
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // 防连点:点一下立刻禁用,RestartSession 成功后 AttachChild 会把 Child 整个换掉
        // (新子窗口盖住这块区域),这个按钮本身不需要、也没机会被重新启用。
        retryButton.Click += (_, _) =>
        {
            retryButton.IsEnabled = false;
            info.Retry();
        };
        stack.Children.Add(retryButton);

        return stack;
    }

    /// <summary>
    /// 隐藏子窗口。特意不用 ShowWindow(SW_HIDE):它和 SetWindowPos 一样,对不同
    /// 线程/进程的窗口会同步阻塞发消息,目标线程卡死时会拖住调用方。改用
    /// SetWindowPos + SWP_HIDEWINDOW,并同样带上 SWP_ASYNCWINDOWPOS 保证不阻塞。
    /// </summary>
    private static void HideChildAsync(nint hwnd)
    {
        System.Threading.Interlocked.Increment(ref SetWindowPosCallCount);
        Win32.SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            Win32.SWP_HIDEWINDOW | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER |
            Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS);
    }

    /// <summary>
    /// 重新解析当前承载渲染的顶层窗口句柄(dock 状态下是主窗口,浮动状态下是
    /// AvalonDock 生成的浮动窗口)。宿主发生变化时(dock↔float 切换)重挂
    /// 窗口消息钩子,并重新把子窗口的 Z 序钉到新宿主上方(不建立 owner 关系,
    /// 见类顶部说明)。
    /// </summary>
    private void HookOwner()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource src)
        {
            return;
        }

        nint root = Win32.GetAncestor(src.Handle, Win32.GA_ROOT);
        if (root == 0)
        {
            root = src.Handle;
        }

        if (root == _ownerHwnd)
        {
            return;
        }

        _ownerSource?.RemoveHook(OnOwnerWndProc);
        _ownerSource = null;

        _ownerHwnd = root;

        // 单独挂顶层窗口自己的 WndProc 钩子来监听移动/缩放/最小化还原,从而在
        // 宿主移动时刷新 overlay 位置 —— 纯 Win32 层面的钩子,不依赖 Window 对象,
        // dock/float 两种宿主都适用(纯移动不会触发 WPF 的 LayoutUpdated)。
        var rootSource = HwndSource.FromHwnd(root);
        if (rootSource is not null)
        {
            rootSource.AddHook(OnOwnerWndProc);
            _ownerSource = rootSource;
        }

        // 宿主换了,Z 序参照物跟着变了,哪怕子窗口位置数值凑巧没变也必须
        // 重新发一次,所以这里强制跳过脏检查。宿主切换是低频事件,不去抖。
        ForceNextUpdate();
        UpdatePlacement();
    }

    private nint OnOwnerWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // WM_WINDOWPOSCHANGED 覆盖移动、缩放、显示/隐藏、Z 序变化。拖动主窗口时
        // 这个消息会连续触发很多次,同样走去抖合并,避免过程中排一堆 SetWindowPos。
        if (msg == Win32.WM_WINDOWPOSCHANGED)
        {
            ScheduleUpdate();
        }

        return 0;
    }

    /// <summary>把子窗口钉在占位区域的屏幕坐标上;占位不可见时隐藏子窗口。</summary>
    private void UpdatePlacement()
    {
        if (_childHwnd == 0)
        {
            return;
        }

        // 兜底:LayoutUpdated/IsVisibleChanged 可能在 dock↔float 切换后、
        // HookOwner 真正跑到之前先触发,这里检测到宿主已经变了就立刻重新挂接。
        if (PresentationSource.FromVisual(this) is HwndSource curSrc)
        {
            nint curRoot = Win32.GetAncestor(curSrc.Handle, Win32.GA_ROOT);
            if (curRoot == 0)
            {
                curRoot = curSrc.Handle;
            }

            if (curRoot != _ownerHwnd)
            {
                HookOwner();
            }
        }

        bool show = IsLoaded && IsVisible && ActualWidth > 0 && ActualHeight > 0
                    && _ownerHwnd != 0 && !Win32.IsIconic(_ownerHwnd)
                    && PresentationSource.FromVisual(this) is not null;
        if (!show)
        {
            if (_lastVisible)
            {
                HideChildAsync(_childHwnd);
                _lastVisible = false;
            }
            return;
        }

        // 设备无关坐标 → 物理像素(PerMonitorV2 下 PointToScreen 已含 DPI 换算)
        var tl = PointToScreen(new Point(0, 0));
        var br = PointToScreen(new Point(ActualWidth, ActualHeight));
        int x = (int)Math.Round(tl.X);
        int y = (int)Math.Round(tl.Y);
        int cx = Math.Max(1, (int)Math.Round(br.X - tl.X));
        int cy = Math.Max(1, (int)Math.Round(br.Y - tl.Y));

        // 没有 owner 关系后,子窗口的 Z 序不再被系统自动维持在宿主上方,只能每次
        // 手动判一遍、不对就重钉。判据是"我在宿主之上"而不是"我紧贴宿主正上方":
        // 后者同一时刻只有一个窗口能占住,平铺布局(同一宿主同时显示多个 overlay)
        // 下多个子窗口会永远互相插队、每一轮触发都重发 SetWindowPos,永不收敛;
        // 前者多个子窗口可以同时满足,矛盾消失。平铺的几个子窗口互不重叠,它们
        // 彼此之间谁上谁下没有任何视觉意义,只要都盖在宿主上方即可。
        bool zOrderOk = IsChildAboveOwner();
        // 需要纠正时,拿宿主"上方"紧邻的窗口当插入锚点,把子窗口插到它下面——也就是
        // 紧贴宿主上方。返回 0 表示宿主已经是 Z 序最顶端,此时 (HWND)0 恰好就是
        // HWND_TOP,语义正好一致,不需要特殊处理。
        // 这里不需要像判据那样跳过不可见窗口(WPF 每个进程都自带若干 Win32 层面
        // 不可见的 HwndWrapper 消息窗口):锚点只决定"插在谁下面",插到一个隐藏
        // 窗口下面照样落在宿主上方,判据随即成立、收敛。真正会被这些隐藏窗口卡住
        // 的是"紧贴"式判据,而它已经被上面的 IsChildAboveOwner 取代了。
        nint rawInsertAfter = Win32.GetWindow(_ownerHwnd, Win32.GW_HWNDPREV);

        // 脏检查:Z 序不对时(zOrderOk == false)无论位置是否变化都必须发,这是
        // 本设计维持层叠顺序的核心;Z 序已经正确时,位置/大小/可见性都和上次
        // 发出的完全一样才跳过 —— tab 切换期间绝大多数 LayoutUpdated 触发都会
        // 落进这个"跳过"分支,SetWindowPos 调用量随之大幅下降。
        if (_lastVisible && zOrderOk && _lastZOrderOk
            && x == _lastX && y == _lastY && cx == _lastCx && cy == _lastCy)
        {
            return;
        }

        uint zFlags = Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW | Win32.SWP_ASYNCWINDOWPOS;
        nint insertAfter = rawInsertAfter;
        if (zOrderOk)
        {
            // SetWindowPos 的 hWndInsertAfter 传自身会失败/无效果,这里直接跳过、
            // 只保留 SWP_NOZORDER,只更新位置/大小。
            zFlags |= Win32.SWP_NOZORDER;
            insertAfter = 0;
        }
        // else: rawInsertAfter 要么是 0(GW_HWNDPREV 返回 0 表示参照物已经是 Z 序
        // 最顶端窗口,直接把子窗口插到 HWND_TOP——(HWND)0 和"没有更上面的窗口"
        // 恰好是同一个值——就是紧贴参照物上方),要么是别的窗口,两种情况都保留
        // insertAfter 原值、不加 SWP_NOZORDER,让 SetWindowPos 真正调整 Z 序。

        // SWP_ASYNCWINDOWPOS 是这里最关键的一个 flag:见 Win32.cs 常量注释,
        // 没有它的话,子窗口卡死时这一句 SetWindowPos 会把调用方(主进程 UI
        // 线程)一起拖住,又变相重新引入了本类要修复的"一个卡死全部卡死"。
        System.Threading.Interlocked.Increment(ref SetWindowPosCallCount);
        Win32.SetWindowPos(_childHwnd, insertAfter, x, y, cx, cy, zFlags);
        _lastVisible = true;
        _lastX = x; _lastY = y; _lastCx = cx; _lastCy = cy;
        _lastZOrderOk = zOrderOk;
    }

    /// <summary>
    /// 沿宿主的 GW_HWNDPREV 一路向"上"(Z 序更靠前)走,看能不能在有限步数内碰到
    /// 自己的子窗口——碰到了就说明子窗口确实盖在宿主上方,Z 序无需纠正。
    ///
    /// 用步数上限而不是走到链表尽头:整条 Z 序链上的顶层窗口可能非常多(桌面上
    /// 每个进程的每个顶层窗口都在里面,还包括大量不可见的内部消息窗口),而子窗口
    /// 是被本类主动钉在宿主附近的,正常情况下几步之内就能命中;走满上限还没找到
    /// 就当作"不在上方"、触发一次纠正——纠正本身是幂等的,偶尔多做一次没有副作用,
    /// 总比每次判定都遍历整条链划算。
    /// </summary>
    private bool IsChildAboveOwner()
    {
        const int MaxSteps = 50;
        nint cursor = _ownerHwnd;
        for (int i = 0; i < MaxSteps; i++)
        {
            cursor = Win32.GetWindow(cursor, Win32.GW_HWNDPREV);
            if (cursor == 0)
            {
                return false;          // 已经走到 Z 序顶端,没碰到自己
            }

            if (cursor == _childHwnd)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>意外退出错误页需要的展示数据 + 重试动作。<see cref="Retry"/> 是框架给的动作
/// (内部就是 SessionManager.RestartSession(sessionId)),这样自定义 <see cref="OverlayHost.FaultContentFactory"/>
/// 不需要知道 SessionManager/sessionId 的存在,拿到这个 record 就能拼错误 UI、挂重试按钮。</summary>
public sealed record OverlayFaultInfo(string FeatureId, int FeatureIndex, int ExitCode, Action Retry);
