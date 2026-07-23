using System.IO;
using System.Threading.Channels;
using Grpc.Core;

namespace WpfMultiProcess.Host.Session;

/// <summary>
/// 可选的静态助手:绝大多数 <see cref="Session.ServeAsync{T}"/> 实现,本质上都是
/// "把自己内部 Channel 里攒的 envelope 原样转发进 gRPC 的 IServerStreamWriter,直到
/// 取消或连接断开"——这段样板抽到这里,免得每个 feature 的 Session 子类都要重复写
/// 同一段 try/catch。纯粹是省事,不是必须:Session 子类完全可以不用它,自己实现
/// ServeAsync(比如需要在写出前做额外的节流/合帧)。
/// </summary>
public static class SessionPump
{
    /// <summary>把 <paramref name="reader"/> 里的 envelope 逐个写进
    /// <paramref name="writer"/>,直到 <paramref name="ct"/> 被取消、reader 完成,
    /// 或连接断开(IOException)。三种情况都是正常的会话结束路径,吞掉不向上抛。</summary>
    public static async Task PumpAsync<T>(ChannelReader<T> reader, IServerStreamWriter<T> writer, CancellationToken ct)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(ct))
                await writer.WriteAsync(item, ct);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}
