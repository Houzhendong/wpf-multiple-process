using System.Runtime.InteropServices;

namespace WpfMultiProcess.Ipc;

public static partial class Win32
{
    public const int GWLP_HWNDPARENT = -8;
    public const int GWL_EXSTYLE = -20;

    // WS_EX_NOACTIVATE 已经不再使用(问题 1 修复:子窗口全部改为可激活,否则键盘
    // 输入永远进不去,详见 ChildWindow),但 WS_EX_TOOLWINDOW 仍然保留——它只影响
    // 任务栏/Alt-Tab 可见性,和激活行为无关。
    public const uint WS_EX_TOOLWINDOW = 0x00000080;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_HIDEWINDOW = 0x0080;

    /// <summary>
    /// 关键:调用线程和目标窗口所属线程如果不在同一个输入队列(本框架去掉
    /// owner 关系后,主进程和子进程永远不在同一输入队列),SetWindowPos 默认
    /// 会像 SendMessage 一样同步阻塞发送 WM_WINDOWPOSCHANGING/CHANGED 等消息,
    /// 直到目标线程处理完 —— 如果目标线程(某个子进程 UI 线程)卡死,调用方
    /// (主进程 UI 线程)会被一起拖住,直到系统大约 5 秒后的"ghosting"超时兜底
    /// 放行。这个 flag 让系统改为把请求 post 给目标线程、调用方立即返回,
    /// 是让"手动 SetWindowPos 覆盖 owner"方案真正不合并卡死影响的关键。
    /// </summary>
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8;

    /// <summary>GetAncestor 的 gaFlags:从任意子/顶层窗口一路走到真正的顶层窗口
    /// (不会像 GA_ROOTOWNER 那样继续跨过 owner 链条,所以浮动窗口不会被误判成主窗口)。</summary>
    public const uint GA_ROOT = 2;

    /// <summary>GetWindow 的 uCmd:紧邻在指定窗口"上方"(Z 序更靠前)的窗口。</summary>
    public const uint GW_HWNDPREV = 3;

    public const int WM_WINDOWPOSCHANGED = 0x0047;

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hWnd, uint gaFlags);

    /// <summary>取指定窗口在 Z 序中相邻的窗口句柄(uCmd=GW_HWNDPREV 时为"上方"紧邻的窗口)。</summary>
    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    /// <summary>
    /// 查子窗口自己的 Win32 可见性,而不是 WPF 的 IsVisible/IsVisibleChanged:
    /// 隐藏路径是主进程异步 post 过来的 SetWindowPos+SWP_HIDEWINDOW,和本地
    /// WPF 布局系统更新 IsVisible 之间不保证严格同步,直接问 Win32 最准确。
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    /// <summary>BitBlt 的 rop:直接拷贝源像素,不做任何逻辑运算。</summary>
    public const uint SRCCOPY = 0x00CC0020;

    /// <summary>
    /// 把一块设备上下文里的像素拷贝到另一块——用于给切换瞬间的过渡占位截一张桌面区域的
    /// 静态快照(见 OverlayHost)。特意不用 PrintWindow(叫目标窗口"自己再画一遍给我",
    /// 和 SendMessage 语义相同,子进程卡死会拖住调用方所在线程);BitBlt 只是读源 DC 里
    /// 已经合成好的像素,不跟任何窗口的消息队列打交道,可以放心同步跑在 UI 线程上。
    /// </summary>
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint hdcDest, int xDest, int yDest, int width, int height, nint hdcSrc, int xSrc, int ySrc, uint rop);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint hObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint hObject);
}
