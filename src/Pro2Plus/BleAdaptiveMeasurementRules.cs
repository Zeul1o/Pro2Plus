using System;

namespace Pro2Plus;

/// <summary>
/// 测量窗口的合法性、比较与「变差 / 变好」判定。全部是纯函数：不读时钟、不碰设备。
/// 阈值集中在这里，公开常量由 <see cref="BleAdaptivePresetPolicy"/> 转发。
/// </summary>
internal static class BleAdaptiveMeasurementRules
{
    internal const double TargetHz = 125;
    internal const double PlayableHz = 60;
    internal const double MinimumWindowSeconds = 10;
    internal const double MaximumWindowSeconds = 30;
    internal const double MaximumWindowAgeSeconds = 15;
    internal const double RateNoiseToleranceHz = 2;

    /// <summary>窗口合法：连接、时长、样本量、解析完整性、输入与窗口新鲜度、间隙单调。</summary>
    internal static bool IsValid(BleAdaptiveMeasurement? value, bool requireRequestSuccess)
    {
        if (value?.Sample == null) return false;
        MeasuredTrialSample sample = value.Sample;
        return sample.Connected && (!requireRequestSuccess || sample.RequestedSuccessfully) &&
            double.IsFinite(sample.DurationSeconds) &&
            sample.DurationSeconds >= MinimumWindowSeconds && sample.DurationSeconds <= MaximumWindowSeconds &&
            sample.Fd2Count >= MeasuredTrialPolicy.MinimumSamples &&
            sample.ParsedCount >= MeasuredTrialPolicy.MinimumSamples &&
            sample.ParsedCount <= sample.Fd2Count &&
            sample.ParsedHz >= sample.Fd2Hz * MeasuredTrialPolicy.MinimumParsedRatio &&
            double.IsFinite(sample.LastInputAgeMs) && sample.LastInputAgeMs >= 0 &&
            sample.LastInputAgeMs < MeasuredTrialPolicy.MaximumInputAgeMs &&
            double.IsFinite(value.CompletedAgeSeconds) && value.CompletedAgeSeconds >= 0 &&
            value.CompletedAgeSeconds <= MaximumWindowAgeSeconds &&
            IsValidGap(sample.P95GapMs) && IsValidGap(value.MaximumGapMs) &&
            (!sample.P95GapMs.HasValue || !value.MaximumGapMs.HasValue ||
             value.MaximumGapMs >= sample.P95GapMs);
    }

    internal static bool MeetsTarget(BleAdaptiveMeasurement? measurement) =>
        IsValid(measurement, requireRequestSuccess: false) &&
        measurement!.Sample.Fd2Hz >= TargetHz && measurement.Sample.ParsedHz >= TargetHz;

    internal static bool IsPlayable(BleAdaptiveMeasurement? measurement) =>
        IsValid(measurement, requireRequestSuccess: false) && measurement!.EffectiveHz >= PlayableHz;

    /// <summary>正数表示左边更优。无效或过期的窗口永远排在实时证据之后。</summary>
    internal static int Compare(BleAdaptiveMeasurement? left, BleAdaptiveMeasurement? right)
    {
        bool validLeft = IsValid(left, requireRequestSuccess: false);
        bool validRight = IsValid(right, requireRequestSuccess: false);
        if (validLeft != validRight) return validLeft ? 1 : -1;
        if (!validLeft) return 0;

        bool targetLeft = MeetsTarget(left);
        bool targetRight = MeetsTarget(right);
        if (targetLeft != targetRight) return targetLeft ? 1 : -1;

        double difference = left!.EffectiveHz - right!.EffectiveHz;
        if (Math.Abs(difference) > RateNoiseToleranceHz) return Math.Sign(difference);
        int gaps = CompareKnownGap(left.Sample.P95GapMs, right.Sample.P95GapMs);
        if (gaps != 0) return gaps;
        gaps = CompareKnownGap(left.MaximumGapMs, right.MaximumGapMs);
        if (gaps != 0) return gaps;
        return Math.Sign(difference);
    }

    /// <summary>候选是否真的优于基准。复核窗口没有新请求，因此不看请求成功标记。</summary>
    internal static bool Outranks(BleAdaptiveMeasurement baseline, BleAdaptiveMeasurement candidate)
    {
        if (!IsValid(baseline, false) || !IsValid(candidate, false)) return false;
        if (!MatchingWindows(baseline, candidate)) return false;
        if (MeetsTarget(baseline)) return false;
        if (Regressed(baseline, candidate) || GapRegressed(baseline, candidate)) return false;
        return Compare(candidate, baseline) > 0;
    }

    internal static bool MatchingWindows(BleAdaptiveMeasurement a, BleAdaptiveMeasurement b) =>
        Math.Abs(a.Sample.DurationSeconds - b.Sample.DurationSeconds) <=
        Math.Min(a.Sample.DurationSeconds, b.Sample.DurationSeconds) *
        MeasuredTrialPolicy.MaximumWindowDifferenceRatio;

    internal static bool Regressed(BleAdaptiveMeasurement before, BleAdaptiveMeasurement after) =>
        after.Sample.Fd2Hz < before.Sample.Fd2Hz - RateNoiseToleranceHz - 1e-9 ||
        after.Sample.ParsedHz < before.Sample.ParsedHz - RateNoiseToleranceHz - 1e-9;

    internal static bool GapRegressed(BleAdaptiveMeasurement before, BleAdaptiveMeasurement after) =>
        IncreasedGap(before.Sample.P95GapMs, after.Sample.P95GapMs, 2, .25) ||
        IncreasedGap(before.MaximumGapMs, after.MaximumGapMs, 15, .5);

    internal static bool GapImproved(BleAdaptiveMeasurement before, BleAdaptiveMeasurement after) =>
        ReducedGap(before.Sample.P95GapMs, after.Sample.P95GapMs, 1, .1) ||
        ReducedGap(before.MaximumGapMs, after.MaximumGapMs, 5, .1);

    private static bool IsValidGap(double? gap) => !gap.HasValue || double.IsFinite(gap.Value) && gap.Value >= 0;

    // 间隙越小越好，所以取反。
    private static int CompareKnownGap(double? left, double? right) =>
        left.HasValue && right.HasValue ? -left.Value.CompareTo(right.Value) : 0;

    private static bool IncreasedGap(double? before, double? after, double absolute, double ratio) =>
        before.HasValue && after.HasValue && after.Value > before.Value + Math.Max(absolute, before.Value * ratio);

    private static bool ReducedGap(double? before, double? after, double absolute, double ratio) =>
        before.HasValue && after.HasValue && after.Value <= before.Value - Math.Max(absolute, before.Value * ratio);
}
