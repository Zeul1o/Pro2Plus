using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

/// <summary>
/// 有线（USB）输入后端，挂在 BLE 输入源上。
///
/// 设计取舍：不新建一整套输入源，而是把 USB 读到的状态注入 BLE 源已有的发布管线
/// （稳定性滤波 → latest → 顺序队列 → 唤醒信号 → 速率统计）。
/// 这样键鼠映射、陀螺映射、VIIPER 输出、震动策略一行都不用改，
/// 有线/蓝牙切换对上层完全透明。
/// </summary>
public sealed partial class Pro2BleInputSource
{
    private Pro2WiredProtocol? wiredPad;
    private CancellationTokenSource? wiredCts;
    private Task? wiredTask;
    private IProgress<string>? wiredProgress;
    private volatile bool wiredActive;
    private long wiredFrames;
    private long wiredFirstTicks;
    private long wiredLastTicks;
    private long wiredLastLogTicks;
    private long wiredErrorFrames;
    private string wiredLastError = "";
    private bool wiredCalibrationLogged;

    private readonly byte[] wiredBuffer = new byte[64];

    /// <summary>当前是否在用 USB 有线输入。</summary>
    public bool IsWiredInput => wiredActive;

    private const string WiredSourceName = "wired_0x05";

    /// <summary>有线的开关：设 PRO2_WIRED=0 可强制走蓝牙（排查用）。</summary>
    private static bool WiredEnabled
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable("PRO2_WIRED");
            return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 尝试走有线。成功接管输入返回 true（调用方应直接 return，不再扫描蓝牙）。
    /// </summary>
    private async Task<bool> TryStartWiredAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        if (!WiredEnabled)
        {
            progress.Report("[PRO2_WIRED] 已由 PRO2_WIRED=0 禁用，走蓝牙。");
            return false;
        }

        wiredProgress = progress;
        wiredFrames = 0;
        wiredFirstTicks = 0;
        wiredLastTicks = 0;
        wiredLastLogTicks = 0;
        wiredErrorFrames = 0;
        wiredLastError = "";
        wiredCalibrationLogged = false;

        // 打开 + 初始化要发十几条命令，几十到几百毫秒，不要阻塞 UI 线程。
        (Pro2WiredProtocol? Pad, string Detail) result = await Task.Run(() =>
        {
            var pad = new Pro2WiredProtocol();
            try
            {
                if (pad.TryOpen(out string detail)) return (pad, "ok");
                pad.Dispose();
                return ((Pro2WiredProtocol?)null, detail);
            }
            catch (Exception ex)
            {
                pad.Dispose();
                return ((Pro2WiredProtocol?)null, ex.GetType().Name + ": " + ex.Message);
            }
        }, cancellationToken).ConfigureAwait(false);

        if (result.Pad == null)
        {
            progress.Report("[PRO2_WIRED] 有线不可用，回退蓝牙：" + result.Detail);
            return false;
        }

        Pro2WiredProtocol padInstance = result.Pad;
        wiredPad = padInstance;
        wiredCts = new CancellationTokenSource();
        wiredActive = true;

        lock (gate)
        {
            IsRunning = true;
            transportConnected = true;
            rateAccepted = true;
            linkRateClass = "500hz";
            lastPerformanceWarning = "";
            lastPerformanceFailure = "";
            lastParseSource = WiredSourceName;
        }

        SetConnectionPhase(ConnectionPhase.Ready);
        progress.Report("[PRO2_WIRED] 有线已连接 vid_pid=057E:2069 report=0x05 endpoint=0x81 " +
                        "期望 500Hz · " + padInstance.CalibrationSummary);
        progress.Report("[PRO2_STICK] mode=" + StickProcessingModeLabel(stickProcessingMode) +
                        " 有线通道沿用同一套稳定性策略。");

        CancellationToken token = wiredCts.Token;
        wiredTask = Task.Factory.StartNew(
            () => WiredLoop(token),
            token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    private void WiredLoop(CancellationToken token)
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }

        Pro2WiredProtocol? pad = wiredPad;
        if (pad == null) return;

        int consecutiveErrors = 0;
        long lastPublishTicks = 0;

