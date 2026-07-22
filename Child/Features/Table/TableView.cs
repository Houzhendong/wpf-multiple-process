using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Grpc.Core;
using WpfMultiProcess.Ipc.Table;
using Control = WpfMultiProcess.Ipc.Common.Control;

namespace WpfMultiProcess.Child.Features.Table;

/// <summary>
/// table feature 的子进程视图:DataGrid 绑定行集合 + "按值排序"按钮(调用
/// TableService.Sort 在 value 升/降序间切换)。会话建立(带 hwnd/pid)并进这次
/// Register 开流请求,不再单独有一次 RegisterWindow RPC。流 demux 同 WaveformView:
/// down.Reply → ctx.Shell.ApplyReply;down.Control → ctx.Shell.OnPing/RequestClose;
/// down.Delta → 应用 upserts/removed_ids,并按 ordered_ids 把可见集合重新排一遍
/// (排序状态在主进程侧维护,这里只是照着排)。
/// </summary>
public sealed class TableView : DockPanel
{
    private readonly ChildContext _ctx;
    private readonly TableService.TableServiceClient _client;
    private readonly ObservableCollection<RowVM> _rows = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _sortAscending = true;

    public TableView(ChildContext ctx)
    {
        _ctx = ctx;
        _client = new TableService.TableServiceClient(ctx.Channel);

        var grid = new DataGrid
        {
            ItemsSource = _rows,
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
        sortButton.Click += async (_, _) => await SortAsync();

        DockPanel.SetDock(sortButton, Dock.Top);
        Children.Add(sortButton);
        Children.Add(grid);

        Unloaded += (_, _) => _cts.Cancel();
        _ = StreamAsync(_cts.Token);
    }

    private async Task SortAsync()
    {
        // 每次点击在升/降序间切换,方便肉眼/UIA 都能确认"点了确实变了"。
        _sortAscending = !_sortAscending;
        try
        {
            await _client.SortAsync(new SortRequest
            {
                SessionId = _ctx.SessionId,
                Field = "value",
                Ascending = _sortAscending,
            }, deadline: DateTime.UtcNow.AddSeconds(5));
        }
        catch { /* 主进程不可达时忽略,不影响窗口自身交互 */ }
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
                    case TableDown.KindOneofCase.Reply:
                        _ = Dispatcher.BeginInvoke(() => _ctx.Shell.ApplyReply(down.Reply));
                        break;
                    case TableDown.KindOneofCase.Control:
                        OnControl(down.Control);
                        break;
                    case TableDown.KindOneofCase.Delta:
                        var delta = down.Delta;
                        _ = Dispatcher.BeginInvoke(() => ApplyDelta(delta));
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
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

    private void ApplyDelta(TableDelta delta)
    {
        foreach (var id in delta.RemovedIds)
        {
            var existing = _rows.FirstOrDefault(r => r.Id == id);
            if (existing is not null) _rows.Remove(existing);
        }

        foreach (var row in delta.Upserts)
        {
            var existing = _rows.FirstOrDefault(r => r.Id == row.Id);
            if (existing is not null)
            {
                existing.Name = row.Name;
                existing.Value = row.Value;
                existing.Status = row.Status;
            }
            else
            {
                _rows.Add(new RowVM(row.Id, row.Name, row.Value, row.Status));
            }
        }

        for (int i = 0; i < delta.OrderedIds.Count; i++)
        {
            var vm = _rows.FirstOrDefault(r => r.Id == delta.OrderedIds[i]);
            if (vm is null) continue;
            int cur = _rows.IndexOf(vm);
            if (cur != i) _rows.Move(cur, i);
        }
    }
}

/// <summary>DataGrid 绑定的行视图模型;Value/Status 实现 INotifyPropertyChanged,
/// 让每帧的原地更新(而不是整表重建)能反映到 UI,避免闪烁/丢选中状态。</summary>
public sealed class RowVM(long id, string name, double value, string status) : INotifyPropertyChanged
{
    private string _name = name;
    private double _value = value;
    private string _status = status;

    public long Id { get; } = id;

    public string Name
    {
        get => _name;
        set { _name = value; OnChanged(nameof(Name)); }
    }

    public double Value
    {
        get => _value;
        set { _value = value; OnChanged(nameof(Value)); }
    }

    public string Status
    {
        get => _status;
        set { _status = value; OnChanged(nameof(Status)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
