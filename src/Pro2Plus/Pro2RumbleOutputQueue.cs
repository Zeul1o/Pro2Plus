using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Pro2Plus;

public sealed record Pro2QueuedRumble(byte[] Data, bool Native, long EnqueuedTicks, bool SafetyStop = false,
    int StopRetryAttempt = 0, string Source = "unknown", string GeneratedKind = "source", long StateRevision = 0);

/// <summary>
/// 震动事件的发送队列。服务跟得上时原样转发每一个包；只有在测得持续过载时，
/// 才按「整事件」丢弃最不显眼的那一个——不会合成载波频率，也不会重排子帧时序。
/// 这是有损的事件取舍，不是重采样，也不代表一定能播放出来。
/// </summary>
public sealed class Pro2RumbleOutputQueue
{
    public const int Capacity = 32;
    public const double MaxAgeMs = 30;
    private const int SourceGapWindow = 32;

    private readonly object gate = new();
    private readonly List<Pro2QueuedRumble> pending = new();
    private readonly Queue<double> sourceGaps = new();
    private double gapSum;
    private double serviceBudgetMs = 8;
    private long lastSourceTicks;
    private bool? nativeMode;
    private string lastSource = "unknown";
    private string sourceFamily = "unknown";
    private byte[] lastWritten = RumbleFrameCodec.Neutralize(ReadOnlySpan<byte>.Empty);
    private string policy = "passthrough";

    public long OverflowDropped { get; private set; }
    public long ExpiredDropped { get; private set; }
    public long Replaced { get; private set; }
    public long ModeDiscarded { get; private set; }
    public long ExpiryStops { get; private set; }
    public long SafetyStops { get; private set; }
    public long SafetyDiscarded { get; private set; }
    public long AdaptiveDropped { get; private set; }
    public long DeadlineEdgeDropped { get; private set; }
    public long OpaqueDropped { get; private set; }
    public long ProtectedEdges { get; private set; }
    public long StopProtected { get; private set; }
    public long GainDiscarded { get; private set; }
    public long GainStops { get; private set; }
    public long WriteFailures { get; private set; }
    public long StopRetries { get; private set; }
    public long StopRetryFailures { get; private set; }
    public long StopRetrySuperseded { get; private set; }
    public long DeadlinePeakDropped { get; private set; }
    public long DeadlineFrequencyDropped { get; private set; }
    public long DeadlineStopDropped { get; private set; }
    public long DeadlineStopsDelivered { get; private set; }
    public long FailureStops { get; private set; }
    public int MaximumDepth { get; private set; }
    public double MaximumAgeMs { get; private set; }

    public int Count
    {
        get { lock (gate) return pending.Count; }
    }

    public bool LastWrittenActive
    {
        get
        {
            lock (gate)
            {
                return !RumbleQueuePolicy.IsKnown(lastWritten) || new RumbleMotorView(lastWritten).Activity != 0;
            }
        }
    }

    public byte[] LastWrittenNeutral
    {
        get { lock (gate) return RumbleFrameCodec.Neutralize(lastWritten); }
    }

    public bool HasNativePending
    {
        get { lock (gate) return pending.Count > 0 && pending[0].Native; }
    }

    public string Telemetry
    {
        get
        {
            lock (gate)
            {
                bool lossy = AdaptiveDropped + OverflowDropped + ExpiredDropped + SafetyDiscarded +
                    GainDiscarded + ModeDiscarded + Replaced > 0;
                double avgSourceMs = sourceGaps.Count > 0 ? gapSum / sourceGaps.Count : 0;
                return FormattableString.Invariant(
                    $"rumble_policy={policy} rumble_lossy={(lossy ? "true" : "false")} source_ms={avgSourceMs:F2} service_ms={serviceBudgetMs:F2}") +
                    $" rumble_depth={pending.Count} rumble_max_depth={MaximumDepth}" +
                    $" rumble_overflow={OverflowDropped} rumble_expired={ExpiredDropped} rumble_adaptive_drop={AdaptiveDropped}" +
                    $" rumble_deadline_edge_drop={DeadlineEdgeDropped} rumble_opaque_drop={OpaqueDropped}" +
                    $" rumble_edge_protection_checks={ProtectedEdges} rumble_stop_protection_checks={StopProtected}" +
                    $" rumble_deadline_peak_drop={DeadlinePeakDropped} rumble_deadline_frequency_drop={DeadlineFrequencyDropped} rumble_deadline_stop_drop={DeadlineStopDropped}" +
                    $" rumble_deadline_stop_delivered={DeadlineStopsDelivered}" +
                    $" rumble_transport_failure_stops={FailureStops}" +
                    $" rumble_mode_discard={ModeDiscarded} rumble_expiry_stops={ExpiryStops}" +
                    $" rumble_safety_stops={SafetyStops} rumble_safety_discard={SafetyDiscarded}" +
                    $" rumble_gain_discard={GainDiscarded} rumble_gain_stops={GainStops} rumble_write_failures={WriteFailures}" +
                    $" rumble_stop_retries={StopRetries} rumble_stop_retry_failures={StopRetryFailures} rumble_stop_retry_superseded={StopRetrySuperseded}" +
                    " rumble_max_wait_ms=" + MaximumAgeMs.ToString("F2", CultureInfo.InvariantCulture) +
                    " native_queue_deadline_ms=30 converted_queue_deadline_ms=30 queue_scope=all_modes loss_scope=connection_local_queue protection_checks_are_not_deliveries=true sequence_gaps_possible=true";
            }
        }
    }

