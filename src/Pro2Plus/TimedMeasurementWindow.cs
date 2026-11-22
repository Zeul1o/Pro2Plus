using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

/// <summary>
/// 按采集的单调时钟闭合测量窗口，而不是按定时器请求的延时闭合：定时器可能提前完成，
/// 只有真实流逝的采集时间才算证据。
/// </summary>
public static class TimedMeasurementWindow
{
    public static Task WaitUntilElapsedAsync(long startedAtTicks, TimeSpan minimumDuration,
        CancellationToken token) =>
        WaitUntilElapsedAsync(minimumDuration, () => Stopwatch.GetElapsedTime(startedAtTicks),
            (duration, cancellation) => Task.Delay(duration, cancellation), token);

    /// <summary>
    /// 可注入的单调时钟与协作式延时，供离线边界用例验证；时钟必须与被测采集同源。
    /// </summary>
    public static async Task WaitUntilElapsedAsync(TimeSpan minimumDuration, Func<TimeSpan> elapsed,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token)
    {
        if (minimumDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumDuration), "A measurement duration must be positive.");
        ArgumentNullException.ThrowIfNull(elapsed);
        ArgumentNullException.ThrowIfNull(delay);

        TimeSpan previousElapsed = TimeSpan.Zero;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            TimeSpan actualElapsed = elapsed();
            if (actualElapsed < previousElapsed)
                throw new InvalidOperationException("The measurement elapsed clock must be nonnegative and monotonic.");
            previousElapsed = actualElapsed;
            if (actualElapsed >= minimumDuration) return;

            // 任何正的余量都向上取整到毫秒（含亚毫秒尾巴），至少 1ms 防止空转；
            // 分段等待同时保证取消响应，且不允许把请求延时本身当作证据。
            long remainingTicks = (minimumDuration - actualElapsed).Ticks;
            long milliseconds = remainingTicks / TimeSpan.TicksPerMillisecond;
            if (remainingTicks % TimeSpan.TicksPerMillisecond != 0) milliseconds++;
            milliseconds = Math.Clamp(milliseconds, 1, 1000);
            await delay(TimeSpan.FromMilliseconds(milliseconds), token).ConfigureAwait(false);
        }
    }
}
