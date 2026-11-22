using System;

namespace Pro2Plus;

/// <summary>
/// Pro2 震动报文的编解码。USB 有线输出与 BLE 特征共用同一套 33 字节布局：
/// 两段 16 字节马达块（偏移 1 和 17），每块 3 个 5 字节子帧，子帧里打包
/// 「频率 + 振幅 + 频率 + 振幅」四个 10 位字段。
///
/// 协议参考 ndeadly/switch2_controller_research 的 hid_reports.md（Output 0x02），
/// 以及 SDL 的 EncodeHDRumble 字段顺序。这里只做字节搬运与振幅缩放，
/// 不合成频率、不重排子帧时序。
/// </summary>
public static class RumbleFrameCodec
{
    public const int ReportSize = 64;
    public const int PacketSize = 33;

    /// <summary>两块马达块的起始偏移。</summary>
    public const int LeftBlockOffset = 1;
    public const int RightBlockOffset = 17;
    private const int BlockStride = 16;
    private const int FramesPerBlock = 3;
    private const int FrameStride = 5;

    /// <summary>子帧内两个 10 位振幅字段的位置。</summary>
    private const int LowAmplitudeShift = 10;
    private const int HighAmplitudeShift = 30;
    private const ulong AmplitudeField = 0x3ffUL;
    private const ulong AmplitudeMask = (AmplitudeField << LowAmplitudeShift) |
                                        (AmplitudeField << HighAmplitudeShift);

    /// <summary>已知格式的块头高四位；其余格式当作不透明指令原样转发。</summary>
    private const byte KnownHeaderNibble = 0x50;
    private const byte HeaderMask = 0xf0;

    /// <summary>SDL 的原生零振幅帧头：频率字段 391 / 274，后接四个零字节。</summary>
    private static readonly byte[] NeutralFrameHead = [0x87, 0x01, 0x20, 0x11];

    /// <summary>把 64 字节 raw02 报告编码成 33 字节发送包。</summary>
    public static bool TryEncode(
        ReadOnlySpan<byte> report,
        Span<byte> packet,
        double gain,
        out bool active,
        out string error)
    {
        active = false;
        error = "";
        if (packet.Length < PacketSize)
        {
            error = "native Pro2 output requires a 64-byte report 02 and a 33-byte BLE buffer";
            return false;
        }
        if (report.Length != ReportSize || report[0] != 0x02)
        {
            error = "native Pro2 output requires a 64-byte report 02 and a 33-byte BLE buffer";
            return false;
        }

        gain = double.IsFinite(gain) ? Math.Clamp(gain, 0, 3) : 1;

        // 100% 时整包原样转发：序列、控制位、三个子帧、两侧马达全部不动，
        // 中立帧与未知格式也一起保持，避免把不该解释的指令改写掉。
        report[..PacketSize].CopyTo(packet);
        packet[0] = 0;
        if (gain != 1)
        {
            bool known = IsKnownBlock(packet, LeftBlockOffset) && IsKnownBlock(packet, RightBlockOffset);
            if (!known)
            {
                if (gain == 0)
                {
                    // 显式静音任何时候都安全：不必理解未知格式也能发中立帧。
                    WriteNeutral(packet);
                    return true;
                }
                packet[..PacketSize].Clear();
                error = "unknown Pro2 rumble format; use 100% for unchanged native forwarding";
                return false;
            }
            ApplyGain(packet, gain, skipUnknownBlocks: false);
        }

        active = HasEffect(packet);
        return true;
    }

    /// <summary>对 64 字节报告原地缩放振幅，有线与蓝牙共用同一强度曲线。</summary>
    public static void ScaleReportAmplitudes(Span<byte> report, double gain)
    {
        if (report.Length < ReportSize || report[0] != 0x02 || gain == 1 || !double.IsFinite(gain))
        {
            return;
        }
        ApplyGain(report, Math.Clamp(gain, 0, 3), skipUnknownBlocks: true);
    }

