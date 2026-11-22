using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Pro2Plus;

/// <summary>初始化命令的下发策略：免配对输入绝不提交/交换配对数据。</summary>
public static class BleInitAuditPolicy
{
    // 0x15 = 提交并交换配对信息的初始化命令。
    private const byte PairingCommitCommand = 0x15;

    public static bool ShouldSend(ReadOnlySpan<byte> command, bool skipUsbInitialization)
    {
        if (command.Length < 8) return false;
        if (command[0] == PairingCommitCommand) return false;
        return !skipUsbInitialization || command[0] != 0x03 || command[3] != 0x0d;
    }

    public static string Profile(bool skipUsb) => skipUsb ? "no_pair_finalize_no_usb_init" : "no_pair_finalize";

    public static bool AckMatches(ReadOnlySpan<byte> request, ReadOnlySpan<byte> reply) =>
        request.Length >= 8 && reply.Length >= 8 && request[0] == reply[0] && request[3] == reply[3];

    public static string CommandIdentity(ReadOnlySpan<byte> data) => data.Length < 8
        ? "short_packet"
        : "cmd=0x" + data[0].ToString("X2") + " sub=0x" + data[3].ToString("X2") + " len=" + data.Length;
}

/// <summary>
/// 固定容量的接收统计：不做 GATT 查询、不写磁盘、不为每条通知分配字符串。
/// 时间全部是应用侧观测（QPC），不是 HCI/空口时间戳，也不是射频延迟。
/// </summary>
public sealed class BleProtocolSessionAudit
{
    private const int PreviousCapacity = 128;
    private const int ControlCapacity = 80;
    private const int EndedCapacity = 3;
    private const int StepBuckets = 18;
    private const int ShortestFrame = 46;
    private const int CounterOffset = 0;
    private const int ControlOffset = 4;
    private const int ControlBytes = 6;
    private const int MotionOffset = 42;
    private const double DelayedCallbackMs = 15;
    private const double HighRateCandidateHz = 180;

    private readonly object sync = new();
    private readonly byte[] previous = new byte[PreviousCapacity];
    private readonly Queue<string> control = new();
    private readonly Queue<string> ended = new();

    private string id = "none";
    private string profile = "not_started";
    private string status = "not_started";
    private long attemptTicks, windowsTicks, gattTicks, firstFd2Ticks, lastFd2Ticks, endTicks;
    private FrameStats stats = new();
    private uint previousCounter, previousMotion;
    private int previousLength;
    private ushort maxPdu;
    private bool hasPrevious, endedCurrent;
    private long sequence;

    public void Begin(bool skipUsb, long ticks)
    {
        lock (sync)
        {
            if (id != "none") ArchivePrevious(ticks);

            id = (++sequence).ToString(CultureInfo.InvariantCulture);
            profile = BleInitAuditPolicy.Profile(skipUsb);
            status = "opening";
            attemptTicks = ticks;
            windowsTicks = gattTicks = firstFd2Ticks = lastFd2Ticks = endTicks = 0;
            stats = new FrameStats();
            previousCounter = previousMotion = 0;
            previousLength = 0;
            maxPdu = 0;
            hasPrevious = endedCurrent = false;
            control.Clear();
            AddNoLock("begin profile=" + profile + " init_pairing_commands_allowed=false system_writes=false");
        }
    }

    public void Connected(string observer, long ticks)
    {
        lock (sync)
        {
            if (id == "none" || endedCurrent) return;
            if (observer == "windows" && windowsTicks == 0) windowsTicks = ticks;
            if (observer == "gatt" && gattTicks == 0) gattTicks = ticks;
            status = "connected";
            AddNoLock(observer + "_active elapsed_attempt_ms=" + Milliseconds(attemptTicks, ticks));
        }
    }

    public void SetPdu(ushort value)
    {
        lock (sync) maxPdu = value;
    }

    public void Note(string detail, long ticks)
    {
        lock (sync)
        {
            if (id == "none") return;
            AddNoLock("t_attempt_ms=" + Milliseconds(attemptTicks, ticks) + " " + detail);
        }
    }

