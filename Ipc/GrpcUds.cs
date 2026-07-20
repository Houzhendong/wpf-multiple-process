using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using Grpc.Net.Client;

namespace WpfMultiProcess.Ipc;

/// <summary>gRPC over Unix Domain Socket 的客户端通道工厂(Windows 10 1803+ 支持 AF_UNIX)。</summary>
public static class GrpcUds
{
    /// <summary>主进程本次运行使用的套接字文件路径(按 PID 区分,允许多开)。</summary>
    public static string GetSocketPath(int hostPid) =>
        Path.Combine(Path.GetTempPath(), $"wpfmp-{hostPid}.sock");

    public static GrpcChannel CreateChannel(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            // http/2 长连接保活,避免流被闲置回收
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        // 地址仅作占位,实际连接走 ConnectCallback
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
    }
}
