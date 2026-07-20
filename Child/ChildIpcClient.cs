using Grpc.Core;
using Grpc.Net.Client;
using WpfMultiProcess.Ipc;

namespace WpfMultiProcess.Child;

/// <summary>
/// 子进程侧 IPC 客户端:一条 server stream 收 Feature/Ping/Shutdown,
/// unary 调用发 InitParams / Interaction / RegisterWindow / Pong。
/// 所有事件都在后台线程触发,UI 侧自行 Dispatcher 调度。
/// </summary>
public sealed class ChildIpcClient : IAsyncDisposable
{
    private readonly string _featureId;
    private readonly GrpcChannel _channel;
    private readonly HostIpc.HostIpcClient _client;
    private readonly CancellationTokenSource _cts = new();
    private Task? _streamTask;

    public event Action<FeatureData>? FeatureReceived;
    public event Action<Ping>? PingReceived;
    public event Action? ShutdownRequested;
    public event Action<Exception>? StreamFaulted;

    public ChildIpcClient(string featureId, string socketPath)
    {
        _featureId = featureId;
        _channel = GrpcUds.CreateChannel(socketPath);
        _client = new HostIpc.HostIpcClient(_channel);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Task<InitParamsReply> GetInitParamsAsync() =>
        _client.GetInitParamsAsync(new InitParamsRequest { FeatureId = _featureId },
            deadline: DateTime.UtcNow.AddSeconds(10)).ResponseAsync;

    public Task RegisterWindowAsync(nint hwnd) =>
        _client.RegisterWindowAsync(new RegisterWindowRequest
        {
            FeatureId = _featureId,
            Hwnd = hwnd,
            Pid = Environment.ProcessId,
        }).ResponseAsync;

    public Task ReportInteractionAsync(string action, string payloadJson) =>
        _client.ReportInteractionAsync(new InteractionRequest
        {
            FeatureId = _featureId,
            Action = action,
            PayloadJson = payloadJson,
            TimestampMs = NowMs(),
        }).ResponseAsync;

    /// <summary>在 UI 线程收到 Ping 后调用;fire-and-forget 上传 Pong。</summary>
    public void SendPong(Ping ping)
    {
        _ = _client.PongAsync(new PongRequest
        {
            FeatureId = _featureId,
            Sequence = ping.Sequence,
            PingTimestampMs = ping.TimestampMs,
            PongTimestampMs = NowMs(),
        }).ResponseAsync;
    }

    public void StartSubscription()
    {
        _streamTask = Task.Run(async () =>
        {
            try
            {
                using var call = _client.Subscribe(new SubscribeRequest
                {
                    FeatureId = _featureId,
                    Pid = Environment.ProcessId,
                }, cancellationToken: _cts.Token);

                await foreach (var msg in call.ResponseStream.ReadAllAsync(_cts.Token))
                {
                    switch (msg.PayloadCase)
                    {
                        case ServerMessage.PayloadOneofCase.Feature:
                            FeatureReceived?.Invoke(msg.Feature);
                            break;
                        case ServerMessage.PayloadOneofCase.Ping:
                            PingReceived?.Invoke(msg.Ping);
                            break;
                        case ServerMessage.PayloadOneofCase.Shutdown:
                            ShutdownRequested?.Invoke();
                            return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StreamFaulted?.Invoke(ex);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_streamTask is not null)
        {
            try { await _streamTask; } catch { }
        }
        _channel.Dispose();
        _cts.Dispose();
    }
}
