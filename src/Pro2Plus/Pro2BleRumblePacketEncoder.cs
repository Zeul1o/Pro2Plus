using System;

namespace Pro2Plus;

/// <summary>
/// BLE 侧的发包入口：接收的已经是打包好的十位振幅参数（原生或由 Pro1/Xbox/PS5
/// 反馈转换而来），不再做第二次归一化，否则振幅会被放大、音调反向。
/// </summary>
public static class Pro2BleRumblePacketEncoder
{
    public const int Raw02ReportSize = RumbleFrameCodec.ReportSize;
    public const int BlePacketSize = RumbleFrameCodec.PacketSize;
    private const byte KnownBlockHeader = 0x50;
    private const byte HeaderMask = 0xf0;

    public static bool TryEncodeRaw02(
        ReadOnlySpan<byte> report,
        byte packetId,
        Span<byte> packet,
        out bool active,
        out string error,
        double gain = 1.0)
    {
        active = false;
        error = "";
        if (packet.Length < BlePacketSize)
        {
            error = "BLE rumble packet buffer too small";
            return false;
        }
        if (report.Length != Raw02ReportSize || report[0] != 0x02)
        {
            error = "raw02 HID report must be 64 bytes with report id 0x02";
            return false;
        }
        if ((report[1] & HeaderMask) != KnownBlockHeader || (report[17] & HeaderMask) != KnownBlockHeader)
        {
            error = "converted raw02 HID report requires two known 0x50 rumble blocks";
            return false;
        }

        if (!Pro2NativeRumbleEncoder.TryEncode(report, packet, gain, out active, out error))
        {
            return false;
        }

        // 只有块头低四位携带 BLE 包序号；100% 增益时其余字节与原生报告完全一致。
        RumbleFrameCodec.StampPacketId(packet, packetId);
        return true;
    }
}
