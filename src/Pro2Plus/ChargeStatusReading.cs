using System;
using System.Buffers.Binary;

namespace Pro2Plus;

/// <summary>
/// FD2 报告被动字节 0x21，或只读命令 0B/04 的回包。
/// 回包里的标志字含义未确认，不能当作主报告的 Power Info 解读；充电状态是单字节。
/// </summary>
public readonly record struct ChargeStatusReading(ushort Code, ushort Flags)
{
    public bool Known => Code == 0 || Code == 0x20 || Code is >= 0x30 and <= 0x3f;
    public bool Charging => Code is >= 0x30 and <= 0x3f;
    public bool ExternalPower => Code == 0x20 || Charging;

    public static bool TryDecodeFd2(ReadOnlySpan<byte> payload, out ChargeStatusReading status)
    {
        status = default;
        if (payload.Length < 60) return false;
        status = new ChargeStatusReading(payload[0x21], 0);
        return true;
    }

    public static bool TryDecodeReply(ReadOnlySpan<byte> reply, out ChargeStatusReading status)
    {
        status = default;
        if (reply.Length < 12 || reply[0] != 0x0b || reply[1] != 1 ||
            reply[2] != 1 || reply[3] != 4 || reply[5] != 0x78) return false;
        status = new ChargeStatusReading(BinaryPrimitives.ReadUInt16LittleEndian(reply.Slice(8, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(reply.Slice(10, 2)));
        return true;
    }

    public void ApplyTo(GamepadState state)
    {
        state.BatteryPowerKnown = Known;
        state.BatteryCharging = Known && Charging;
        state.BatteryExternalPower = Known && ExternalPower;
    }
}
