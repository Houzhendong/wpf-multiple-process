using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Table;

namespace WpfMultiProcess.Demo.Host.Features.Table;

/// <summary>table 会话的宿主侧状态:行集合 + 排序状态 + 50ms 一帧的 producer,原来挂在
/// TableServiceImpl 内部的 per-session Producer,现在下沉成 Session 自己的状态——
/// Sort unary(见 TableServiceImpl.Sort)经 SessionManager.FindSession 直接改这里的
/// 排序状态,下一帧 ProduceAsync 立即按新排序算 ordered_ids,子进程侧不需要再排一遍。</summary>
public sealed class TableSession : Session<TableDown>
{
    private sealed class RowState
    {
        public required long Id { get; init; }
        public required string Name { get; set; }
        public double Value { get; set; }
        public string Status { get; set; } = "";
    }

    private static readonly string[] StatusPool = ["ok", "warn", "error", "idle"];

    private readonly Lock _lock = new();
    private readonly Dictionary<long, RowState> _rows = new();
    private string _sortField = "id";
    private bool _ascending = true;
    private long _nextId = 1;
    private long _seq;

    private CancellationTokenSource? _produceCts;
    private Task? _produceTask;

    public TableSession(string sessionId, string featureId, int featureIndex)
        : base(sessionId, featureId, featureIndex, control => new TableDown { Control = control })
    {
    }

    // 跨程序集 override "protected internal" 成员时,C# 只允许声明为 "protected"
    // (internal 那部分在别的程序集里本来就无意义/无法表达)。
    protected override void OnConnected(nint hwnd)
    {
        var rand = new Random();
        lock (_lock)
        {
            for (int i = 0; i < 8; i++) AddRow(rand);
        }
        _produceCts = new CancellationTokenSource();
        _produceTask = ProduceAsync(_produceCts.Token);
    }

    protected override void OnDisconnected() => _produceCts?.Cancel();

    private void AddRow(Random rand)
    {
        long id = _nextId++;
        _rows[id] = new RowState
        {
            Id = id,
            Name = $"row-{id}",
            Value = Math.Round(rand.NextDouble() * 100, 2),
            Status = StatusPool[rand.Next(StatusPool.Length)],
        };
    }

    private async Task ProduceAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var rand = new Random();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                TableDown env;
                lock (_lock)
                {
                    _seq++;

                    // 每帧小概率改动已有行的 value/status,制造"活的"数据流
                    foreach (var row in _rows.Values)
                    {
                        if (rand.NextDouble() < 0.3)
                        {
                            row.Value = Math.Round(row.Value + (rand.NextDouble() - 0.5) * 5, 2);
                            row.Status = StatusPool[rand.Next(StatusPool.Length)];
                        }
                    }

                    long? removedId = null;
                    if (rand.NextDouble() < 0.03 && _rows.Count < 20)
                    {
                        AddRow(rand);
                    }
                    else if (rand.NextDouble() < 0.02 && _rows.Count > 4)
                    {
                        removedId = _rows.Keys.First();
                        _rows.Remove(removedId.Value);
                    }

                    var delta = new TableDelta { Seq = _seq };
                    foreach (var row in _rows.Values)
                        delta.Upserts.Add(new Row { Id = row.Id, Name = row.Name, Value = row.Value, Status = row.Status });
                    if (removedId is not null)
                        delta.RemovedIds.Add(removedId.Value);
                    delta.OrderedIds.AddRange(OrderedIds());

                    env = new TableDown { Delta = delta };
                }
                PushData(env);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>调用方已持有 _lock。</summary>
    private IEnumerable<long> OrderedIds()
    {
        IEnumerable<RowState> query = _sortField switch
        {
            "value" => _ascending
                ? _rows.Values.OrderBy(r => r.Value)
                : _rows.Values.OrderByDescending(r => r.Value),
            "name" => _ascending
                ? _rows.Values.OrderBy(r => r.Name, StringComparer.Ordinal)
                : _rows.Values.OrderByDescending(r => r.Name, StringComparer.Ordinal),
            "status" => _ascending
                ? _rows.Values.OrderBy(r => r.Status, StringComparer.Ordinal)
                : _rows.Values.OrderByDescending(r => r.Status, StringComparer.Ordinal),
            _ => _ascending
                ? _rows.Values.OrderBy(r => r.Id)
                : _rows.Values.OrderByDescending(r => r.Id),
        };
        return query.Select(r => r.Id).ToList();
    }

    public void ApplySort(string field, bool ascending)
    {
        lock (_lock)
        {
            _sortField = field;
            _ascending = ascending;
        }
    }
}
