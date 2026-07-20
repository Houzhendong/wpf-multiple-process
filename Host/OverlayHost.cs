using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Host;

/// <summary>
/// dock pane 里的空白占位控件。子进程窗口注册后,持续把它 overlay(SetWindowPos)
/// 在本控件的屏幕矩形上,并手动把它的 Z 序钉在"宿主顶层窗口"正上方。
///
/// 不使用 owner 关系(SetWindowLongPtr(GWLP_HWNDPARENT))或 SetParent:
/// 跨进程建立 owner/parent 关系会让 Windows 隐式合并两个线程的输入队列
/// (效果等同 AttachThreadInput),一旦子进程 UI 线程卡死,主进程和其他
/// 子进程窗口的输入也会被一起冻住 —— 这正是本类要修复的 bug。改为:子窗口
/// 与宿主窗口完全没有 owner/parent 关系,只在宿主移动/缩放/Z 序变化时
/// (WM_WINDOWPOSCHANGED 钩子)以及本控件布局变化时,手动调用 SetWindowPos
/// 把子窗口的屏幕矩形和 Z 序都重新钉一遍,靠"持续纠正"而不是系统关系来
/// 保持 overlay 效果和层叠顺序。子窗口本身也改为 WS_EX_NOACTIVATE(见
/// ChildWindow),点击不会抢激活、不会打乱这里维护的 Z 序。
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
    private bool _visibleNow;

    public OverlayHost()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        Child = new TextBlock
        {
            Text = "等待子进程窗口接入…",
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Loaded += (_, _) => HookOwner();
        Unloaded += (_, _) => UpdatePlacement();   // 被拖出/隐藏时先藏起子窗口
        LayoutUpdated += (_, _) => UpdatePlacement();
        IsVisibleChanged += (_, _) => UpdatePlacement();
    }

    /// <summary>子进程上报 HWND 后调用(UI 线程)。</summary>
    public void AttachChild(nint childHwnd)
    {
        _childHwnd = childHwnd;
        _visibleNow = false;
        HookOwner();
        // HookOwner() 只在宿主发生变化时才会重新钉 Z 序;首次挂载时宿主可能
        // 早已确定(Loaded 已跑过),这里始终显式补一次 UpdatePlacement,确保
        // 子窗口的位置和 Z 序一定会被同步一次。
        UpdatePlacement();
    }

    public void DetachChild()
    {
        if (_childHwnd != 0)
            HideChildAsync(_childHwnd);
        _childHwnd = 0;
        _visibleNow = false;
    }

    /// <summary>
    /// 隐藏子窗口。特意不用 ShowWindow(SW_HIDE):它和 SetWindowPos 一样,对不同
    /// 线程/进程的窗口会同步阻塞发消息,目标线程卡死时会拖住调用方。改用
    /// SetWindowPos + SWP_HIDEWINDOW,并同样带上 SWP_ASYNCWINDOWPOS 保证不阻塞。
    /// </summary>
    private static void HideChildAsync(nint hwnd) =>
        Win32.SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            Win32.SWP_HIDEWINDOW | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER |
            Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS);

    /// <summary>
    /// 重新解析当前承载渲染的顶层窗口句柄(dock 状态下是主窗口,浮动状态下是
    /// AvalonDock 生成的浮动窗口)。宿主发生变化时(dock↔float 切换)重挂
    /// 窗口消息钩子,并重新把子窗口的 Z 序钉到新宿主上方(不建立 owner 关系,
    /// 见类顶部说明)。
    /// </summary>
    private void HookOwner()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource src) return;

        nint root = Win32.GetAncestor(src.Handle, Win32.GA_ROOT);
        if (root == 0) root = src.Handle;
        if (root == _ownerHwnd) return;

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

        UpdatePlacement();
    }

    private nint OnOwnerWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        // WM_WINDOWPOSCHANGED 覆盖移动、缩放、显示/隐藏、Z 序变化。
        if (msg == Win32.WM_WINDOWPOSCHANGED)
            UpdatePlacement();
        return 0;
    }

    /// <summary>把子窗口钉在占位区域的屏幕坐标上;占位不可见时隐藏子窗口。</summary>
    private void UpdatePlacement()
    {
        if (_childHwnd == 0) return;

        // 兜底:LayoutUpdated/IsVisibleChanged 可能在 dock↔float 切换后、
        // HookOwner 真正跑到之前先触发,这里检测到宿主已经变了就立刻重新挂接。
        if (PresentationSource.FromVisual(this) is HwndSource curSrc)
        {
            nint curRoot = Win32.GetAncestor(curSrc.Handle, Win32.GA_ROOT);
            if (curRoot == 0) curRoot = curSrc.Handle;
            if (curRoot != _ownerHwnd) HookOwner();
        }

        bool show = IsLoaded && IsVisible && ActualWidth > 0 && ActualHeight > 0
                    && _ownerHwnd != 0 && !Win32.IsIconic(_ownerHwnd)
                    && PresentationSource.FromVisual(this) is not null;
        if (!show)
        {
            if (_visibleNow)
            {
                HideChildAsync(_childHwnd);
                _visibleNow = false;
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

        // 没有 owner 关系后,子窗口的 Z 序不再被系统自动维持在宿主上方,这里
        // 每次都手动算一遍、钉一遍:取宿主"上方"紧邻的窗口(GW_HWNDPREV),把
        // 子窗口插到它的后面 —— 效果就是紧贴在宿主正上方。
        nint insertAfter = Win32.GetWindow(_ownerHwnd, Win32.GW_HWNDPREV);
        // SWP_ASYNCWINDOWPOS 是这里最关键的一个 flag:见 Win32.cs 常量注释,
        // 没有它的话,子窗口卡死时这一句 SetWindowPos 会把调用方(主进程 UI
        // 线程)一起拖住,又变相重新引入了本类要修复的"一个卡死全部卡死"。
        uint zFlags = Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW | Win32.SWP_ASYNCWINDOWPOS;
        if (insertAfter == 0)
        {
            // GW_HWNDPREV 返回 0 表示宿主已经是 Z 序最顶端的窗口,此时直接把子窗口
            // 插到 HWND_TOP((HWND)0,和这里的"没有更上面的窗口"恰好是同一个值)
            // 就是紧贴宿主上方,insertAfter 保持 0 即可,无需特殊处理。
        }
        else if (insertAfter == _childHwnd)
        {
            // 宿主上方紧邻的已经是子窗口自己,说明 Z 序已经正确,不需要再调整。
            // SetWindowPos 的 hWndInsertAfter 传自身会失败/无效果,这里直接跳过、
            // 只保留 SWP_NOZORDER,只更新位置/大小。
            zFlags |= Win32.SWP_NOZORDER;
            insertAfter = 0;
        }

        Win32.SetWindowPos(_childHwnd, insertAfter, x, y, cx, cy, zFlags);
        _visibleNow = true;
    }
}