    public void RecordFd2(ReadOnlySpan<byte> data, long ticks, double callbackAgeMs)
    {
        lock (sync)
        {
            if (id == "none" || endedCurrent) return;

            FrameStats counted = stats;
            counted.Frames++;
            if (firstFd2Ticks == 0) firstFd2Ticks = ticks;
            lastFd2Ticks = ticks;
            TrackCallbackAge(counted, callbackAgeMs);

            if (data.Length < ShortestFrame || data.Length > previous.Length)
            {
                counted.Malformed++;
                return;
            }

            uint counter = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(CounterOffset, 4));
            uint motion = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(MotionOffset, 4));
            if (hasPrevious) CompareWithPrevious(counted, data, counter, motion);

            data.CopyTo(previous);
            previousLength = data.Length;
            previousCounter = counter;
            previousMotion = motion;
            hasPrevious = true;
        }
    }

    public string End(string reason, long ticks)
    {
        lock (sync)
        {
            if (id == "none") return "";
            if (!endedCurrent)
            {
                status = reason;
                endedCurrent = true;
                endTicks = ticks;
                AddNoLock("end reason=" + reason + " t_attempt_ms=" + Milliseconds(attemptTicks, ticks));
            }

            return FormatNoLock(ticks);
        }
    }

    public string Summary(long ticks)
    {
        lock (sync) return FormatNoLock(ticks);
    }

    public string DiagnosticText(long ticks)
    {
        lock (sync)
        {
            var text = new StringBuilder("BLE 初始化与停流诊断（只读缓存）\n");
            text.AppendLine(FormatNoLock(ticks));
            foreach (string item in control) text.AppendLine("[BLE_CONTROL_EVIDENCE] attempt=" + id + " " + item);
            if (ended.Count > 0)
            {
                text.AppendLine("此前连接摘要（最多 3 次；重连不会抹掉停流证据）");
                foreach (string item in ended) text.AppendLine(item);
            }

            text.AppendLine("Windows/GATT 建链时间是应用观察值，非空口时间；时间戳变化和通知数量不独自证明物理采样率。HCI 断开原因未采集，不能由 Windows 状态猜测 0x08 或 LL 超时。");
            return text.ToString();
        }
    }

    // 上一次会话的摘要进环形缓冲：重连后停流证据仍可回看，但最多保留三次。
    private void ArchivePrevious(long ticks)
    {
        string detail = FormatNoLock(ticks) + "\n" + string.Join("\n", control.Select(item =>
            "[BLE_CONTROL_EVIDENCE] attempt=" + id + " " + item));
        ended.Enqueue(detail);
        while (ended.Count > EndedCapacity) ended.Dequeue();
    }

    private void AddNoLock(string detail)
    {
        control.Enqueue(detail.Replace('\r', ' ').Replace('\n', ' '));
        while (control.Count > ControlCapacity) control.Dequeue();
    }

    private void CompareWithPrevious(FrameStats counted, ReadOnlySpan<byte> data, uint counter, uint motion)
    {
        if (data.Length == previousLength && data.SequenceEqual(previous.AsSpan(0, previousLength)))
        {
            counted.SameRaw++;
        }

        if (data.Slice(ControlOffset, 4).SequenceEqual(previous.AsSpan(ControlOffset, 4)) &&
            data.Slice(10, ControlBytes).SequenceEqual(previous.AsSpan(10, ControlBytes)))
        {
            counted.SameControls++;
        }

        // 计数字段是自由运行的：差值按无符号回绕算，再按 17+ 归并成最后一桶。
        uint step = unchecked(counter - previousCounter);
        counted.Steps[Math.Min((int)Math.Min(step, StepBuckets - 1u), StepBuckets - 1)]++;
        if (step == 0) counted.SameCounter++;

        if (motion == previousMotion)
        {
            counted.SameMotion++;
            return;
        }

        counted.MotionChanges++;
        uint motionStep = unchecked(motion - previousMotion);
        if (motionStep <= int.MaxValue) counted.MotionAdvance += motionStep;
        else counted.MotionBackwards++;
    }

    private static void TrackCallbackAge(FrameStats counted, double ageMs)
    {
        if (!double.IsFinite(ageMs)) return;
        if (ageMs < 0)
        {
            counted.NegativeCallbackAges++;
            return;
        }

        counted.MaxCallbackAgeMs = Math.Max(counted.MaxCallbackAgeMs, ageMs);
        if (ageMs >= DelayedCallbackMs) counted.DelayedCallbacks++;
    }

    private string FormatNoLock(long ticks)
    {
        FrameStats counted = stats;
        double seconds = firstFd2Ticks > 0 && lastFd2Ticks > firstFd2Ticks
            ? (lastFd2Ticks - firstFd2Ticks) / (double)Stopwatch.Frequency
            : 0;
        double hz = seconds > 0 ? (counted.Frames - 1) / seconds : 0;

        var line = new StringBuilder("[BLE_SESSION_EVIDENCE] attempt=").Append(id)
            .Append(" profile=").Append(profile)
            .Append(" status=").Append(status)
            .Append(" clock=host_observed_qpc")
            .Append(Field("last_fd2_after_windows_ms", Milliseconds(windowsTicks, lastFd2Ticks)))
            .Append(Field("last_fd2_after_gatt_ms", Milliseconds(gattTicks, lastFd2Ticks)))
            .Append(Field("now_after_windows_ms", Milliseconds(windowsTicks, ticks)))
            .Append(Field("now_after_gatt_ms", Milliseconds(gattTicks, ticks)))
            .Append(Field("end_after_windows_ms", Milliseconds(windowsTicks, endTicks)))
            .Append(Field("end_after_gatt_ms", Milliseconds(gattTicks, endTicks)))
            .Append(Field("fd2_count", counted.Frames))
            .Append(Field("fd2_active_span_s", Number(seconds)))
            .Append(Field("fd2_active_span_hz", Number(hz)))
            .Append(Field("high_notify_candidate", (hz >= HighRateCandidateHz).ToString().ToLowerInvariant()))
            .Append(Field("fd2_age_ms", Milliseconds(lastFd2Ticks, ticks)))
            .Append(Field("adjacent_same_raw", counted.SameRaw))
            .Append(Field("adjacent_same_controls", counted.SameControls))
            .Append(Field("adjacent_same_counter", counted.SameCounter))
            .Append(Field("counter_steps", StepHistogram(counted)))
            .Append(Field("motion_timestamp_changes", counted.MotionChanges))
            .Append(Field("adjacent_same_motion_timestamp", counted.SameMotion))
            .Append(Field("motion_timestamp_advance_raw", counted.MotionAdvance))
            .Append(Field("motion_backwards", counted.MotionBackwards))
            .Append(Field("malformed", counted.Malformed))
            .Append(Field("callback_delivery_age_max_ms", Number(counted.MaxCallbackAgeMs)))
            .Append(Field("callback_age_ge15", counted.DelayedCallbacks))
            .Append(Field("callback_age_negative", counted.NegativeCallbackAges))
            .Append(Field("winrt_max_pdu", maxPdu))
            .Append(" stable_250hz_proven=false");
        return line.ToString();
    }

    private static string StepHistogram(FrameStats counted)
    {
        var parts = new List<string>();
        for (int i = 0; i < counted.Steps.Length; i++)
        {
            if (counted.Steps[i] <= 0) continue;
            parts.Add((i == StepBuckets - 1 ? "17+" : i.ToString(CultureInfo.InvariantCulture)) + ":" + counted.Steps[i]);
        }

        return string.Join(",", parts);
    }

    private static string Field(string name, object value) => " " + name + "=" + value;

    private static string Number(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static string Milliseconds(long start, long end) => start > 0 && end >= start
        ? Number((end - start) * 1000.0 / Stopwatch.Frequency)
        : "unavailable";

    // 每次连接一份，Begin 直接换新实例，避免逐个字段清零漏项。
    private sealed class FrameStats
    {
        public long Frames;
        public long SameRaw;
        public long SameControls;
        public long SameCounter;
        public long SameMotion;
        public long MotionChanges;
        public long MotionAdvance;
        public long MotionBackwards;
        public long Malformed;
        public long DelayedCallbacks;
        public long NegativeCallbackAges;
        public double MaxCallbackAgeMs;
        public readonly long[] Steps = new long[StepBuckets];
    }
}
