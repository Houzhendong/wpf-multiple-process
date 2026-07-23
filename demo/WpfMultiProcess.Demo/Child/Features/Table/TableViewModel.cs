using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using WpfMultiProcess.Child;
using WpfMultiProcess.Ipc.Table;

namespace WpfMultiProcess.Demo.Child.Features.Table;

/// <summary>
/// table feature 的子进程视图模型:持有 DataGrid 绑定的行集合 +"按值排序"命令(调用
/// TableService.Sort,在 value 升/降序间切换)。<see cref="Dispatch"/> 做 oneof 三路
/// 分派——Reply/Control 两路直接落到基类,Delta 一路自己 Dispatcher.BeginInvoke 再调
/// OnData 应用 upserts/removed_ids,并按 ordered_ids 把可见集合重新排一遍(排序状态在
/// 主进程侧维护,这里只是照着排)。
/// </summary>
public sealed class TableViewModel : FeatureViewModel<TableDown>
{
    private readonly TableService.TableServiceClient _client;
    private bool _sortAscending = true;

    public ObservableCollection<RowVM> Rows { get; } = new();

    public TableViewModel(ChildShell shell, TableService.TableServiceClient client,
        Grpc.Core.AsyncServerStreamingCall<TableDown> call) : base(shell, call)
    {
        _client = client;
    }

    public async Task ToggleSortAsync()
    {
        // 每次点击在升/降序间切换,方便肉眼/UIA 都能确认"点了确实变了"。
        _sortAscending = !_sortAscending;
        try
        {
            await _client.SortAsync(new SortRequest
            {
                SessionId = Shell.SessionId,
                Field = "value",
                Ascending = _sortAscending,
            }, deadline: DateTime.UtcNow.AddSeconds(5));
        }
        catch { /* 主进程不可达时忽略,不影响窗口自身交互 */ }
    }

    protected override void Dispatch(TableDown env)
    {
        switch (env.KindCase)
        {
            case TableDown.KindOneofCase.Reply:
                OnReply(env.Reply);
                break;
            case TableDown.KindOneofCase.Control:
                HandleControl(env.Control);
                break;
            case TableDown.KindOneofCase.Delta:
                Shell.Window.Dispatcher.BeginInvoke(() => OnData(env));
                break;
        }
    }

    protected override void OnData(TableDown data) => ApplyDelta(data.Delta);

    private void ApplyDelta(TableDelta delta)
    {
        foreach (var id in delta.RemovedIds)
        {
            var existing = Rows.FirstOrDefault(r => r.Id == id);
            if (existing is not null) Rows.Remove(existing);
        }

        foreach (var row in delta.Upserts)
        {
            var existing = Rows.FirstOrDefault(r => r.Id == row.Id);
            if (existing is not null)
            {
                existing.Name = row.Name;
                existing.Value = row.Value;
                existing.Status = row.Status;
            }
            else
            {
                Rows.Add(new RowVM(row.Id, row.Name, row.Value, row.Status));
            }
        }

        for (int i = 0; i < delta.OrderedIds.Count; i++)
        {
            var vm = Rows.FirstOrDefault(r => r.Id == delta.OrderedIds[i]);
            if (vm is null) continue;
            int cur = Rows.IndexOf(vm);
            if (cur != i) Rows.Move(cur, i);
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
