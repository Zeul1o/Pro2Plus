using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Pro2Plus;

/// <summary>FD2 通知间隔的分布统计（毫秒）。</summary>
public sealed record BleGapStatistics(
    int SampleCount,
    double P50Ms,
    double P90Ms,
    double P95Ms,
    double P99Ms,
    double MinMs,
    double MaxMs,
    IReadOnlyDictionary<string, int> Distribution);

/// <summary>
/// 间隔分桶。桶边界按 7.5ms 的整数倍（±3.75ms 窗口）切开，
/// 让「实际采用 7.5 / 15 / 22.5ms 中的哪一个」能直接从分布读出来。
/// </summary>
internal static class BleGapStatisticsBuilder
{
    private const double BucketFloorMs = 5.0;

    // 顺序即日志里的分布顺序，也是判定顺序（区间互不重叠且递增）。
    private static readonly (string Bucket, double UpperBoundMs)[] Buckets =
    [
        ("near_7_5ms", 11.25),
        ("near_15ms", 18.75),
        ("near_22_5ms", 26.25),
        ("near_30ms", 33.75),
        ("near_37_5ms", 41.25),
        ("near_45ms", 47.50),
        ("near_50ms", 55.00),
        ("near_60ms", 65.00)
    ];

    private const string OtherBucket = "other";

    internal static BleGapStatistics Build(long[] ticks)
    {
        var distribution = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string bucket, double _) in Buckets) distribution[bucket] = 0;
        distribution[OtherBucket] = 0;

        if (ticks.Length < 2)
        {
            return new BleGapStatistics(0, 0, 0, 0, 0, 0, 0, distribution);
        }

        // 单遍算间隔，再排序：分位数与分桶都吃这个有序数组，不需要第二条 LINQ 管线。
        double[] gaps = new double[ticks.Length - 1];
        for (int i = 1; i < ticks.Length; i++)
        {
            gaps[i - 1] = (ticks[i] - ticks[i - 1]) * 1000.0 / Stopwatch.Frequency;
        }

        Array.Sort(gaps);
        foreach (double gap in gaps)
        {
            distribution[Classify(gap)]++;
        }

        return new BleGapStatistics(
            gaps.Length,
            Percentile(gaps, 0.50),
            Percentile(gaps, 0.90),
            Percentile(gaps, 0.95),
            Percentile(gaps, 0.99),
            gaps[0],
            gaps[^1],
            distribution);
    }

    private static string Classify(double gapMs)
    {
        if (!(gapMs >= BucketFloorMs)) return OtherBucket; // NaN 也落这里
        foreach ((string bucket, double upperBound) in Buckets)
        {
            if (gapMs < upperBound) return bucket;
        }

        return OtherBucket;
    }

    /// <summary>线性插值分位数，位置口径与旧实现一致：(n-1)*p。</summary>
    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        double weight = position - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }
}
