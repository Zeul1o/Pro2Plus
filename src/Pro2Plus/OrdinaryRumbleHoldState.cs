using System;
using System.Diagnostics;
using System.Globalization;

namespace Pro2Plus;

/// <summary>
/// Pro2 的 BLE 震动包是「一段波形」而不是「一个持续状态」：一包只有三段
/// 12 ms 子帧，约 36 ms 后马达就自己停了。而游戏里的普通震动（Xbox / PS5）
/// 是状态型反馈，常常每 50~80 ms 才重复一次，中间不补包就会一启一停，
/// 听感是「咯咯咯」。
///
/// 这里保存最近一次仍然有效的状态并驱动周期性补包。设计上刻意不把
/// 「能否开始补包」挂在 GATT 写入成功回调上：那条链要连过生成类型、负载
/// 逐字节一致、来源字符串一致三道门槛，任一处不匹配就会永久静默，
/// 而且没有日志能看出来。现在只要处于有效状态，写入循环就会按固定周期
/// 自己来取包；写入回调只用来校准下一个到期时刻。
/// 调用方持有写锁，内部不额外起线程或计时器。
/// </summary>
public sealed class OrdinaryRumbleHoldState
{
    /// <summary>单包覆盖约 36 ms；24 ms 补一次留足余量，也不让 BLE 链路堆包。</summary>
    public const double RefreshIntervalMs = 24;

    private byte[]? heldPacket;
    private string heldSource = "";
    private long revision;
    private long dueTicks;
    private bool steady;
    private readonly long intervalTicks =
        (long)Math.Ceiling(RefreshIntervalMs * Stopwatch.Frequency / 1000);

    public bool Active => heldPacket != null;
    public bool Steady => steady;
    public long Revision => revision;
    public long Generated { get; private set; }
    public long Cancelled { get; private set; }
    public long FailureResets { get; private set; }

    public string Telemetry => "ordinary_hold=" + Active +
        " ordinary_refresh_target_ms=" + RefreshIntervalMs.ToString("F0", CultureInfo.InvariantCulture) +
        " ordinary_refresh_generated=" + Generated.ToString(CultureInfo.InvariantCulture) +
        " ordinary_refresh_cancelled=" + Cancelled.ToString(CultureInfo.InvariantCulture) +
        " ordinary_refresh_failure_resets=" + FailureResets.ToString(CultureInfo.InvariantCulture) +
        " ordinary_steady=" + steady +
        " ordinary_refresh_scope=repeated_states_only refresh_drive=writer_self_driven";

    /// <summary>普通震动来源（非原生）。保留给调用方做分类，保持逻辑本身不再据此筛选。</summary>
    public static bool IsOrdinarySource(string value, bool native = false) =>
        !native && (value == "xinput" || value == "dualsense-ordinary");

    /// <summary>
    /// 收到新的反馈负载。内容相同的重复反馈只更新负载，绝不能重置到期时间，
    /// 否则游戏每次重复都会把补包推迟，节奏永远补不上。只有状态真的变化
    /// （或收到静音 / 无效 / 编码失败）才重开一轮或立即停止。
    /// 波形类 HD 震动每包内容都不同，会反复走「重开」分支，不会被拖长。
    /// </summary>
    public void Observe(ReadOnlySpan<byte> packet, string value, bool native, bool valid)
    {
        if (!valid || packet.Length < RumbleFrameCodec.PacketSize ||
            !Pro2NativeRumbleEncoder.HasEffect(packet))
        {
            // 静音 / 无效 / 编码失败一律视为停止，不能让上一轮状态继续震。
            revision++;
            Clear();
            return;
        }

        byte[] incoming = packet[..RumbleFrameCodec.PacketSize].ToArray();
        if (heldPacket != null && SameState(heldPacket, incoming))
        {
            // 同一状态被重复确认：这才是状态型震动，允许周期性补包。
            // 只刷新负载与来源标记，保留 dueTicks，否则每次重复都会把补包推迟。
            heldPacket = incoming;
            heldSource = value;
            steady = true;
            return;
        }

        // 内容变化（或首次出现）：重开一轮。波形流每包都不同，会一直重开、
        // 永远等不到到期，因此不会被拖长；状态型反馈第二次到达即进入 steady。
        revision++;
        Clear();
        heldPacket = incoming;
        heldSource = value;
        // 0 表示「还没写过，随时可以补第一包」；真正的周期由 TryCreate /
        // Written 在拿到写入时点后推进，不依赖进程绝对时钟。
        dueTicks = 0;
        _ = native;
    }

