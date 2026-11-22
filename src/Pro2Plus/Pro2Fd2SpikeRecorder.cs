using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Pro2Plus;

/// <summary>
/// 轴突跳现场记录器：环形缓冲保存最近若干帧，触发时把“触发点前后一段”写成 JSONL。
/// 采集默认关闭（只在原始完整性诊断模式下打开），写盘走独立的后台通道，
/// 解析线程只做一次入环和一次入队，不做任何等待。
/// </summary>
public sealed class Pro2Fd2SpikeRecorder : IAsyncDisposable
{
    private const int QueueCapacity = 16;

    private readonly object gate = new();
    private readonly InputStabilityOptions options;
    private readonly Pro2Fd2FrameSnapshot?[] frames;
    private readonly DumpRateWindow rateWindow;
    private readonly Channel<SpikeDumpRequest> dumpQueue;
    private readonly string dumpDirectory;
    private readonly Task writerTask;
    private long droppedDumpCount;
    private long writtenDumpCount;

    public Pro2Fd2SpikeRecorder(InputStabilityOptions options) : this(options, null)
    {
    }

    // 目录可注入，便于离线自检把落盘结果读回来比对。
    internal Pro2Fd2SpikeRecorder(InputStabilityOptions options, string? dumpDirectory)
    {
        this.options = InputStabilityOptions.Normalize(options);
        this.dumpDirectory = dumpDirectory ?? SpikeDumpPath.DefaultDirectory;
        CaptureEnabled = this.options.RawIntegrityModeEnabled ||
                         EnvironmentSwitches.IsEnabled("PRO2_FD2_SPIKE_CAPTURE");
        frames = new Pro2Fd2FrameSnapshot[this.options.AxisSpikeRingBufferCapacity];
        rateWindow = new DumpRateWindow(this.options.AxisSpikeDumpRateLimitPer10Seconds);
        dumpQueue = Channel.CreateBounded<SpikeDumpRequest>(
            new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        writerTask = Task.Run(WriteLoopAsync);
    }

    public long DroppedDumpCount => droppedDumpCount;
    public long WrittenDumpCount => writtenDumpCount;
    public bool CaptureEnabled { get; }

    public void Clear()
    {
        lock (gate)
        {
            Array.Clear(frames);
        }
    }

    public void AddFrame(Pro2Fd2FrameSnapshot frame)
    {
        if (!CaptureEnabled) return;
        lock (gate)
        {
            frames[(int)(frame.FrameIndex % (ulong)frames.Length)] = frame;
        }
    }

    /// <summary>返回是否成功排队；被限流、被关闭或队列满都记一次丢弃。</summary>
    public bool TryQueueDump(Pro2AxisSpikeTelemetry telemetry, ulong triggerFrameIndex, out string path)
    {
        path = "";
        if (!CaptureEnabled || !options.AxisSpikeRawDumpEnabled)
        {
            droppedDumpCount++;
            return false;
        }

        if (!rateWindow.TryTake(DateTimeOffset.UtcNow))
        {
            droppedDumpCount++;
            return false;
        }

        string target = SpikeDumpPath.Build(telemetry, DateTime.Now, dumpDirectory);
        var request = new SpikeDumpRequest(
            telemetry,
            triggerFrameIndex,
            target,
            Math.Max(0, options.AxisSpikeRingBufferFramesBefore),
            Math.Max(0, options.AxisSpikeRingBufferFramesAfter));
        if (!dumpQueue.Writer.TryWrite(request))
        {
            droppedDumpCount++;
            return false;
        }

        path = target;
        return true;
    }

    private async Task WriteLoopAsync()
    {
        await foreach (SpikeDumpRequest request in dumpQueue.Reader.ReadAllAsync())
        {
            try
            {
                await WriteDumpAsync(request).ConfigureAwait(false);
                writtenDumpCount++;
            }
            catch
            {
                // 写盘失败不能影响手柄输入链路，只记一次丢弃。
                droppedDumpCount++;
            }
        }
    }

    private async Task WriteDumpAsync(SpikeDumpRequest request)
    {
        // 等触发点之后的帧真正到达缓冲区：一帧约 24ms，另加 120ms 余量，上限 1 秒。
        int delayMs = Math.Min(1000, request.AfterFrames * 24 + 120);
        if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);

        ulong first = request.TriggerFrameIndex >= (ulong)request.BeforeFrames
            ? request.TriggerFrameIndex - (ulong)request.BeforeFrames
            : 0;
        IReadOnlyList<Pro2Fd2FrameSnapshot> snapshot = SnapshotRange(first, request.TriggerFrameIndex + (ulong)request.AfterFrames);

        Directory.CreateDirectory(Path.GetDirectoryName(request.Path)!);
        await using FileStream stream = new(
            request.Path, FileMode.Create, FileAccess.Write, FileShare.Read, 16 * 1024, useAsync: true);
        await using StreamWriter writer = new(stream);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "spike",
            request.Telemetry,
            request.TriggerFrameIndex,
            request.BeforeFrames,
            request.AfterFrames,
            captured_frames = snapshot.Count
        })).ConfigureAwait(false);
        foreach (Pro2Fd2FrameSnapshot frame in snapshot)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "frame", frame })).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<Pro2Fd2FrameSnapshot> SnapshotRange(ulong first, ulong last)
    {
        lock (gate)
        {
            return SelectRange(frames, first, last);
        }
    }

    /// <summary>取环里帧号落在 [first,last] 的帧，按帧号升序——环的物理顺序不等于时间顺序。</summary>
    internal static Pro2Fd2FrameSnapshot[] SelectRange(Pro2Fd2FrameSnapshot?[] ring, ulong first, ulong last)
    {
        var selected = new List<Pro2Fd2FrameSnapshot>(ring.Length);
        foreach (Pro2Fd2FrameSnapshot? frame in ring)
        {
            if (frame != null && frame.FrameIndex >= first && frame.FrameIndex <= last) selected.Add(frame);
        }

        selected.Sort(static (a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
        return selected.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        dumpQueue.Writer.TryComplete();
        await writerTask.ConfigureAwait(false);
    }

    private sealed record SpikeDumpRequest(
        Pro2AxisSpikeTelemetry Telemetry,
        ulong TriggerFrameIndex,
        string Path,
        int BeforeFrames,
        int AfterFrames);
}

/// <summary>
/// 每 10 秒最多写 N 个文件的滑动窗口。只吃调用方给的时刻，内部不读时钟，
/// 便于离线自检覆盖边界；时钟回退时不清理任何记录（与旧行为一致）。
/// </summary>
internal sealed class DumpRateWindow(int limit)
{
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly DateTimeOffset[] stamps = new DateTimeOffset[Math.Max(limit, 1)];
    private int head;
    private int count;

    public bool TryTake(DateTimeOffset now)
    {
        DropExpired(now);
        if (count >= limit) return false;
        stamps[(head + count) % stamps.Length] = now;
        count++;
        return true;
    }

    private void DropExpired(DateTimeOffset now)
    {
        while (count > 0 && now - stamps[head] > Window)
        {
            head = (head + 1) % stamps.Length;
            count--;
        }
    }
}

/// <summary>突跳转储的文件名规则：<c>spike_yyyyMMdd_HHmmss_fff_序号_轴名.jsonl</c>。</summary>
internal static class SpikeDumpPath
{
    internal static string DefaultDirectory => Path.Combine(AppPaths.LogsDirectory, "spikes");

    internal static string Build(Pro2AxisSpikeTelemetry telemetry, DateTime localNow, string directory) =>
        Path.Combine(
            directory,
            "spike_" + localNow.ToString("yyyyMMdd_HHmmss_fff") +
            "_" + telemetry.ParseSeq +
            "_" + SafeAxis(telemetry.AxisName) + ".jsonl");

    /// <summary>轴名会进文件名，只把空格换成下划线，其余保持原样（与历史文件一致）。</summary>
    internal static string SafeAxis(string axisName) => axisName.Replace(" ", "_", StringComparison.Ordinal);
}
