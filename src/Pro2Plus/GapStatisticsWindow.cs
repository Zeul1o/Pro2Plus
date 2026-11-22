using System;

namespace Pro2Plus;

public readonly record struct GapStatisticsSummary(int Samples, double P50Ms, double P95Ms, double P99Ms,
    double MinMs, double MaxMs, int Near7p5, int Near15, int Over22);

/// <summary>
/// 通知间隔的定长环形窗口：热路径无分配，调用方负责同步。
/// 采样计数与写入位置分开维护，分位数只在汇总时排序一次、计算一次。
/// </summary>
public sealed class GapStatisticsWindow
{
    private const int Capacity = 2048;

    private readonly double[] gaps = new double[Capacity];
    private int count, stored, near7p5, near15, over22;
    private double min = double.PositiveInfinity, max;

    public void Record(double gapMs)
    {
        if (!double.IsFinite(gapMs) || gapMs < 0) return;
        gaps[stored++ % Capacity] = gapMs;
        count++;
        min = Math.Min(min, gapMs);
        max = Math.Max(max, gapMs);
        if (Math.Abs(gapMs - 7.5) <= 1.5) near7p5++;
        if (Math.Abs(gapMs - 15) <= 1.5) near15++;
        if (gapMs >= 22) over22++;
    }

    public string SummarizeAndReset() => SummarizeAndReset(out _);

    public string SummarizeAndReset(out GapStatisticsSummary summary)
    {
        int n = Math.Min(stored, Capacity);
        Array.Sort(gaps, 0, n);
        double p50 = Quantile(n, .5);
        double p95 = Quantile(n, .95);
        double p99 = Quantile(n, .99);
        double reportedMin = count == 0 ? 0 : min;
        summary = new GapStatisticsSummary(count, p50, p95, p99, reportedMin, max, near7p5, near15, over22);

        // 预先格式化，整行一次插值成型；旧实现会把三个分位数重复计算并拼接两次。
        string p50Text = p50.ToString("F2");
        string p95Text = p95.ToString("F2");
        string p99Text = p99.ToString("F2");
        string minText = reportedMin.ToString("F2");
        string maxText = max.ToString("F2");
        string line = $"gap_samples={count} gap_p50_ms={p50Text} gap_p95_ms={p95Text} " +
            $"gap_p99_ms={p99Text} gap_min_ms={minText} gap_max_ms={maxText} " +
            $"gap_near7p5={near7p5} gap_near15={near15} gap_ge22={over22}";
        Reset();
        return line;
    }

    private double Quantile(int n, double q) =>
        n == 0 ? 0 : gaps[Math.Clamp((int)Math.Ceiling(n * q) - 1, 0, n - 1)];

    public void Reset()
    {
        count = 0;
        stored = 0;
        near7p5 = 0;
        near15 = 0;
        over22 = 0;
        min = double.PositiveInfinity;
        max = 0;
    }
}
