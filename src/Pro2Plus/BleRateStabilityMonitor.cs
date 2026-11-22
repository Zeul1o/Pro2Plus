using System;

namespace Pro2Plus;

public enum BleRateStabilityState { Observing, StableHighRate, LowRate, Recovering, Stale }

// These describe Windows notification delivery, not a proven connection interval,
// RF fault, driver fault or cause of a low input rate.
public enum BleNotifyCadenceHint { Unknown, Predominantly15Ms, MixedShortAndLong, Irregular }

public sealed record BleRateStabilityOptions
{
    // A measured 125 Hz stream has window/scheduling tolerance. This health
    // threshold is independent of gameplay (60 Hz) and matches preflight
    // acceptance (SoftwareOnlyMode.MinimumAcceptedHz).
    public double HighRateThresholdHz { get; init; } = 125;
    public int GoodWindowsRequired { get; init; } = 3;
    public int LowWindowsRequired { get; init; } = 2;
    public double MinimumWindowSeconds { get; init; } = 4;
    public double MaximumWindowSeconds { get; init; } = 10;
    public double RecentWindowSeconds { get; init; } = 15;
    public double MaximumContinuityGapSeconds { get; init; } = 1;

    /// <summary>窗口策略是否自洽：门槛为正、连续计数至少 2、窗口区间有序。</summary>
    public bool IsUsable =>
        double.IsFinite(HighRateThresholdHz) && HighRateThresholdHz > 0 &&
        GoodWindowsRequired >= 2 && LowWindowsRequired >= 2 &&
        double.IsFinite(MinimumWindowSeconds) && MinimumWindowSeconds > 0 &&
        double.IsFinite(MaximumWindowSeconds) && MaximumWindowSeconds >= MinimumWindowSeconds &&
        double.IsFinite(RecentWindowSeconds) && RecentWindowSeconds >= MaximumWindowSeconds &&
        double.IsFinite(MaximumContinuityGapSeconds) && MaximumContinuityGapSeconds >= 0;
}

/// <summary>Counts are deltas for this exact completed window, never session totals.</summary>
public sealed record BleRateMeasurementWindow(long WindowStartTicks, long WindowEndTicks,
    ulong RawNotifications, ulong ParsedInputs, GapStatisticsSummary Gaps = default);

public sealed record BleRateStabilityWindow(long WindowStartTicks, long WindowEndTicks,
    ulong RawNotifications, ulong ParsedInputs, double Seconds, double RawHz,
    double ParsedHz, GapStatisticsSummary Gaps, BleNotifyCadenceHint CadenceHint)
{
    public double EffectiveHz => Math.Min(RawHz, ParsedHz);
}

public sealed record BleRateStabilitySnapshot(BleRateStabilityState State, bool IsRecent,
    bool IsStableHighRate, bool ShouldWarnLowRate, int ConsecutiveGoodWindows,
    int ConsecutiveLowWindows, long ValidWindowCount, long LowWindowCount,
    BleRateStabilityWindow? LastWindow, BleRateStabilityWindow? RecentLowWindow,
    double? SessionMinimumHz, double? SessionMaximumHz, double? SessionMinimumRawHz,
    double? SessionMaximumRawHz, double? SessionMinimumParsedHz, double? SessionMaximumParsedHz,
    double HighRateThresholdHz, double ValidMeasuredSeconds)
{
    public bool HasValidWindow => LastWindow != null;
    public BleNotifyCadenceHint CadenceHint => LastWindow?.CadenceHint ?? BleNotifyCadenceHint.Unknown;
    public bool CauseProven => false;
}

/// <summary>
/// Pure, bounded session monitor. The caller owns synchronization and supplies
/// monotonic timestamps. No timer, wall clock, transport, process or system I/O.
/// Reset when disconnecting, changing physical connection or clearing counters.
/// </summary>
public sealed class BleRateStabilityMonitor
{
    private readonly long timestampFrequency;
    private readonly BleRateStabilityOptions options;
    private BleRateStabilityWindow? lastWindow, recentLowWindow;
    private int consecutiveGood, consecutiveLow;
    private long validWindows, lowWindows;
    private bool lowRateConfirmed;
    private RateRange effectiveRange, rawRange, parsedRange;
    private double measuredSeconds;

    public BleRateStabilityMonitor(long timestampFrequency, BleRateStabilityOptions? options = null)
    {
        if (timestampFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        this.timestampFrequency = timestampFrequency;
        this.options = options ?? new();
        if (!this.options.IsUsable) throw new ArgumentOutOfRangeException(nameof(options), "Invalid stability window policy.");
    }

    /// <summary>
    /// Accepts an independent completed window once. Invalid, duplicate and
    /// overlapping windows return false without changing session evidence.
    /// Zero counts in a valid completed window are real low-rate evidence.
    /// </summary>
    public bool ObserveWindow(BleRateMeasurementWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!TryMeasure(window, out WindowMeasurement measurement)) return false;

        // A gap in observations cannot be used to join old and new good/low
        // streaks. An existing confirmed warning still requires good recovery.
        if (lastWindow != null &&
            GapSeconds(window.WindowStartTicks - lastWindow.WindowEndTicks) > options.MaximumContinuityGapSeconds)
        {
            consecutiveGood = consecutiveLow = 0;
        }

        lastWindow = new(window.WindowStartTicks, window.WindowEndTicks, window.RawNotifications,
            window.ParsedInputs, measurement.Seconds, measurement.RawHz, measurement.ParsedHz,
            window.Gaps, ClassifyCadence(window.Gaps));
        validWindows = Increment(validWindows);
        measuredSeconds += measurement.Seconds;
        effectiveRange.Note(lastWindow.EffectiveHz);
        rawRange.Note(measurement.RawHz);
        parsedRange.Note(measurement.ParsedHz);

        if (lastWindow.EffectiveHz >= options.HighRateThresholdHz)
        {
            consecutiveLow = 0;
            consecutiveGood = Increment(consecutiveGood);
            if (consecutiveGood >= options.GoodWindowsRequired) lowRateConfirmed = false;
        }
        else
        {
            consecutiveGood = 0;
            consecutiveLow = Increment(consecutiveLow);
            lowWindows = Increment(lowWindows);
            recentLowWindow = lastWindow;
            if (consecutiveLow >= options.LowWindowsRequired) lowRateConfirmed = true;
        }

        return true;
    }

