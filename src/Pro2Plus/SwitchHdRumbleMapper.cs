using System;

namespace Pro2Plus;

/// <summary>
/// 一代 Switch 的震动反馈（4 字节一对「低频码 / 高频码」）换算成本机 Pro2 的 5 字节马达帧。
/// 这是参数换算而不是 PCM 透传：源侧先解出 Hz 与振幅，再编码成本机协议的受限控制量。
/// 源 Hz 与本机控制量刻意分开——当前的代理量仍需实机标定。
/// </summary>
public static class SwitchHdRumbleMapper
{
    // SDL_hidapi_switch.c 里 hfa[101] 的独立逆向表（0..65535）。按码值直接取，
    // 不换成四舍五入后的毫单位：第一个非零码是 514/65535，不是 10/1003。
    // 超过 SDL 安全端点 1.0 的源码在这里饱和。
    private static readonly ushort[] AmplitudeByCode =
    [
        0, 514, 775, 921, 1096, 1303, 1550, 1843, 2192, 2606, 3100, 3686, 4383, 5213, 6199, 7372,
        7698, 8039, 8395, 8767, 9155, 9560, 9984, 10426, 10887, 11369, 11873, 12398, 12947, 13520,
        14119, 14744, 15067, 15397, 15734, 16079, 16431, 16790, 17158, 17534, 17918, 18310, 18711,
        19121, 19540, 19967, 20405, 20851, 21308, 21775, 22251, 22739, 23236, 23745, 24265, 24797,
        25340, 25894, 26462, 27041, 27633, 28238, 28856, 29488, 30134, 30794, 31468, 32157, 32861,
        33581, 34316, 35068, 35836, 36620, 37422, 38242, 39079, 39935, 40809, 41703, 42616, 43549,
        44503, 45477, 46473, 47491, 48531, 49593, 50679, 51789, 52923, 54082, 55266, 56476, 57713,
        58977, 60268, 61588, 62936, 64315, 65535
    ];

    private const int FeedbackBytes = 10;
    private const byte RumbleFlag = 0x01;
    private const byte PlayerLedFlag = 0x02;
    private const string SourceTag = "switch1-hd-converted";

    /// <summary>一对马达解出的音量：低频 / 高频各自的 Hz 与振幅控制量。</summary>
    public readonly record struct HdPair(ushort LowHz, ushort LowAmplitude, ushort HighHz, ushort HighAmplitude);

    /// <summary>把 4 字节源码解成一对 Hz / 振幅。调用方保证长度 ≥ 4。</summary>
    public static HdPair DecodePair(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < 4)
        {
            throw new ArgumentException("Switch Pro rumble requires four bytes.", nameof(encoded));
        }

        int lowCode = (encoded[2] & 0x7F) + 0x40;
        int highCode = ((encoded[0] | ((encoded[1] & 1) << 8)) >> 2) + 0x60;
        int lowAmplitudeCode = encoded[3] >= 0x40
            ? ((encoded[3] - 0x40) * 2) + (encoded[2] >> 7)
            : 0;

        return new HdPair(
            FrequencyOf(lowCode), AmplitudeOf(lowAmplitudeCode),
            FrequencyOf(highCode), AmplitudeOf(encoded[1] >> 1));
    }

    public static bool TryMap(ReadOnlySpan<byte> feedback, out Pro2OutputPacket packet, out string reason)
    {
        packet = default!;
        reason = string.Empty;
        if (feedback.Length != FeedbackBytes)
        {
            reason = "Switch Pro feedback must be ten bytes";
            return false;
        }

        byte flags = feedback[8];
        byte? led = (flags & PlayerLedFlag) != 0 ? feedback[9] : null;
        if ((flags & RumbleFlag) == 0)
        {
            // 只报灯光的反馈没有物理马达负载，不能凭灯位推断震动。
            reason = led.HasValue
                ? "led-only feedback has no physical Pro2 rumble payload"
                : "feedback has no rumble flag";
            return false;
        }

        HdPair left = DecodePair(feedback[..4]);
        HdPair right = DecodePair(feedback.Slice(4, 4));
        int clamps = CountFrequencyClamps(left, right);

        packet = Pro2OutputPacketMapper.BuildRaw02Packet(
            RumbleFrequencyLimits.FromEstimatedHz(left.LowHz), left.LowAmplitude,
            RumbleFrequencyLimits.FromEstimatedHz(left.HighHz), left.HighAmplitude,
            RumbleFrequencyLimits.FromEstimatedHz(right.LowHz), right.LowAmplitude,
            RumbleFrequencyLimits.FromEstimatedHz(right.HighHz), right.HighAmplitude,
            SourceTag) with
        {
            PlayerLedMask = led,
            FrequencyClamps = clamps,
            ConversionTelemetry =
                $"frequency_mapping={RumbleFrequencyLimits.Mapping} frequency_clamps={clamps}" +
                $" source_left_hz={left.LowHz},{left.HighHz} source_right_hz={right.LowHz},{right.HighHz}"
        };
        return true;
    }

    // 静音通道（振幅 0）不算钳制，否则会把「没在震动」记成频率超限。
    private static int CountFrequencyClamps(HdPair left, HdPair right)
    {
        int count = 0;
        if (left.LowAmplitude != 0 && RumbleFrequencyLimits.WouldClamp(left.LowHz)) count++;
        if (left.HighAmplitude != 0 && RumbleFrequencyLimits.WouldClamp(left.HighHz)) count++;
        if (right.LowAmplitude != 0 && RumbleFrequencyLimits.WouldClamp(right.LowHz)) count++;
        if (right.HighAmplitude != 0 && RumbleFrequencyLimits.WouldClamp(right.HighHz)) count++;
        return count;
    }

    private static ushort FrequencyOf(int code) =>
        (ushort)Math.Clamp(Math.Round(10 * Math.Pow(2, code / 32.0)), 40, 1253);

    private static ushort AmplitudeOf(int code) =>
        (ushort)(((uint)AmplitudeByCode[Math.Clamp(code, 0, AmplitudeByCode.Length - 1)] * 29000u + 32767u) / 65535u);
}
