using System.Windows;

namespace WpfMultiProcess.Child;

/// <summary>
/// feature 作者要实现的子进程侧接缝(MVVM 版,取代原来的 IFeatureChildModule):
/// 按已建好的 ChildContext 构造自己的 ViewModel(内部自己用 ctx.Channel 建 feature
/// client、自己发起 Register 开流请求带 session_id/hwnd/pid,并把返回的
/// AsyncServerStreamingCall 交给 FeatureViewModel&lt;TDown&gt; 基类),再按这个
/// ViewModel 构造对应的 View(View 是绑定该 ViewModel 的 UserControl,DataContext=vm)。
/// CreateViewModel/CreateView 总是配对调用(同一个 ChildProgram bootstrap 里前后脚
/// 执行),因此 CreateView 内部把 <see cref="FeatureViewModel"/> 转型回具体类型是安全的。
/// </summary>
public interface IFeatureChild
{
    string FeatureId { get; }

    FeatureViewModel CreateViewModel(ChildContext ctx);

    FrameworkElement CreateView(FeatureViewModel viewModel);
}
