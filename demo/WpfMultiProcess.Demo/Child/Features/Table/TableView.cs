using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WpfMultiProcess.Demo.Child.Features.Table;

/// <summary>
/// table feature 的子进程视图(MVVM 拆分后纯 View,绑定 TableViewModel):DataGrid 绑定
/// vm.Rows + "按值排序"按钮(调用 vm.ToggleSortAsync)。行集合/流 demux 全部下沉到
/// TableViewModel,这里只管展示。
/// </summary>
public sealed class TableView : UserControl
{
    public TableView(TableViewModel vm)
    {
        DataContext = vm;

        var grid = new DataGrid
        {
            ItemsSource = vm.Rows,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserSortColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "Id", Binding = new Binding(nameof(RowVM.Id)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(RowVM.Name)) });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Value", Binding = new Binding(nameof(RowVM.Value)) { StringFormat = "F2" },
        });
        grid.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new Binding(nameof(RowVM.Status)) });

        var sortButton = new Button
        {
            Content = "按值排序",
            Margin = new Thickness(8, 4, 4, 4),
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        sortButton.Click += async (_, _) => await vm.ToggleSortAsync();

        var root = new DockPanel();
        DockPanel.SetDock(sortButton, Dock.Top);
        root.Children.Add(sortButton);
        root.Children.Add(grid);
        Content = root;

        Unloaded += (_, _) => vm.Dispose();
    }
}