    /// <summary>服务预算用于判定"真的跟不上了"，与来源回报率无关。</summary>
    public void ConfigureServiceBudget(double milliseconds)
    {
        lock (gate)
        {
            serviceBudgetMs = double.IsFinite(milliseconds) ? Math.Clamp(milliseconds, 7.5, 30) : 8;
        }
    }

    public bool TryEnqueueNative(ReadOnlySpan<byte> report, double gain, long now,
        out byte[] packet, out string error, string source = "ns2pro-hd")
    {
        packet = new byte[RumbleFrameCodec.PacketSize];
        bool encoded = Pro2NativeRumbleEncoder.TryEncode(report, packet, gain, out _, out error);
        lock (gate)
        {
            if (!encoded)
            {
                // 编码失败说明来源不可信：清空在途事件，改发一帧中和，避免卡在上一状态。
                SafetyDiscarded += pending.Count;
                pending.Clear();
                SafetyStops++;
                packet = RumbleFrameCodec.Neutralize(report);
                error += "; safety_stop_queued=true";
            }
            Enqueue(packet, true, now, source);
            if (!encoded)
            {
                pending[^1] = pending[^1] with { SafetyStop = true, GeneratedKind = "encoding_stop" };
            }
        }
        return encoded;
    }

    public void Enqueue(ReadOnlySpan<byte> data, bool native, long now, string source = "unknown",
        bool safetyStop = false)
    {
        lock (gate)
        {
            if (safetyStop)
            {
                SafetyDiscarded += pending.Count;
                pending.Clear();
                SafetyStops++;
            }

            string family = RumbleQueuePolicy.SourceFamily(source);
            if (nativeMode.HasValue && (nativeMode != native || sourceFamily != family))
            {
                // 换了来源或换了包格式，旧事件的时序已经没有意义。
                ModeDiscarded += pending.Count;
                pending.Clear();
                sourceGaps.Clear();
                gapSum = 0;
                lastSourceTicks = 0;
            }
            nativeMode = native;
            lastSource = source;
            sourceFamily = family;

            // 新事件到达就作废上一轮的中和重试：不能让一个旧的"停"插在新冲击前面。
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].StopRetryAttempt > 0)
                {
                    pending.RemoveAt(i);
                    StopRetrySuperseded++;
                }
            }

            TrackSourceGap(now);

            if (pending.Count == Capacity)
            {
                // 满队列优先丢旧的冲击，尽量保住显式的停止。
                int candidate = FindExpendable();
                DropAt(candidate >= 0 ? candidate : 0, expired: false);
            }

            pending.Add(new Pro2QueuedRumble(data.ToArray(), native, now, safetyStop,
                Source: source, GeneratedKind: safetyStop ? "encoding_stop" : "source"));
            MaximumDepth = Math.Max(MaximumDepth, pending.Count);
        }
    }

    public bool TryDequeue(long now, out Pro2QueuedRumble? item)
    {
        lock (gate)
        {
            if (DrainExpiredLocked(now, out item))
            {
                return true;
            }
            if (pending.Count == 0)
            {
                item = null;
                return false;
            }

            policy = "passthrough";
            if (IsOverloaded(now))
            {
                policy = "event_selective_lossy";
                ThinDownToSingleLocked();
            }

            item = pending[0];
            pending.RemoveAt(0);
            MaximumAgeMs = Math.Max(MaximumAgeMs, AgeMs(item, now));
            return true;
        }
    }

    /// <summary>状态只在 GATT 调用真的成功后才算生效，出队不算。</summary>
    public void MarkWriteResult(Pro2QueuedRumble item, bool success, long? completedTicks = null)
    {
        lock (gate)
        {
            if (success)
            {
                lastWritten = item.Data.ToArray();
                return;
            }

            WriteFailures++;
            bool isStop = item.SafetyStop || RumbleQueuePolicy.IsSilent(item.Data);
            if (!isStop)
            {
                return; // 失败的冲击不重放，交给下一次真实事件
            }
            if (item.StopRetryAttempt > 0)
            {
                StopRetryFailures++;
                return;
            }
            if (pending.Count > 0 || nativeMode != item.Native || sourceFamily != RumbleQueuePolicy.SourceFamily(item.Source))
            {
                StopRetrySuperseded++;
                return;
            }

            // 只补一次中和：既不复读失败的冲击，也不形成无限重试链。
            pending.Add(new Pro2QueuedRumble(RumbleFrameCodec.Neutralize(item.Data), item.Native,
                completedTicks ?? Stopwatch.GetTimestamp(), true, 1, item.Source, GeneratedKind: "stop_retry"));
            StopRetries++;
        }
    }

    public bool StopForGainChange(long now)
    {
        lock (gate)
        {
            if (!nativeMode.HasValue)
            {
                return false;
            }
            GainDiscarded += pending.Count;
            pending.Clear();
            GainStops++;
            pending.Add(new Pro2QueuedRumble(RumbleFrameCodec.Neutralize(lastWritten), nativeMode.Value, now, true,
                Source: lastSource, GeneratedKind: "gain_stop"));
            return true;
        }
    }

    public bool TryEnqueueFailureStop(Pro2QueuedRumble failed, long now)
    {
        lock (gate)
        {
            // 只有普通震动状态才需要补中和：HD/原生负载与已失败的停止都不再重试。
            if (failed.Native || !OrdinaryRumbleHoldState.IsOrdinarySource(failed.Source) ||
                !RumbleQueuePolicy.IsKnown(failed.Data) || new RumbleMotorView(failed.Data).Activity == 0 ||
                pending.Count != 0 || sourceFamily != RumbleQueuePolicy.SourceFamily(failed.Source))
            {
                return false;
            }
            pending.Add(new Pro2QueuedRumble(RumbleFrameCodec.Neutralize(failed.Data), false, now, true,
                Source: failed.Source, GeneratedKind: "transport_stop"));
            FailureStops++;
            return true;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            pending.Clear();
            nativeMode = null;
            lastSource = sourceFamily = "unknown";
            sourceGaps.Clear();
            gapSum = 0;
            lastSourceTicks = 0;
            lastWritten = RumbleFrameCodec.Neutralize(ReadOnlySpan<byte>.Empty);
            serviceBudgetMs = 8;
            policy = "passthrough";
            OverflowDropped = ExpiredDropped = Replaced = ModeDiscarded = ExpiryStops = 0;
            SafetyStops = SafetyDiscarded = AdaptiveDropped = DeadlineEdgeDropped = OpaqueDropped = 0;
            ProtectedEdges = StopProtected = GainDiscarded = GainStops = WriteFailures = 0;
            StopRetries = StopRetryFailures = StopRetrySuperseded = 0;
            DeadlinePeakDropped = DeadlineFrequencyDropped = DeadlineStopDropped = 0;
            DeadlineStopsDelivered = 0;
            FailureStops = 0;
            MaximumDepth = 0;
            MaximumAgeMs = 0;
        }
    }

    /// <summary>过期事件出队。返回 true 表示已经产出一个可发送项。</summary>
    private bool DrainExpiredLocked(long now, out Pro2QueuedRumble? item)
    {
        item = null;
        while (pending.Count > 0 && AgeMs(pending[0], now) > MaxAgeMs)
        {
            Pro2QueuedRumble head = pending[0];
            MaximumAgeMs = Math.Max(MaximumAgeMs, AgeMs(head, now));

            // 迟到的中和仍然安全，迟到的冲击不能发：原样送出去当屏障。
            bool isStop = head.SafetyStop || RumbleQueuePolicy.IsSilent(head.Data);
            if (isStop)
            {
                pending.RemoveAt(0);
                DeadlineStopsDelivered++;
                item = head;
                policy = "deadline_explicit_stop";
                return true;
            }

            DropAt(0, expired: true);
            if (pending.Count == 0)
            {
                item = new Pro2QueuedRumble(RumbleFrameCodec.Neutralize(head.Data), head.Native, now, true,
                    head.StopRetryAttempt, head.Source, GeneratedKind: "deadline_stop");
                ExpiryStops++;
                policy = "deadline_stop";
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 持续过载（不是瞬时突发）才做事件取舍：来源间隔稳定小于服务预算的九成，
    /// 且队首已经等待超过预算。
    /// </summary>
    private bool IsOverloaded(long now)
    {
        if (pending.Count <= 1 || sourceGaps.Count < 8)
        {
            return false;
        }
        double averageGap = gapSum / sourceGaps.Count;
        return averageGap < serviceBudgetMs * 0.9 && AgeMs(pending[0], now) >= serviceBudgetMs;
    }

    private void ThinDownToSingleLocked()
    {
        while (pending.Count > 1)
        {
            int victim = -1;
            int lowestCost = int.MaxValue;
            for (int i = 0; i < pending.Count - 1; i++)
            {
                Pro2QueuedRumble current = pending[i];
                byte[] before = i == 0 ? lastWritten : pending[i - 1].Data;
                byte[] after = pending[i + 1].Data;
                if (current.SafetyStop ||
                    !RumbleQueuePolicy.IsKnown(current.Data) ||
                    !RumbleQueuePolicy.IsKnown(before) ||
                    !RumbleQueuePolicy.IsKnown(after))
                {
                    continue;
                }

                RumbleQueuePolicy.Protection protection = RumbleQueuePolicy.Classify(current.Data, before, after);
                if (protection != RumbleQueuePolicy.Protection.None)
                {
                    ProtectedEdges++;
                    if ((protection & RumbleQueuePolicy.Protection.Stop) != 0)
                    {
                        StopProtected++;
                    }
                    continue;
                }

                int cost = RumbleQueuePolicy.DropCost(current.Data, before, after);
                if (cost < lowestCost)
                {
                    lowestCost = cost;
                    victim = i;
                }
            }

            if (victim < 0)
            {
                policy = "edge_or_opaque_protected";
                break;
            }
            pending.RemoveAt(victim);
            AdaptiveDropped++;
        }
    }

    /// <summary>队列满时找一个"丢了最不心疼"的位置。</summary>
    private int FindExpendable()
    {
        for (int i = 0; i < pending.Count; i++)
        {
            Pro2QueuedRumble item = pending[i];
            if (!item.SafetyStop && !RumbleQueuePolicy.IsSilent(item.Data))
            {
                return i;
            }
        }
        return -1;
    }

    private void TrackSourceGap(long now)
    {
        if (lastSourceTicks != 0)
        {
            double gap = (now - lastSourceTicks) * 1000.0 / Stopwatch.Frequency;
            if (gap > 250)
            {
                // 中间断了一大段（暂停、重连），旧间隔样本作废。
                sourceGaps.Clear();
                gapSum = 0;
            }
            else if (gap >= 0)
            {
                sourceGaps.Enqueue(gap);
                gapSum += gap;
                if (sourceGaps.Count > SourceGapWindow)
                {
                    gapSum -= sourceGaps.Dequeue();
                }
            }
        }
        lastSourceTicks = now;
    }

    private void DropAt(int index, bool expired)
    {
        Pro2QueuedRumble current = pending[index];
        if (!RumbleQueuePolicy.IsKnown(current.Data))
        {
            OpaqueDropped++;
        }
        else
        {
            byte[] before = index == 0 ? lastWritten : pending[index - 1].Data;
            byte[]? after = index + 1 < pending.Count ? pending[index + 1].Data : null;
            RumbleQueuePolicy.Protection protection = RumbleQueuePolicy.Classify(current.Data, before, after);
            if (protection != RumbleQueuePolicy.Protection.None)
            {
                DeadlineEdgeDropped++;
            }
            if ((protection & RumbleQueuePolicy.Protection.Peak) != 0)
            {
                DeadlinePeakDropped++;
            }
            if ((protection & RumbleQueuePolicy.Protection.Frequency) != 0)
            {
                DeadlineFrequencyDropped++;
            }
            if ((protection & RumbleQueuePolicy.Protection.Stop) != 0)
            {
                DeadlineStopDropped++;
            }
        }

        pending.RemoveAt(index);
        if (expired)
        {
            ExpiredDropped++;
        }
        else
        {
            OverflowDropped++;
        }
    }

    private static double AgeMs(Pro2QueuedRumble item, long now) =>
        Math.Max(0, (now - item.EnqueuedTicks) * 1000.0 / Stopwatch.Frequency);
}
