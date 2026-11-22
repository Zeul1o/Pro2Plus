using System;

namespace Pro2Plus;

/// <summary>
/// 把估计的源频率映射到协议中不透明的震动控制字。该映射是未经实测标定的历史代理值，
/// 只用于保持既有输出行为，不得作为已验证的物理频率对外报告。
/// </summary>
public static class RumbleFrequencyLimits
{
    public const string Mapping = "legacy_hz_proxy_unvalidated";
    public const ushort ReferenceLowControl = 0x112;
    public const ushort ReferenceHighControl = 0x187;
    public const ushort MaximumLegacyControl = 511;

    public static ushort FromEstimatedHz(double hz, double minimumHz = 40, double maximumHz = 511)
    {
        double minimum = double.IsFinite(minimumHz) ? Math.Clamp(minimumHz, 1, MaximumLegacyControl) : 40;
        double maximum = double.IsFinite(maximumHz) ? Math.Clamp(maximumHz, minimum, MaximumLegacyControl) : MaximumLegacyControl;
        double finite = double.IsFinite(hz) ? hz : minimum;
        return (ushort)Math.Clamp(Math.Round(finite, MidpointRounding.AwayFromZero), minimum, maximum);
    }

    public static bool WouldClamp(double hz, double minimumHz = 40, double maximumHz = 511) =>
        !double.IsFinite(hz) || hz < minimumHz || hz > Math.Min(maximumHz, MaximumLegacyControl);
}
