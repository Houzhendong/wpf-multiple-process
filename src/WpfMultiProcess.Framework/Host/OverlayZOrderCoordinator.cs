using System.Collections.Concurrent;

namespace WpfMultiProcess.Host;

/// <summary>
/// 问题 2 修复(平铺布局下多 overlay 互抢 Z 序):按宿主根 HWND 分组维护 overlay
/// 注册表,给同一宿主下的多个 <see cref="OverlayHost"/> 排出一条确定性的 Z 序链
/// (按登记序:child₁ 钉在宿主正上方,child₂ 钉在 child₁ 正上方,……),取代原来每个
/// OverlayHost 各自独立地"以宿主为参照物"钉自己。
///
/// bug 是怎么发生的:去掉 owner 关系后,原来每个 OverlayHost.UpdatePlacement 都固定
/// 用 GetWindow(宿主, GW_HWNDPREV) 判断"宿主正上方紧邻的是不是我自己"。tab 场景下
/// 同一时刻只有一个 overlay 可见,这条假设永远成立;但平铺布局(比如左右分栏同时显示
/// 两个 pane)下,同一宿主同时有两个可见 overlay:A 先钉完,Z 序变成 [A, host]；B
/// 检查发现"宿主正上方是 A,不是我",于是把自己插进去变成 [A, B, host]——这时候
/// A 用同样的方法再检查,"宿主正上方"现在是 B 不是 A,判定自己 Z 序不对,又把自己
/// 插回宿主正上方变成 [B, A, host]。两边的判据都只认"宿主正上方是不是我自己"，
/// 永远不可能同时满足,每一次触发 UpdatePlacement 的事件(哪怕只是状态栏心跳/UI
/// 饱和度文本刷新引发的 LayoutUpdated,约 1~2 秒一次)都会引发一轮 SetWindowPos
/// 互相插队,永不收敛——这正是"平铺时两个子窗口疯狂互相盖"的根因。
///
/// 修复思路:两个 overlay 不能都以"宿主"为参照物,必须让它们的参照物按某种全局
/// 一致的顺序错开。这里选最简单的确定性顺序——登记序(每个 OverlayHost 第一次挂到
/// 某个宿主根 HWND 时,在该宿主的链表里追加一个位置,先登记的排更靠近宿主)。链条
/// 里每个 overlay 只以"链里排在它前面、且当前正显示着子窗口"的那个 overlay 为
/// 参照物(取代原来固定的"宿主"),取到参照物之后,具体怎么算 hWndInsertAfter、
/// 怎么用返回值判断"是否已经贴对了"完全复用 OverlayHost 原来对宿主做的那一套
/// (GetWindow(参照物, GW_HWNDPREV) 是否等于自己),不需要在这里重新发明——链条本身
/// 只负责"每个 overlay 该盯着谁"，具体的 SetWindowPos 决策留给 OverlayHost 自己的
/// 脏检查。这样只要链条稳定,各 overlay 的脏检查各自独立收敛,不再需要每次都重钉
/// 整条链,平铺静止时应当收敛到零 SetWindowPos 调用。
///
/// 三种场景下的行为:
///   - tab(同一宿主同一时刻只有一个 overlay 可见):其余 overlay 因为
///     "当前正显示子窗口"这个条件不成立而被跳过,链退化成长度 1,行为和没有这个
///     协调者之前完全一样。
///   - 平铺(同一宿主同时有多个 overlay 可见):本类要解决的场景,链长等于当前可见
///     的 overlay 数,每个都紧贴在前一个正上方。
///   - 浮动(AvalonDock 把某个 pane 拖成独立窗口):浮动窗口是不同的顶层根 HWND,
///     天然落在另一条链上,和仍然停靠着的其它 pane 互不干扰,不需要特殊处理。
///
/// 线程:所有方法都只应该从 UI 线程(OverlayHost 所在的 Dispatcher)调用——
/// OverlayHost 本身就是 UI 线程独占的 DependencyObject。这里仍然用
/// ConcurrentDictionary + 显式锁,是防御性的写法(不假设调用方一定是单线程),
/// 不代表设计上真的允许跨线程并发访问。
/// </summary>
internal static class OverlayZOrderCoordinator
{
    // key = 宿主根 HWND,value = 该宿主下所有登记过的 OverlayHost,按登记序排列
    // (List 的顺序即链序,index 0 最靠近宿主)。
    private static readonly ConcurrentDictionary<nint, List<OverlayHost>> _chains = new();
    private static readonly Lock _lock = new();

    /// <summary>OverlayHost 第一次挂到(或换宿主后重新挂到)某个宿主根 HWND 时调用:
    /// 按登记序追加到该宿主链条的末尾。已经登记过同一个 (rootHwnd, overlay) 则什么都
    /// 不做(幂等),避免 HookOwner 在同一宿主上重复触发时把自己在链里搞出重复项。</summary>
    public static void Register(nint rootHwnd, OverlayHost overlay)
    {
        if (rootHwnd == 0) return;
        lock (_lock)
        {
            var list = _chains.GetOrAdd(rootHwnd, static _ => []);
            if (!list.Contains(overlay)) list.Add(overlay);
        }
    }

    /// <summary>OverlayHost 换宿主(dock↔float 切换,HookOwner 检测到根 HWND 变化)
    /// 或最终 DetachChild(会话结束,这个 overlay 再也不会有子窗口需要钉)时调用,
    /// 把它从旧宿主的链条里摘掉,避免留下一个再也用不到、还占着链位的僵尸登记项。</summary>
    public static void Unregister(nint rootHwnd, OverlayHost overlay)
    {
        if (rootHwnd == 0) return;
        lock (_lock)
        {
            if (!_chains.TryGetValue(rootHwnd, out var list)) return;
            list.Remove(overlay);
            if (list.Count == 0) _chains.TryRemove(rootHwnd, out _);
        }
    }

    /// <summary>
    /// 查询 <paramref name="overlay"/> 在它所属宿主链条里应该紧贴在其正上方的目标
    /// 句柄:链首(前面没有任何当前可见的 overlay)返回宿主根 HWND 本身——和去掉
    /// 本协调者之前的行为完全一致;否则返回链条里排在它前面、且当前正显示着子窗口
    /// 的那个 overlay 的子 HWND。不可见/尚未挂子窗口的 overlay 直接跳过,链条据此
    /// 自动收缩——tab 切走隐藏的 pane 不占链位,切回来时凭当前可见状态重新计入,
    /// 不需要显式的"进/出链"操作。
    /// </summary>
    public static nint GetPredecessor(nint rootHwnd, OverlayHost overlay)
    {
        if (!_chains.TryGetValue(rootHwnd, out var list)) return rootHwnd;
        lock (_lock)
        {
            nint predecessor = rootHwnd;
            foreach (var candidate in list)
            {
                if (ReferenceEquals(candidate, overlay)) break;
                nint hwnd = candidate.VisibleChildHwnd;
                if (hwnd != 0) predecessor = hwnd;
            }
            return predecessor;
        }
    }
}
