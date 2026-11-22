using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

/// <summary>
/// 实验室对照用的原始帧采集（PRO2_FD2_RESEARCH_CAPTURE=路径 打开）。
/// BLE 回调只做入队，绝不等待磁盘；队列写满时挤掉最旧的一帧并计数。
/// </summary>
public sealed class Pro2Fd2ResearchRecorder : IAsyncDisposable
{
    private const string EnvironmentVariable = "PRO2_FD2_RESEARCH_CAPTURE";
    private const int DefaultCapacity = 8192;
    private const int NotStarted = 0;
    private const int Running = 1;
    private const int Suppressed = 2;

    private static readonly object StartGate = new();

    private readonly CaptureFrameQueue? queue;
    private readonly string disabledReason;
    private Task? writerTask;
    private int writerState;
    private long droppedOutsideQueue;
    private string writeFailure = "";

    public Pro2Fd2ResearchRecorder() : this(Environment.GetEnvironmentVariable(EnvironmentVariable))
    {
    }

    internal Pro2Fd2ResearchRecorder(string? configuredPath, int capacity = DefaultCapacity)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            disabledReason = "not_configured";
            return;
        }

        try
        {
            OutputPath = Path.GetFullPath(configuredPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            // 环境变量写错不应该让主程序在构造期崩溃：关掉采集，把原因留给人看。
            disabledReason = "invalid_path:" + ex.GetType().Name;
            return;
        }

        queue = new CaptureFrameQueue(capacity);
        disabledReason = "";
    }

    public bool Enabled => queue != null;
    public string OutputPath { get; } = "";

    /// <summary>丢弃的帧数：队列满被挤掉的 + 通道关闭后仍写入的。</summary>
    public long Dropped => (queue?.Dropped ?? 0) + Interlocked.Read(ref droppedOutsideQueue);

    /// <summary>写盘线程异常的类型名；正常为空。</summary>
    public string WriteFailure => Volatile.Read(ref writeFailure);

    /// <summary>
    /// 未启用时的原因：<c>not_configured</c>（没设环境变量）或 <c>invalid_path:异常类型</c>。
    /// 路径已被同进程另一个实例占用时不计在这里，而是第一帧就被丢弃（见 <see cref="Dropped"/>）。
    /// </summary>
    public string DisabledReason => disabledReason;

    public void TryRecord(ulong sequence, DateTimeOffset timestamp, double deltaMs, byte[] raw)
    {
        if (queue == null) return;
        EnsureWriterStarted();
        if (Volatile.Read(ref writerState) != Running) return;
        // 调用方可能复用解析缓冲区，入队前取一份快照。
        if (!queue.TryWrite(new CaptureFrame(sequence, timestamp, deltaMs, raw.ToArray())))
            Interlocked.Increment(ref droppedOutsideQueue);
    }

    private void EnsureWriterStarted()
    {
        if (Volatile.Read(ref writerState) != NotStarted) return;
        lock (StartGate)
        {
            if (writerState != NotStarted) return;
            if (!CapturePathLease.TryAcquire(OutputPath))
            {
                writerState = Suppressed;
                Interlocked.Increment(ref droppedOutsideQueue);
                return;
            }

            writerState = Running;
            writerTask = Task.Run(WriteLoopAsync);
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            string? directory = Path.GetDirectoryName(OutputPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await using var stream = new FileStream(OutputPath, FileMode.Create, FileAccess.Write,
                FileShare.Read, 64 * 1024, useAsync: true);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(ResearchCaptureFormat.Header(DateTimeOffset.Now, EnvironmentVariable))
                .ConfigureAwait(false);

            int pendingFlush = 0;
            await foreach (CaptureFrame frame in queue!.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await writer.WriteLineAsync(
                        ResearchCaptureFormat.Frame(frame.Sequence, frame.Timestamp, frame.DeltaMs, frame.Raw))
                    .ConfigureAwait(false);
                if (++pendingFlush < 64) continue;
                await writer.FlushAsync().ConfigureAwait(false);
                pendingFlush = 0;
            }

            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref writeFailure, ex.GetType().Name);
            Interlocked.Increment(ref droppedOutsideQueue);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (queue == null) return;
        if (Volatile.Read(ref writerState) != Running) return;
        queue.Complete();
        if (writerTask != null) await writerTask.ConfigureAwait(false);
        CapturePathLease.Release(OutputPath);
    }
}
