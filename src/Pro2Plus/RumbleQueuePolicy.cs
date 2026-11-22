using System;

namespace Pro2Plus;

/// <summary>
/// 33 字节震动包的只读视图：两段马达块 ×3 子帧 ×2 个 10 位振幅，共 12 个振幅带。
/// 队列的丢弃决策只看这些带宽，不合成波形、不重排子帧。
/// </summary>
internal readonly struct RumbleMotorView
{
    private readonly byte[] data;

    public RumbleMotorView(byte[] data) => this.data = data;

    public bool IsKnown => data.Length >= RumbleFrameCodec.PacketSize &&
                           (data[1] & 0xf0) == 0x50 && (data[17] & 0xf0) == 0x50;

    /// <summary>哪个马达块还在响：bit0 左、bit1 右。</summary>
    public int Activity
    {
        get
        {
            int result = 0;
            for (int band = 0; band < 12; band++)
            {
                if (Amplitude(band) != 0)
                {
                    result |= band < 6 ? 1 : 2;
                }
            }
            return result;
        }
    }

    public int Amplitude(int band) =>
        (int)((Pack(band / 2) >> (band % 2 == 0 ? 10 : 30)) & 0x3ff);

    public bool SameFrequenciesAs(RumbleMotorView other)
    {
        const ulong frequencyMask = 0x3ffUL | (0x3ffUL << 20);
        for (int frame = 0; frame < 6; frame++)
        {
            if ((Pack(frame) & frequencyMask) != (other.Pack(frame) & frequencyMask))
            {
                return false;
            }
        }
        return true;
    }

    private ulong Pack(int frame)
    {
        int at = 2 + frame / 3 * 16 + frame % 3 * 5;
        ulong bits = 0;
        for (int i = 0; i < 5; i++)
        {
            bits |= (ulong)data[at + i] << (8 * i);
        }
        return bits;
    }
}

/// <summary>
/// 队列过载时的取舍规则。目标只有一个：宁可丢掉一段正在响的旧冲击，
/// 也不能丢掉停止指令、频率切换或突然出现的峰值——那三种在体感上是"断掉"。
/// </summary>
internal static class RumbleQueuePolicy
{
    [Flags]
    public enum Protection
    {
        None = 0,
        Edge = 1,
        Stop = 2,
        Frequency = 4,
        Peak = 8,
        Opaque = 16
    }

    /// <summary>判断某个事件相对前后事件是否属于"删了会听出来"的那类。</summary>
    public static Protection Classify(byte[] current, byte[] before, byte[]? after)
    {
        var now = new RumbleMotorView(current);
        var prior = new RumbleMotorView(before);
        if (!now.IsKnown || !prior.IsKnown || (after != null && !new RumbleMotorView(after).IsKnown))
        {
            return Protection.Opaque;
        }

        int state = now.Activity;
        int previousState = prior.Activity;
        int nextState = after == null ? state : new RumbleMotorView(after).Activity;

        Protection result = Protection.None;
        if (state != previousState || state != nextState)
        {
            result |= Protection.Edge;
        }
        // 前后有一侧在响、这一侧静音，等于中间夹了一个"本不该有的停"。
        if ((previousState & ~state) != 0 || (state & ~nextState) != 0)
        {
            result |= Protection.Stop;
        }
        if (!prior.SameFrequenciesAs(now) ||
            (after != null && !now.SameFrequenciesAs(new RumbleMotorView(after))))
        {
            result |= Protection.Frequency;
        }
        if (after != null)
        {
            var next = new RumbleMotorView(after);
            for (int band = 0; band < 12; band++)
            {
                if (now.Amplitude(band) >= Math.Max(prior.Amplitude(band), next.Amplitude(band)) + 4)
                {
                    result |= Protection.Peak;
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 丢弃代价：这个事件与前后差异越小，删掉越听不出来。
    /// 12 个振幅带的绝对差之和，越小越可丢。
    /// </summary>
    public static int DropCost(byte[] current, byte[] before, byte[] after)
    {
        var now = new RumbleMotorView(current);
        var prior = new RumbleMotorView(before);
        var next = new RumbleMotorView(after);
        int cost = 0;
        for (int band = 0; band < 12; band++)
        {
            int amplitude = now.Amplitude(band);
            cost += Math.Abs(amplitude - prior.Amplitude(band)) +
                    Math.Abs(amplitude - next.Amplitude(band));
        }
        return cost;
    }

    public static bool IsSilent(byte[] data)
    {
        var view = new RumbleMotorView(data);
        return view.IsKnown && view.Activity == 0;
    }

    public static bool IsKnown(byte[] data) => new RumbleMotorView(data).IsKnown;

    /// <summary>同一只物理手柄的反馈来源归到一族，切族时必须清空队列。</summary>
    public static string SourceFamily(string source)
    {
        if (source.StartsWith("ns2pro", StringComparison.Ordinal)) return "pro2";
        if (source.StartsWith("switch1", StringComparison.Ordinal)) return "pro1";
        if (source.StartsWith("xinput", StringComparison.Ordinal)) return "xbox";
        if (source.StartsWith("dualsense-hd", StringComparison.Ordinal)) return "ps5_pcm";
        if (source.StartsWith("dualsense", StringComparison.Ordinal)) return "ps5_ordinary";
        return source;
    }
}
