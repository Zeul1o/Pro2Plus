using System;

namespace Pro2Plus;

/// <summary>
/// 原生 Pro2 震动通道的入口：USB 输出报文与 BLE 特征共用同一份 33 字节载荷，
/// 区别只是 BLE 会改写两个块头的低四位当作包序号。
/// 具体字节布局与振幅缩放在 <see cref="RumbleFrameCodec"/>。
/// </summary>
public static class Pro2NativeRumbleEncoder
{
    public const int PacketSize = RumbleFrameCodec.PacketSize;

    /// <summary>
    /// 把 64 字节 report 02 转成可发送的 33 字节包。<paramref name="gain"/> 只缩放振幅，
    /// 100% 时整包逐字节保留（含未知格式与中立帧），保证原生透传不受影响。
    /// </summary>
    public static bool TryEncode(
        ReadOnlySpan<byte> report,
        Span<byte> packet,
        double gain,
        out bool active,
        out string error)
    {
        if (packet.Length < PacketSize)
        {
            active = false;
            error = "native Pro2 output requires a 64-byte report 02 and a 33-byte BLE buffer";
            return false;
        }
        if (report.Length != RumbleFrameCodec.ReportSize || report[0] != 0x02)
        {
            active = false;
            error = "native Pro2 output requires a 64-byte report 02 and a 33-byte BLE buffer";
            return false;
        }
        return RumbleFrameCodec.TryEncode(report, packet, gain, out active, out error);
    }

    public static bool HasEffect(ReadOnlySpan<byte> report) => RumbleFrameCodec.HasEffect(report);

    /// <summary>按强度增益原地缩放振幅；两块都是已知格式时才动，未知格式原样保留。</summary>
    public static void ScaleReportAmplitudes(Span<byte> report, double gain) =>
        RumbleFrameCodec.ScaleReportAmplitudes(report, gain);

    /// <summary>构造一份中立帧（保留原包号），用于停止、超时与兜底。</summary>
    public static byte[] Neutralize(ReadOnlySpan<byte> blePacket) =>
        RumbleFrameCodec.Neutralize(blePacket);
}
