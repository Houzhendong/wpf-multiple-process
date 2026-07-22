using System.Windows;

namespace WpfMultiProcess.Child.Features.Table;

/// <summary>table feature 的子进程侧模块:唯一职责是按会话生成对应视图。</summary>
public sealed class TableChildModule : IFeatureChildModule
{
    public string FeatureId => "table";

    public FrameworkElement CreateView(ChildContext ctx) => new TableView(ctx);
}