    /// <summary>窗口本身的合法性：时间有序、计数自洽、不重叠、长度在策略区间内。</summary>
    private bool TryMeasure(BleRateMeasurementWindow window, out WindowMeasurement measurement)
    {
        measurement = default;
        if (window.WindowStartTicks < 0 || window.WindowEndTicks <= window.WindowStartTicks ||
            window.ParsedInputs > window.RawNotifications) return false;
        if (lastWindow != null && window.WindowStartTicks < lastWindow.WindowEndTicks) return false;

        double seconds = (window.WindowEndTicks - window.WindowStartTicks) / (double)timestampFrequency;
        if (!double.IsFinite(seconds) || seconds < options.MinimumWindowSeconds ||
            seconds > options.MaximumWindowSeconds) return false;

        double rawHz = window.RawNotifications / seconds;
        double parsedHz = window.ParsedInputs / seconds;
        if (!double.IsFinite(rawHz) || !double.IsFinite(parsedHz)) return false;
        measurement = new(seconds, rawHz, parsedHz);
        return true;
    }

    /// <summary>A read-only snapshot; repeated UI reads never extend a streak.</summary>
    public BleRateStabilitySnapshot Snapshot(long nowTicks)
    {
        bool recent = lastWindow != null && nowTicks >= lastWindow.WindowEndTicks &&
            GapSeconds(nowTicks - lastWindow.WindowEndTicks) <= options.RecentWindowSeconds;
        bool stable = recent && !lowRateConfirmed && consecutiveGood >= options.GoodWindowsRequired;
        BleRateStabilityState state = lastWindow == null ? BleRateStabilityState.Observing :
            !recent ? BleRateStabilityState.Stale :
            lowRateConfirmed ? (consecutiveGood > 0 ? BleRateStabilityState.Recovering : BleRateStabilityState.LowRate) :
            stable ? BleRateStabilityState.StableHighRate : BleRateStabilityState.Observing;
        return new(state, recent, stable, recent && lowRateConfirmed, consecutiveGood, consecutiveLow,
            validWindows, lowWindows, lastWindow, recentLowWindow,
            effectiveRange.Min, effectiveRange.Max, rawRange.Min, rawRange.Max, parsedRange.Min, parsedRange.Max,
            options.HighRateThresholdHz, measuredSeconds);
    }

    public void Reset()
    {
        lastWindow = recentLowWindow = null;
        consecutiveGood = consecutiveLow = 0;
        validWindows = lowWindows = 0;
        lowRateConfirmed = false;
        effectiveRange = rawRange = parsedRange = default;
        measuredSeconds = 0;
    }

    public static BleNotifyCadenceHint ClassifyCadence(GapStatisticsSummary gaps)
    {
        if (gaps.Samples < 50 || gaps.Near7p5 < 0 || gaps.Near15 < 0 || gaps.Over22 < 0 ||
            (long)gaps.Near7p5 + gaps.Near15 + gaps.Over22 > gaps.Samples ||
            !double.IsFinite(gaps.P50Ms) || !double.IsFinite(gaps.P95Ms) ||
            !double.IsFinite(gaps.P99Ms) || !double.IsFinite(gaps.MinMs) || !double.IsFinite(gaps.MaxMs) ||
            gaps.MinMs < 0 || gaps.P50Ms < gaps.MinMs || gaps.P95Ms < gaps.P50Ms ||
            gaps.P99Ms < gaps.P95Ms || gaps.MaxMs < gaps.P99Ms) return BleNotifyCadenceHint.Unknown;
        if (gaps.Near7p5 >= gaps.Samples * .2 && gaps.Over22 >= gaps.Samples * .1)
            return BleNotifyCadenceHint.MixedShortAndLong;
        if (gaps.Near15 >= gaps.Samples * .8 && gaps.Near7p5 < gaps.Samples * .1 &&
            gaps.Over22 < gaps.Samples * .1 && gaps.P50Ms is >= 13.5 and <= 16.5 && gaps.P95Ms <= 18)
            return BleNotifyCadenceHint.Predominantly15Ms;
        return BleNotifyCadenceHint.Irregular;
    }

    private double GapSeconds(long ticks) => ticks / (double)timestampFrequency;

    private static long Increment(long value) => value == long.MaxValue ? value : value + 1;
    private static int Increment(int value) => value == int.MaxValue ? value : value + 1;

    private readonly record struct WindowMeasurement(double Seconds, double RawHz, double ParsedHz);

    /// <summary>会话内的最小/最大值对；两个值总是同步更新，单独存会出现只更新一半的状态。</summary>
    private struct RateRange
    {
        private double? minimum, maximum;

        public readonly double? Min => minimum;
        public readonly double? Max => maximum;

        public void Note(double value)
        {
            minimum = minimum.HasValue ? Math.Min(minimum.Value, value) : value;
            maximum = maximum.HasValue ? Math.Max(maximum.Value, value) : value;
        }
    }
}
