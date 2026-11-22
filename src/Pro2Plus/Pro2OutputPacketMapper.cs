using System;

namespace Pro2Plus;

public sealed record Pro2OutputPacket(byte[] Report, string Source, bool Active, byte? PlayerLedMask,
    bool PreserveNativePayload = false, string ConversionTelemetry = "", int FrequencyClamps = 0);

/// <summary>
/// 虚拟手柄反馈 → 物理 Pro2 震动报告的转换。
/// Pro2 模式是原生透传；Pro1 走 SwitchHdRumbleMapper 换算；Xbox / DualSense 的 legacy
/// 震动只有强度，按左右马达分别编码成三段相同子帧。
/// 触觉（HD / PCM）不在这里，由 DualSenseHapticFeedback 与
/// Pro2QualityHapticProcessor 单独处理。
/// </summary>
public static class Pro2OutputPacketMapper
{
    private const byte FlagRumble = 0x01;
    private const byte FlagPlayerLed = 0x02;
    private const int SubframeCount = 3;
    private const ushort OrdinaryAmplitudeFullScale = 29000;

    private static readonly ushort ReferenceLowFrequency = RumbleFrequencyLimits.ReferenceLowControl;
    private static readonly ushort ReferenceHighFrequency = RumbleFrequencyLimits.ReferenceHighControl;

    /// <summary>按当前虚拟设备类型把反馈转成 64 字节 report 02。</summary>
    public static bool TryMapFeedback(
        ViiperDeviceProfile profile,
        ReadOnlySpan<byte> feedback,
        out Pro2OutputPacket packet,
        out string reason)
    {
        packet = default!;
        reason = "";

        switch (profile.Mode)
        {
            case ViiperVirtualMode.Pro2:
                return MapNative(feedback, out packet, out reason);

            case ViiperVirtualMode.Pro1:
                return SwitchHdRumbleMapper.TryMap(feedback, out packet, out reason);

            case ViiperVirtualMode.Xbox:
            case ViiperVirtualMode.XboxProfessionalImuTest:
                if (feedback.Length < 2)
                {
                    reason = "xinput feedback too short";
                    return false;
                }
                packet = BuildOrdinaryPacket(feedback[1], feedback[0], "xinput");
                return true;

            case ViiperVirtualMode.DualSenseLike:
            case ViiperVirtualMode.DualSenseProfessionalImuTest:
                if (feedback.Length < 2)
                {
                    reason = "dualsense feedback too short";
                    return false;
                }
                packet = BuildOrdinaryPacket(feedback[0], feedback[1], "dualsense-ordinary");
                return true;

            default:
                reason = "unsupported profile";
                return false;
        }
    }

    /// <summary>Pro2 原生 34 字节反馈：两段马达块原样搬到 report，不做二次量化。</summary>
    private static bool MapNative(ReadOnlySpan<byte> feedback, out Pro2OutputPacket packet, out string reason)
    {
        packet = default!;
        reason = "";
        if (feedback.Length < 34)
        {
            reason = "ns2pro feedback too short";
            return false;
        }

        byte flags = feedback[32];
        byte? playerLed = (flags & FlagPlayerLed) != 0 ? feedback[33] : null;
        if ((flags & FlagRumble) == 0)
        {
            reason = playerLed.HasValue
                ? "led-only feedback has no physical Pro2 rumble payload"
                : "feedback has no rumble flag";
            return false;
        }

        byte[] report = RumbleFrameCodec.NewRaw02Report();
        feedback[..16].CopyTo(report.AsSpan(RumbleFrameCodec.LeftBlockOffset, 16));
        feedback.Slice(16, 16).CopyTo(report.AsSpan(RumbleFrameCodec.RightBlockOffset, 16));
        packet = new Pro2OutputPacket(report, "ns2pro-hd",
            Pro2NativeRumbleEncoder.HasEffect(report), playerLed, PreserveNativePayload: true);
        return true;
    }

    /// <summary>
    /// XInput / DualSense legacy 震动：强（低频）走左马达，弱（高频）走右马达。
    /// 不把两个值混进两侧，否则来源的空间提示会丢掉。
    /// </summary>
    public static Pro2OutputPacket BuildOrdinaryPacket(byte weak, byte strong, string source)
    {
        byte[] report = RumbleFrameCodec.NewRaw02Report();
        FillSide(report, RumbleFrameCodec.LeftBlockOffset, weak: 0, strong: strong);
        FillSide(report, RumbleFrameCodec.RightBlockOffset, weak: weak, strong: 0);
        return new Pro2OutputPacket(report, source, weak != 0 || strong != 0, null);
    }

    /// <summary>按四个频率/振幅参数直接构造一份 report，用于诊断与标定。</summary>
    public static Pro2OutputPacket BuildRaw02Packet(
        ushort lowFrequencyLeft, ushort lowAmplitudeLeft,
        ushort highFrequencyLeft, ushort highAmplitudeLeft,
        ushort lowFrequencyRight, ushort lowAmplitudeRight,
        ushort highFrequencyRight, ushort highAmplitudeRight,
        string source)
    {
        byte[] report = RumbleFrameCodec.NewRaw02Report();
        for (int frame = 0; frame < SubframeCount; frame++)
        {
            RumbleFrameCodec.WriteSubframe(
                FrameSpan(report, RumbleFrameCodec.LeftBlockOffset, frame),
                highFrequencyLeft, highAmplitudeLeft, lowFrequencyLeft, lowAmplitudeLeft,
                OrdinaryAmplitudeFullScale);
            RumbleFrameCodec.WriteSubframe(
                FrameSpan(report, RumbleFrameCodec.RightBlockOffset, frame),
                highFrequencyRight, highAmplitudeRight, lowFrequencyRight, lowAmplitudeRight,
                OrdinaryAmplitudeFullScale);
        }
        // 小于一个量化步长的输入会被编码成 0，活动状态以实际编码结果为准。
        return new Pro2OutputPacket(report, source, Pro2NativeRumbleEncoder.HasEffect(report), null);
    }

    /// <summary>
    /// 一侧 = 块头 + 三段相同子帧。只填第一段、后两段留零会被手柄当成
    /// 立即衰减，随后 HID 输出通道 STALL，表现是"震一下就再也不动了"。
    /// </summary>
    private static void FillSide(byte[] report, int blockOffset, byte weak, byte strong)
    {
        Span<byte> side = report.AsSpan(blockOffset, 16);
        side.Clear();
        side[0] = 0x50;

        ushort lowAmplitude = RumbleFrameCodec.ScaleAmplitude(strong, OrdinaryAmplitudeFullScale);
        ushort highAmplitude = RumbleFrameCodec.ScaleAmplitude(weak, OrdinaryAmplitudeFullScale);
        // 普通震动只有强度：参考频率是固定控制字，不是实测 Hz，也不随强度变调。
        for (int frame = 0; frame < SubframeCount; frame++)
        {
            RumbleFrameCodec.WriteSubframe(
                FrameSpan(report, blockOffset, frame),
                ReferenceHighFrequency, highAmplitude, ReferenceLowFrequency, lowAmplitude,
                OrdinaryAmplitudeFullScale);
        }
    }

    private static Span<byte> FrameSpan(byte[] report, int blockOffset, int frame) =>
        report.AsSpan(blockOffset + 1 + frame * 5, 5);
}