        while (!token.IsCancellationRequested)
        {
            int length;
            try
            {
                length = pad.ReadReport(wiredBuffer, 250);
            }
            catch (Exception ex)
            {
                wiredLastError = ex.GetType().Name + ": " + ex.Message;
                length = -1;
            }

            if (length == 0)
            {
                // 超时：设备暂时没新帧。震动由 pad 自己的线程按 12ms 发，这里不用管。
                continue;
            }

            if (length < 0)
            {
                consecutiveErrors++;
                wiredErrorFrames++;
                if (consecutiveErrors == 1)
                {
                    wiredProgress?.Report("[PRO2_WIRED] 读取失败 " + wiredLastError + "，继续重试。");
                }

                if (consecutiveErrors >= 20)
                {
                    lock (gate)
                    {
                        transportConnected = false;
                        status = "有线输入中断（" + wiredLastError + "），请重新插拔 USB。";
                    }

                    SetConnectionPhase(ConnectionPhase.Disconnected);
                    wiredActive = false;
                    return;
                }

                Thread.Sleep(20);
                continue;
            }

            consecutiveErrors = 0;
            long nowTicks = Stopwatch.GetTimestamp();

            if (wiredFirstTicks == 0) wiredFirstTicks = nowTicks;
            wiredLastTicks = nowTicks;
            wiredFrames++;
            lastPublishTicks = nowTicks;

            try
            {
                InjectWiredFrame(wiredBuffer, length, nowTicks);
            }
            catch (Exception ex)
            {
                wiredLastError = "parse: " + ex.GetType().Name + ": " + ex.Message;
                wiredErrorFrames++;
            }

            // 震动写在自己的线程上（12ms 节流 + 持续重发），这条循环只管收数据。

            if (!wiredCalibrationLogged && pad.CalibrationReady)
            {
                wiredCalibrationLogged = true;
                wiredProgress?.Report("[PRO2_WIRED_CAL] " + pad.CalibrationSummary);
            }

            long nowForLog = nowTicks;
            if (wiredLastLogTicks == 0 ||
                Stopwatch.GetElapsedTime(wiredLastLogTicks, nowForLog).TotalSeconds >= 5)
            {
                wiredLastLogTicks = nowForLog;
                double hz = wiredFrames > 1 && wiredLastTicks > wiredFirstTicks
                    ? (wiredFrames - 1) * (double)Stopwatch.Frequency / (wiredLastTicks - wiredFirstTicks)
                    : 0;
                wiredProgress?.Report("[PRO2_WIRED_RATE] hz=" + hz.ToString("F1") +
                                      " frames=" + wiredFrames +
                                      " errors=" + wiredErrorFrames +
                                      " report=0x" + wiredBuffer[0].ToString("X2") +
                                      " rumble=" + pad.RumbleRouteLabel +
                                      " rumble_n=" + pad.RumbleSentCount +
                                      " rumble_fail=" + pad.RumbleFailCount +
                                      " rumble_err=" + pad.RumbleErrorLabel +
                                      " " + pad.TakeGyroProbe() +
                                      " last_error=" + (wiredLastError.Length == 0 ? "none" : wiredLastError));
            }

            _ = lastPublishTicks;
        }
    }

    /// <summary>把一帧有线状态并入 BLE 源原有的发布管线。</summary>
    private void InjectWiredFrame(byte[] report, int length, long nowTicks)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ulong sequence;
        lock (gate) sequence = (ulong)rawNotifyCount + 1;

        GamepadState parsed = (wiredPad ?? throw new InvalidOperationException("wired pad closed"))
            .Parse(report, length, nowTicks, sequence);

        Pro2InputFilterResult filterResult;
        GamepadState stableState;
        GamepadState rawSnapshot;

        lock (gate)
        {
            rawNotifyCount++;
            fd2InputNotifyCount++;
            // 解析计数同步递增，否则 live rate 复核会把有线判成 rate_limited 而降级。
            fd2InputParsedCount++;
            NoteSample(
                nowTicks,
                ref firstParsedNotifyTicks,
                ref lastParsedNotifyTicks,
                ref lastParsedNotifyGapTicks,
                ref maxParsedNotifyGapTicks);
            NoteSample(
                nowTicks,
                ref firstFd2InputTicks,
                ref lastFd2InputTicks,
                ref lastFd2InputGapTicks,
                ref maxFd2InputGapTicks);

            filterResult = stickProcessingMode == StickProcessingMode.RawDirect
                ? new Pro2InputFilterResult(parsed, parsed, Array.Empty<Pro2AxisFilterEvent>())
                : inputStability.Process(parsed, nowTicks);

            stableState = filterResult.AcceptedState;
            rawSnapshot = filterResult.RawState;

            if (filterResult.HasAxisIntervention)
            {
                axisSpikeRejectCount += (uint)filterResult.InterventionCount;
            }

            updates++;
            stableState.Updates = updates;
            rawSnapshot.Updates = updates;

            rawState = rawSnapshot;
            rawStateAt = now;
            latest = stableState;
            latestAt = now;
            lastParseSource = WiredSourceName;

            NoteUserActivityNoLock(stableState, nowTicks);

            if (IsRunning)
            {
                sequentialInput.Enqueue(stableState, nowTicks);
                inputSignal.Notify();
            }
        }
    }

    private void StopWired()
    {
        wiredActive = false;

        try { wiredCts?.Cancel(); } catch (ObjectDisposedException) { }

        Task? task = wiredTask;
        if (task != null)
        {
            try { task.Wait(300); } catch (AggregateException) { } catch (Exception) { }
        }

        wiredTask = null;

        try { wiredCts?.Dispose(); } catch { }
        wiredCts = null;

        Pro2WiredProtocol? pad = wiredPad;
        wiredPad = null;
        try { pad?.SendNeutralRumble(); } catch { }
        try { pad?.Dispose(); } catch { }

        lock (gate)
        {
            wiredFrames = 0;
            wiredFirstTicks = 0;
            wiredLastTicks = 0;
            wiredLastLogTicks = 0;
        }
    }
}