    /// <summary>是否有任何一侧会真的响：未知格式一律当作有效，不吞指令。</summary>
    public static bool HasEffect(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < PacketSize)
        {
            return false;
        }
        for (int offset = LeftBlockOffset; offset <= RightBlockOffset; offset += BlockStride)
        {
            if (!IsKnownHeader(packet[offset]))
            {
                return true;
            }
            for (int frame = 0; frame < FramesPerBlock; frame++)
            {
                if ((ReadBits(packet, FrameOffset(offset, frame)) & AmplitudeMask) != 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>生成中立帧，保留原有块头的序号低四位。</summary>
    public static void WriteNeutral(Span<byte> packet)
    {
        byte leftSequence = packet.Length > LeftBlockOffset ? (byte)(packet[LeftBlockOffset] & 0x0f) : (byte)0;
        byte rightSequence = packet.Length > RightBlockOffset ? (byte)(packet[RightBlockOffset] & 0x0f) : (byte)0;
        int limit = Math.Min(packet.Length, PacketSize);
        packet[..limit].Clear();
        if (limit > LeftBlockOffset)
        {
            WriteNeutralBlock(packet, LeftBlockOffset, leftSequence);
        }
        if (limit > RightBlockOffset)
        {
            WriteNeutralBlock(packet, RightBlockOffset, rightSequence);
        }
    }

    /// <summary>复制一份并改成中立帧；不足 33 字节时补零。</summary>
    public static byte[] Neutralize(ReadOnlySpan<byte> packet)
    {
        byte[] result = new byte[PacketSize];
        if (packet.Length >= PacketSize)
        {
            packet[..PacketSize].CopyTo(result);
        }
        WriteNeutral(result);
        return result;
    }

    /// <summary>把 33 字节原生包换成 BLE 传输用的包号（保留两块各自的低位）。</summary>
    public static void StampPacketId(Span<byte> packet, byte packetId)
    {
        byte id = (byte)(packetId & 0x0f);
        packet[LeftBlockOffset] = (byte)(KnownHeaderNibble | id);
        packet[RightBlockOffset] = (byte)(KnownHeaderNibble | id);
    }

    /// <summary>空白的 64 字节 report 02：两段马达块头都是已知格式。</summary>
    public static byte[] NewRaw02Report()
    {
        byte[] report = new byte[ReportSize];
        report[0] = 0x02;
        report[LeftBlockOffset] = KnownHeaderNibble;
        report[RightBlockOffset] = KnownHeaderNibble;
        return report;
    }

    /// <summary>把 8 位强度量化到十位振幅代码的输入量程。</summary>
    public static ushort ScaleAmplitude(byte value, ushort maximum)
    {
        if (maximum == 0)
        {
            return 0;
        }
        return (ushort)((value * (uint)maximum + 127u) / 255u);
    }

    /// <summary>
    /// 写一个 5 字节子帧：高频 + 高振幅 + 低频 + 低振幅，四个十位字段依 SDL
    /// EncodeHDRumble 的顺序打包。振幅必须在屏蔽前先限幅，否则越界会回绕。
    /// </summary>
    public static void WriteSubframe(Span<byte> destination,
        ushort highFrequency, ushort highAmplitude,
        ushort lowFrequency, ushort lowAmplitude,
        ushort maximumAmplitude)
    {
        // 饱和截断，不能用 & 0x3ff：越界的控制字必须钳到 1023，而不是回绕。
        highFrequency = (ushort)Math.Min(highFrequency, (ushort)0x03ff);
        lowFrequency = (ushort)Math.Min(lowFrequency, (ushort)0x03ff);
        highAmplitude = (ushort)(Math.Min(highAmplitude, maximumAmplitude) & 0xffc0);
        lowAmplitude = (ushort)(Math.Min(lowAmplitude, maximumAmplitude) & 0xffc0);

        destination[0] = (byte)(highFrequency & 0xff);
        destination[1] = (byte)(((highFrequency >> 8) & 0x03) | ((highAmplitude >> 4) & 0xfc));
        destination[2] = (byte)(((highAmplitude >> 12) & 0x0f) | ((lowFrequency & 0x0f) << 4));
        destination[3] = (byte)(((lowFrequency >> 4) & 0x3f) | (lowAmplitude & 0xc0));
        destination[4] = (byte)((lowAmplitude >> 8) & 0xff);
    }

    private static void ApplyGain(Span<byte> data, double gain, bool skipUnknownBlocks)
    {
        for (int offset = LeftBlockOffset; offset <= RightBlockOffset; offset += BlockStride)
        {
            if (skipUnknownBlocks && !IsKnownBlock(data, offset))
            {
                continue; // 不认识的格式原样保留，改了反而是破坏
            }
            for (int frame = 0; frame < FramesPerBlock; frame++)
            {
                int at = FrameOffset(offset, frame);
                ulong bits = ReadBits(data, at);
                ulong low = ScaleAmplitude((bits >> LowAmplitudeShift) & AmplitudeField, gain);
                ulong high = ScaleAmplitude((bits >> HighAmplitudeShift) & AmplitudeField, gain);
                WriteBits(data, at, (bits & ~AmplitudeMask) | (low << LowAmplitudeShift) | (high << HighAmplitudeShift));
            }
        }
    }

    private static ulong ScaleAmplitude(ulong amplitude, double gain) =>
        (ulong)Math.Clamp(Math.Round(amplitude * gain, MidpointRounding.AwayFromZero), 0, 1023);

    private static void WriteNeutralBlock(Span<byte> packet, int offset, byte sequence)
    {
        packet[offset] = (byte)(KnownHeaderNibble | sequence);
        int at = offset + 1;
        if (at + NeutralFrameHead.Length > packet.Length)
        {
            return;
        }
        NeutralFrameHead.CopyTo(packet.Slice(at, NeutralFrameHead.Length));
    }

    private static int FrameOffset(int blockOffset, int frame) => blockOffset + 1 + frame * FrameStride;

    private static bool IsKnownHeader(byte header) => (header & HeaderMask) == KnownHeaderNibble;

    private static bool IsKnownBlock(ReadOnlySpan<byte> data, int offset) =>
        offset < data.Length && IsKnownHeader(data[offset]);

    private static ulong ReadBits(ReadOnlySpan<byte> data, int offset)
    {
        ulong bits = 0;
        for (int i = FrameStride - 1; i >= 0; i--)
        {
            bits = (bits << 8) | data[offset + i];
        }
        return bits;
    }

    private static void WriteBits(Span<byte> data, int offset, ulong bits)
    {
        for (int i = 0; i < FrameStride; i++)
        {
            data[offset + i] = (byte)(bits >> (i * 8));
        }
    }
}
