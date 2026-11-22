using System;
using System.Threading;
using System.Threading.Channels;

namespace Pro2Plus;

/// <summary>一帧原始 BLE 通知。</summary>
internal readonly record struct CaptureFrame(ulong Sequence, DateTimeOffset Timestamp, double DeltaMs, byte[] Raw);

/// <summary>
/// 有界帧队列。写满时挤掉最旧的一帧并计数。
/// 旧实现用 <c>DropWrite</c> 再检查 <c>TryWrite</c> 的返回值——该模式下 TryWrite 永远返回 true，
/// 被丢弃的恰恰是正在写入的那一帧，所以丢帧数始终是 0。这里改成 DropOldest + itemDropped 回调，
/// 计数与真实丢弃一一对应；保留最旧的写法也会让「刚出问题的那一帧」被丢掉。
/// </summary>
internal sealed class CaptureFrameQueue
{
    private readonly Channel<CaptureFrame> channel;
    private long dropped;

    public CaptureFrameQueue(int capacity)
    {
        channel = Channel.CreateBounded<CaptureFrame>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            },
            _ => Interlocked.Increment(ref dropped));
    }

    public ChannelReader<CaptureFrame> Reader => channel.Reader;
    public long Dropped => Interlocked.Read(ref dropped);

    public bool TryWrite(CaptureFrame frame) => channel.Writer.TryWrite(frame);

    public void Complete() => channel.Writer.TryComplete();
}
