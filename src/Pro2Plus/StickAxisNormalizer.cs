using System;

namespace Pro2Plus;

/// <summary>
/// 有线和蓝牙共用的 12 位摇杆归一化：中心对齐 + 把行程展开到 0..4095。
///
/// 死区两条链路共用同一个值：设置页的「摇杆死区」（默认 2%，可调到 0）。
/// 中心必须尽量等于静止读数——死区只是兜底，不是靠它把输出「拍」回中心，
/// 所以死区内的行程完全放弃，死区外仍然 1:1 起步（UnityZone）。
/// </summary>
public static class StickAxisNormalizer
{
    /// <summary>中心附近 1:1 的原始单位区（12 位轴值，约 1.5%）。只影响精度，不是死区。</summary>
    private const double UnityZone = 64;

    public static ushort Normalize(double raw, double center, double range, int deadzone = 0)
    {
        double carry = 0;
        return Normalize(raw, center, range, deadzone, ref carry);
    }

    /// <summary>
    /// 带小数进位：把每次取整丢掉的零头留到下一帧，长期均值才等于连续值。
    /// 摄像头/摇杆的静止值往往是 2219.8 这种带小数的，不留进位就会一帧帧全部向下取整，
    /// 表现为「物理上没动，输出恒低 0.2 个单位」——鼠标每秒就慢慢跑 0.6 像素。
    /// </summary>
    public static ushort Normalize(double raw, double center, double range, int deadzone, ref double carry)
    {
        double delta = raw - center;
        bool negative = delta < 0;
        double magnitude = Math.Abs(delta);
        if (magnitude <= deadzone)
        {
            carry = 0;   // 回到中心就丢掉小数零头，免得下一次动起来先补一帧
            return GamepadState.AxisCenter;
        }

        // 死区关到 0 时也要留一道取整门槛：
        // 中心是带小数的均值（2212.5 这种），静止读数交替落在 2212/2213，减出来是 ±0.5 个单位的残差。
        // 只要中心短暂停在整数上，就会有一帧算出 2049 —— 平均下来就是「每秒恒定 1 像素」的漂移。
        // 1.5 个原始单位 = 0.07% 行程，手感上完全无感，却把「松手绝对不动」变成数学上的必然。
        if (deadzone == 0 && magnitude < 1.5)
        {
            carry = 0;
            return GamepadState.AxisCenter;
        }

        double directional = Math.Max(deadzone + 1.0, range);
        double usable = directional - deadzone;
        double target = negative
            ? GamepadState.AxisCenter
            : GamepadState.AxisMax - GamepadState.AxisCenter;

        // 零死区时，中心附近按原始单位 1:1 走（UnityZone 以内不做放大）。
        // 放大 1.3 倍会把 1 个单位的零位残差变成 1.3 个，鼠标就会一直慢慢跑；
        // 1:1 时这点残差被取整吃掉，同时保住「推多少走多少」的线性手感。
        double unity = deadzone > 0 ? 0.0 : Math.Min(UnityZone, magnitude);
        double scaled = unity + (magnitude - unity) * (target - unity) /
            Math.Max(1.0, usable - unity);
        if (scaled > target) scaled = target;

        double output = negative
            ? GamepadState.AxisCenter - scaled
            : GamepadState.AxisCenter + scaled;

        double withCarry = output + carry;
        double rounded = Math.Round(withCarry, MidpointRounding.AwayFromZero);
        carry = withCarry - rounded;
        return (ushort)Math.Clamp(rounded, 0, GamepadState.AxisMax);
    }
}
