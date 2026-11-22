using System;
using System.Diagnostics;
using System.Globalization;

namespace Pro2Plus;

/// <summary>
/// 蓝牙震动写入节奏的每秒采样。只做整数计数与定长环形缓冲，每秒排序一次，
/// 热路径不产生托管分配。输出必须带 [RAW] 前缀，否则会被连接诊断日志过滤掉，
/// 之前几轮排查只能靠累计计数反推节奏，就是踩了这个坑。
/// </summary>
public sealed class RumbleWritePacingProbe
{
    private const int Capacity = 128;
    private readonly double[] intervals = new double[Capacity];
    private readonly double[] gattDurations = new double[Capacity];
    private readonly double[] sortBuffer = new double[Capacity];
    private int intervalCount;
    private int intervalHead;
    private int gattCount;
    private int gattHead;
    private long lastWriteTicks;
    private long windowTicks;
    private bool started;
    private int sourceWrites;
    private int refreshWrites;
    private int otherWrites;
    private int maxQueueDepth;
    private double maxIntervalMs;
    private double maxGattMs;

    public void Reset()
    {
        intervalCount = intervalHead = gattCount = gattHead = 0;
        sourceWrites = refreshWrites = otherWrites = maxQueueDepth = 0;
        maxIntervalMs = maxGattMs = 0;
        lastWriteTicks = 0;
        started = false;
    }

    /// <summary>每次 GATT 写入完成后调用；跨过 1 秒窗口时返回一行日志。</summary>
    public string? ObserveWrite(long writeStarted, long completed, string generatedKind,
        int queueDepth, long generated, long cancelled, bool holdActive, bool steady)
    {
        double toMs = 1000.0 / Stopwatch.Frequency;
        if (!started)
        {
            started = true;
            windowTicks = writeStarted;
        }

        if (lastWriteTicks != 0)
        {
            double gap = (writeStarted - lastWriteTicks) * toMs;
            if (gap >= 0)
            {
                intervals[intervalHead] = gap;
                intervalHead = (intervalHead + 1) % Capacity;
                intervalCount = Math.Min(intervalCount + 1, Capacity);
                if (gap > maxIntervalMs) maxIntervalMs = gap;
            }
        }
        lastWriteTicks = writeStarted;

        double duration = (completed - writeStarted) * toMs;
        gattDurations[gattHead] = duration;
        gattHead = (gattHead + 1) % Capacity;
        gattCount = Math.Min(gattCount + 1, Capacity);
        if (duration > maxGattMs) maxGattMs = duration;

        if (generatedKind == "source") sourceWrites++;
        else if (generatedKind == "refresh") refreshWrites++;
        else otherWrites++;
        if (queueDepth > maxQueueDepth) maxQueueDepth = queueDepth;

        double windowMs = (writeStarted - windowTicks) * toMs;
        if (windowMs < 1000)
        {
            return null;
        }

        // 诊断日志的 IsConnectionLogLine 只放行 [BLE / [USB / [LINK 等前缀，
        // 由 ConnectionDiag.Raw 统一补 [RAW] 标记；这里自带 [RAW] 会被整行丢掉。
        string report = "[BLE_RUMBLE_PACE] window_ms=" + windowMs.ToString("F0", CultureInfo.InvariantCulture) +
            " writes=" + (sourceWrites + refreshWrites + otherWrites) +
            " source=" + sourceWrites + " refresh=" + refreshWrites + " other=" + otherWrites +
            " interval_ms_p50=" + Percentile(intervals, intervalCount, 0.50).ToString("F1", CultureInfo.InvariantCulture) +
            " p95=" + Percentile(intervals, intervalCount, 0.95).ToString("F1", CultureInfo.InvariantCulture) +
            " max=" + maxIntervalMs.ToString("F1", CultureInfo.InvariantCulture) +
            " gatt_ms_p50=" + Percentile(gattDurations, gattCount, 0.50).ToString("F1", CultureInfo.InvariantCulture) +
            " p95=" + Percentile(gattDurations, gattCount, 0.95).ToString("F1", CultureInfo.InvariantCulture) +
            " max=" + maxGattMs.ToString("F1", CultureInfo.InvariantCulture) +
            " queue_max=" + maxQueueDepth +
            " hold_active=" + holdActive + " steady=" + steady +
            " refresh_generated=" + generated + " refresh_cancelled=" + cancelled;

        // 开新窗口，但保留 lastWriteTicks 以便跨窗口的间隔仍然正确。
        windowTicks = writeStarted;
        sourceWrites = refreshWrites = otherWrites = maxQueueDepth = 0;
        intervalCount = intervalHead = gattCount = gattHead = 0;
        maxIntervalMs = maxGattMs = 0;
        return report;
    }

    private double Percentile(double[] source, int count, double fraction)
    {
        if (count == 0) return 0;
        Array.Copy(source, sortBuffer, count);
        Array.Sort(sortBuffer, 0, count);
        int index = Math.Min(count - 1, (int)Math.Ceiling(count * fraction) - 1);
        return sortBuffer[Math.Max(0, index)];
    }
}
