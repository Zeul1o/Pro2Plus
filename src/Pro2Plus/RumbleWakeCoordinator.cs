using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

/// <summary>
/// 让「源信号」与「精确到期定时器」竞速，赢家返回前必须取消并汇合输家。
/// 被遗弃的信号等待者可能吞掉下一条源信号、把整条流卡死，因此两个操作都不能留在后台。
/// </summary>
public static class RumbleWakeCoordinator
{
    public static async Task WaitAsync(SemaphoreSlim sourceSignal, double dueMilliseconds,
        NativeRumblePacer pacer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (double.IsPositiveInfinity(dueMilliseconds))
        {
            await sourceSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!double.IsFinite(dueMilliseconds) || dueMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(dueMilliseconds));
        if (dueMilliseconds == 0) return;
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task signal = sourceSignal.WaitAsync(turn.Token);
        Task timer = pacer.DelayAsync(TimeSpan.FromMilliseconds(dueMilliseconds), turn.Token);
        try
        {
            Task winner = await Task.WhenAny(signal, timer).ConfigureAwait(false);
            await winner.ConfigureAwait(false);
        }
        finally
        {
            turn.Cancel();
            try { await Task.WhenAll(signal, timer).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
