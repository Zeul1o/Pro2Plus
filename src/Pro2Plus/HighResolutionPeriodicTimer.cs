using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Pro2Plus;

/// <summary>
/// 周期唤醒：绝对截止时刻 + 可等待定时器，定时器不可用时退回 Stopwatch（睡眠 + 自旋）。
/// 到点即返回；调用方的处理耗时从下一拍里扣掉，不累积漂移。落后时整拍跳过，不补发。
/// </summary>
public sealed class HighResolutionPeriodicTimer : IDisposable
{
    private readonly Stopwatch scheduleWatch = Stopwatch.StartNew();
    private readonly WaitableTimer? native;
    private PeriodicSchedule schedule;
    private volatile bool disposed;

    public HighResolutionPeriodicTimer(TimeSpan period)
    {
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));

        schedule = new PeriodicSchedule(ToStopwatchTicks(period));
        WaitableTimer? created = WaitableTimer.TryCreate();
        if (created != null)
        {
            // 构造期就上弦：第一次等待不该走「立即到点」的快路径。
            try
            {
                created.Arm(schedule.Remaining(scheduleWatch.ElapsedTicks), Stopwatch.Frequency);
            }
            catch
            {
                created.Dispose();
                throw;
            }
        }

        native = created;
        Backend = created == null ? "stopwatch_fallback" : "high_resolution_waitable_timer_absolute";
    }

    public string Backend { get; }

    internal long IntervalTicks => schedule.IntervalTicks;

    public bool WaitForNextTick(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (native == null)
        {
            WaitOnStopwatch(cancellationToken);
            return true;
        }

        uint result = native.WaitOnce(1000);
        cancellationToken.ThrowIfCancellationRequested();
        if (result == WaitableTimer.Signaled)
        {
            schedule.Advance(scheduleWatch.ElapsedTicks);
            native.Arm(schedule.Remaining(scheduleWatch.ElapsedTicks), Stopwatch.Frequency);
            return true;
        }

        if (result == WaitableTimer.Failed)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "High-resolution timer wait failed.");

        throw new TimeoutException("High-resolution timer did not signal within one second.");
    }

    private void WaitOnStopwatch(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remaining = schedule.NextTicks - scheduleWatch.ElapsedTicks;
            if (remaining <= 0)
            {
                schedule.Advance(scheduleWatch.ElapsedTicks);
                return;
            }

            // 远的时候睡 1ms（让出 CPU），最后 2ms 自旋把抖动压到微秒级。
            if (remaining * 1000.0 / Stopwatch.Frequency > 2) Thread.Sleep(1);
            else Thread.SpinWait(80);
        }
    }

    private static long ToStopwatchTicks(TimeSpan value) =>
        Math.Max(1, checked((long)Math.Round(value.TotalSeconds * Stopwatch.Frequency)));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        native?.Dispose();
    }
}
