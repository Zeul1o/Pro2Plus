using System;

namespace Pro2Plus;

/// <summary>
/// 周期任务的绝对截止时刻（Stopwatch tick 计）。到点只前进一拍；已经落后超过一整拍时
/// 整拍跳过、不做补偿性突发，避免线程被挂起后一次性补发几十次回调。
/// </summary>
internal struct PeriodicSchedule
{
    private readonly long intervalTicks;

    public PeriodicSchedule(long intervalTicks)
    {
        if (intervalTicks <= 0) throw new ArgumentOutOfRangeException(nameof(intervalTicks));
        this.intervalTicks = intervalTicks;
        NextTicks = intervalTicks;
    }

    public long NextTicks { get; private set; }

    public long IntervalTicks => intervalTicks;

    /// <summary>跨过当前这一拍，返回因此跳过的整拍数（0 表示只前进一拍）。</summary>
    public long Advance(long nowTicks)
    {
        NextTicks += intervalTicks;
        if (NextTicks > nowTicks) return 0;
        long missed = ((nowTicks - NextTicks) / intervalTicks) + 1;
        NextTicks += missed * intervalTicks;
        return missed;
    }

    /// <summary>距下一次到点还有多少 tick；至少返回 1，供定时器上弦使用。</summary>
    public long Remaining(long nowTicks) => Math.Max(1, NextTicks - nowTicks);
}
