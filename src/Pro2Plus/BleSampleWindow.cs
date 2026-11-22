using System;
using System.Diagnostics;

namespace Pro2Plus;

/// <summary>
/// 一次 BLE 采样窗的累积器。所有写入都在接收回调的同一把闸下调用，
/// 内部用预分配数组 + 计数，不随回调增长堆，也不会因 List 扩容拷贝而抖动。
/// 计数与样本分开记：超过容量后计数继续增长（用于算平均速率），样本只保留前 N 个。
/// </summary>
internal sealed class Win10BleDiagnosticCapture
{
    private const int MaximumSamples = 8192;

    private readonly long[] rawTicks = new long[MaximumSamples];
    private readonly long[] fd2Ticks = new long[MaximumSamples];
    private readonly long[] parsedTicks = new long[MaximumSamples];
    private int rawCount;
    private int fd2Count;
    private int parsedCount;
    private ArrivalClock fd2Arrivals;
    private ArrivalClock parsedArrivals;
    private string interruption = "";

    public Win10BleDiagnosticCapture(long startedTicks, DateTimeOffset startedAtUtc)
    {
        StartedTicks = startedTicks;
        StartedAtUtc = startedAtUtc;
    }

    public long StartedTicks { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public void RecordRaw(long ticks, bool isFd2)
    {
        // 采集开始前就已入队的回调不属于本窗口。
        if (ticks < StartedTicks) return;
        Append(rawTicks, ref rawCount, ticks);
        if (!isFd2) return;
        fd2Arrivals.Note(ticks, StartedTicks);
        Append(fd2Ticks, ref fd2Count, ticks);
    }

    public void RecordParsed(long ticks, bool isFd2)
    {
        if (!isFd2 || ticks < StartedTicks) return;
        parsedArrivals.Note(ticks, StartedTicks);
        Append(parsedTicks, ref parsedCount, ticks);
    }

    /// <summary>与 RecordRaw/RecordParsed 一样在接收闸下调用；只记第一条原因。</summary>
    public void MarkInterrupted(string reason)
    {
        if (interruption.Length != 0) return;
        interruption = reason.Length == 0 ? "connection_interrupted" : reason;
    }

    public Win10BleDiagnosticSamples Snapshot(long completedTicks, bool measurementCompleted = false,
        bool connectedAtEnd = false)
    {
        long fd2Anchor = fd2Arrivals.Anchor(StartedTicks);
        long parsedAnchor = parsedArrivals.Anchor(StartedTicks);
        return new Win10BleDiagnosticSamples(
            StartedTicks,
            completedTicks,
            Slice(rawTicks, rawCount),
            Slice(fd2Ticks, fd2Count),
            Slice(parsedTicks, parsedCount))
        {
            RawCount = rawCount,
            Fd2Count = fd2Count,
            ParsedCount = parsedCount,
            MeasurementCompleted = measurementCompleted,
            ConnectedAtEnd = connectedAtEnd,
            Interruption = interruption,
            LastParsedInputAgeMs = ToMilliseconds(completedTicks - parsedAnchor),
            MaximumFd2SilenceMs = ToMilliseconds(Math.Max(fd2Arrivals.MaximumGap, completedTicks - fd2Anchor)),
            MaximumParsedSilenceMs = ToMilliseconds(Math.Max(parsedArrivals.MaximumGap, completedTicks - parsedAnchor))
        };
    }

    private static void Append(long[] buffer, ref int count, long ticks)
    {
        if (count < MaximumSamples) buffer[count] = ticks;
        count++;
    }

    private static long[] Slice(long[] buffer, int count) =>
        buffer.AsSpan(0, Math.Min(count, MaximumSamples)).ToArray();

    private static double ToMilliseconds(long ticks) => Math.Max(0, ticks * 1000.0 / Stopwatch.Frequency);

    /// <summary>
    /// 到达时刻统计。并发回调可能乱序进门，因此「最后一次到达」只能单调前进，
    /// 乱序的旧时间戳既不能把新鲜度时钟拨回去，也不能凭空造出一个大间隔。
    /// </summary>
    private struct ArrivalClock
    {
        private long last;
        private long maximumGap;

        public readonly long MaximumGap => maximumGap;

        public void Note(long ticks, long windowStart)
        {
            long previous = last == 0 ? windowStart : last;
            long gap = ticks - previous;
            if (gap > maximumGap) maximumGap = gap;
            if (ticks > last) last = ticks;
        }

        /// <summary>没有任何到达时以窗口起点为准，避免算出「距 0 点很久」的假静默。</summary>
        public readonly long Anchor(long windowStart) => last == 0 ? windowStart : last;
    }
}

internal sealed record Win10BleDiagnosticSamples(
    long StartedTicks,
    long CompletedTicks,
    long[] RawTicks,
    long[] Fd2Ticks,
    long[] ParsedTicks)
{
    public int RawCount { get; init; } = RawTicks.Length;
    public int Fd2Count { get; init; } = Fd2Ticks.Length;
    public int ParsedCount { get; init; } = ParsedTicks.Length;
    public bool MeasurementCompleted { get; init; }
    public bool ConnectedAtEnd { get; init; }
    public string Interruption { get; init; } = "";
    public double? LastParsedInputAgeMs { get; init; }
    public double? MaximumFd2SilenceMs { get; init; }
    public double? MaximumParsedSilenceMs { get; init; }

    /// <summary>样本被容量截断过（计数仍完整）。</summary>
    public bool GapSamplesTruncated =>
        RawCount > RawTicks.Length || Fd2Count > Fd2Ticks.Length || ParsedCount > ParsedTicks.Length;

    public double DurationSeconds =>
        Math.Max(0.001, (CompletedTicks - StartedTicks) / (double)Stopwatch.Frequency);
}