    /// <summary>
    /// 一次真实写入完成后的回调。只用于把下一个到期时刻对齐到写入起点，
    /// 不再充当「是否允许补包」的闸门。失败且负载与当前状态一致时判状态失效。
    /// </summary>
    public void Written(Pro2QueuedRumble item, bool success, long now, long? writeStarted = null)
    {
        if (item.GeneratedKind is not "source" and not "refresh")
        {
            // 中和、重试、停止这类生成事件不改变保持状态。
            Invalidate();
            return;
        }

        if (!success)
        {
            if (heldPacket != null && item.Source == heldSource && SameState(heldPacket, item.Data))
            {
                FailureResets++;
                Invalidate();
            }
            return;
        }

        // 旧队列里的开始不能给新状态对齐时钟；已经收到的停止也不能被晚到的
        // 成功回执推翻。不匹配时什么都不做——补包仍按既有到期时间继续。
        if (heldPacket == null || item.Source != heldSource || !SameState(heldPacket, item.Data))
        {
            return;
        }

        long start = writeStarted ?? now;
        dueTicks = start + intervalTicks;
    }

    /// <summary>写入循环拿它决定等多久。无效状态才返回 +∞（纯等来源信号）。</summary>
    public double MillisecondsUntilDue(long now)
    {
        // 未确认是状态型反馈时只等来源信号：返回 0 会让写入循环空转烧 CPU。
        if (heldPacket == null || !steady)
        {
            return double.PositiveInfinity;
        }
        return dueTicks == 0 ? 0 : Math.Max(0, (dueTicks - now) * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>到点且队列为空时生成一个保持包；一次只放一个在途。</summary>
    public bool TryCreate(long now, bool sourcePending, byte sequence, out Pro2QueuedRumble? item)
    {
        item = null;
        if (heldPacket == null || !steady || sourcePending || now < dueTicks)
        {
            return false;
        }

        byte[] copy = (byte[])heldPacket.Clone();
        RumbleFrameCodec.StampPacketId(copy, sequence);
        item = new Pro2QueuedRumble(copy, false, now, Source: heldSource,
            GeneratedKind: "refresh", StateRevision: revision);
        // 立刻推进到期时间，避免同一轮循环里被反复取用；写入成功后
        // Written 会把它重新对齐到真实写入起点。
        dueTicks = now + intervalTicks;
        Generated++;
        return true;
    }

    public bool IsCurrent(Pro2QueuedRumble item) => heldPacket != null &&
        item.StateRevision == revision && item.Source == heldSource && SameState(heldPacket, item.Data);

    public void RefreshSuperseded() => Cancelled++;

    public void Invalidate()
    {
        revision++;
        Clear();
    }

    public void Reset()
    {
        Invalidate();
        Generated = Cancelled = FailureResets = 0;
    }

    private void Clear()
    {
        heldPacket = null;
        heldSource = "";
        dueTicks = 0;
        steady = false;
    }

    /// <summary>比较状态是否一致：块头只比高四位（低四位是包序号）。</summary>
    private static bool SameState(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length < RumbleFrameCodec.PacketSize || b.Length < RumbleFrameCodec.PacketSize)
        {
            return false;
        }
        for (int i = 1; i < RumbleFrameCodec.PacketSize; i++)
        {
            if (i is 1 or 17)
            {
                if ((a[i] & 0xf0) != (b[i] & 0xf0))
                {
                    return false;
                }
            }
            else if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }
}

public static class Pro2RumblePacingPolicy
{
    // 来源间隔不等于发送能力：蓝牙一个连接事件可以携带多个 PDU。
    // 真正的容量由串行 GATT 完成与实测队列遥测决定，不靠重复填充。
    public const double MinimumWriteIntervalMs = 7.5;
}
