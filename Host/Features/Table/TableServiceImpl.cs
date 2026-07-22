using System.Collections.Concurrent;
using System.IO;
using Grpc.Core;
using WpfMultiProcess.Host.Session;
using WpfMultiProcess.Ipc.Common;
using WpfMultiProcess.Ipc.Table;

namespace WpfMultiProcess.Host.Features.Table;

/// <summary>
/// table feature 专属 gRPC service:会话建立(StreamRequest 带 session_id/hwnd/pid,
/// 校验通过后先下发一条 Reply)+ 数据流(Control+TableDelta 共用一个 envelope)+
/// 专属 unary Sort。每个会话维护一份行集合,50ms 一帧全量下发 upserts/removed_ids,
/// 并按当前排序状态算一份 ordered_ids —— 排序状态本身由 Sort 更新,下一帧立即生效,
/// 子进程侧不需要自己再排一遍。
/// </summary>
public sealed class TableServiceImpl(SessionHub hub) : TableService.TableServiceBase
{
    private sealed class RowState
    {
        public required long Id { get; init; }
        public required string Name { get; set; }
        public double Value { get; set; }
        public string Status { get; set; } = "";
    }

    private sealed class Producer
    {
        public readonly CancellationTokenSource Cts = new();
        public readonly Lock Lock = new();
        public readonly Dictionary<long, RowState> Rows = new();
        public string SortField = "id";
        public bool Ascending = true;
        public long NextId = 1;
        public long Seq;
    }

    private readonly ConcurrentDictionary<string, Producer> _producers = new();
    private static readonly string[] StatusPool = ["ok", "warn", "error", "idle"];

    public override async Task Register(StreamRequest request,
        IServerStreamWriter<TableDown> down, ServerCallContext context)
    {
        // 会话建立现在并进这次开流请求:未知的 session_id 或 feature 对不上一律拒绝。
        if (!hub.TryOpen(request.SessionId, "table", request.Pid, (nint)request.Hwnd))
            return;

        await down.WriteAsync(new TableDown { Reply = hub.ReplyOf("table") }, context.CancellationToken);

        var sub = new Subscription<TableDown>(request.SessionId, "table",
            control => new TableDown { Control = control });
        hub.AttachStream(sub);

        var producer = new Producer();
        var rand = new Random();
        for (int i = 0; i < 8; i++)
            AddRow(producer, rand);
        _producers[request.SessionId] = producer;
        var produceTask = ProduceAsync(sub, producer, producer.Cts.Token);

        try
        {
            await foreach (var env in sub.Reader.ReadAllAsync(context.CancellationToken))
                await down.WriteAsync(env, context.CancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            producer.Cts.Cancel();
            try { await produceTask; } catch { /* 已取消 */ }
            _producers.TryRemove(request.SessionId, out _);
            hub.DetachStream(sub);
        }
    }

    private static void AddRow(Producer p, Random rand)
    {
        long id = p.NextId++;
        p.Rows[id] = new RowState
        {
            Id = id,
            Name = $"row-{id}",
            Value = Math.Round(rand.NextDouble() * 100, 2),
            Status = StatusPool[rand.Next(StatusPool.Length)],
        };
    }

    private static async Task ProduceAsync(Subscription<TableDown> sub, Producer p, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var rand = new Random();
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                TableDown env;
                lock (p.Lock)
                {
                    p.Seq++;

                    // 每帧小概率改动已有行的 value/status,制造"活的"数据流
                    foreach (var row in p.Rows.Values)
                    {
                        if (rand.NextDouble() < 0.3)
                        {
                            row.Value = Math.Round(row.Value + (rand.NextDouble() - 0.5) * 5, 2);
                            row.Status = StatusPool[rand.Next(StatusPool.Length)];
                        }
                    }

                    long? removedId = null;
                    if (rand.NextDouble() < 0.03 && p.Rows.Count < 20)
                    {
                        AddRow(p, rand);
                    }
                    else if (rand.NextDouble() < 0.02 && p.Rows.Count > 4)
                    {
                        removedId = p.Rows.Keys.First();
                        p.Rows.Remove(removedId.Value);
                    }

                    var delta = new TableDelta { Seq = p.Seq };
                    foreach (var row in p.Rows.Values)
                        delta.Upserts.Add(new Row { Id = row.Id, Name = row.Name, Value = row.Value, Status = row.Status });
                    if (removedId is not null)
                        delta.RemovedIds.Add(removedId.Value);
                    delta.OrderedIds.AddRange(OrderedIds(p));

                    env = new TableDown { Delta = delta };
                }
                sub.WriteData(env);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>调用方已持有 p.Lock。</summary>
    private static IEnumerable<long> OrderedIds(Producer p)
    {
        IEnumerable<RowState> query = p.SortField switch
        {
            "value" => p.Ascending
                ? p.Rows.Values.OrderBy(r => r.Value)
                : p.Rows.Values.OrderByDescending(r => r.Value),
            "name" => p.Ascending
                ? p.Rows.Values.OrderBy(r => r.Name, StringComparer.Ordinal)
                : p.Rows.Values.OrderByDescending(r => r.Name, StringComparer.Ordinal),
            "status" => p.Ascending
                ? p.Rows.Values.OrderBy(r => r.Status, StringComparer.Ordinal)
                : p.Rows.Values.OrderByDescending(r => r.Status, StringComparer.Ordinal),
            _ => p.Ascending
                ? p.Rows.Values.OrderBy(r => r.Id)
                : p.Rows.Values.OrderByDescending(r => r.Id),
        };
        return query.Select(r => r.Id).ToList();
    }

    public override Task<Ack> Sort(SortRequest request, ServerCallContext context)
    {
        if (_producers.TryGetValue(request.SessionId, out var p))
        {
            lock (p.Lock)
            {
                p.SortField = request.Field;
                p.Ascending = request.Ascending;
            }
        }
        hub.LogEvent("table", $"Sort by {request.Field} {(request.Ascending ? "asc" : "desc")}");
        return Task.FromResult(new Ack { Ok = true });
    }
}
