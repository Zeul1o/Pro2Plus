using System;

namespace Pro2Plus;

/// <summary>
/// 无线输入源停止出帧时，决定虚拟手柄该收到什么。阈值与诊断标签集中在一张表里，
/// 每个分支都可单独核对；超过安全释放窗口一律回中，避免按键卡住。
/// </summary>
public static class FrameFreshnessPolicy
{
    public static readonly TimeSpan NormalInputAge = TimeSpan.FromMilliseconds(20);
    public static readonly TimeSpan JitterInputAge = TimeSpan.FromMilliseconds(33);
    public static readonly TimeSpan MissedCycleInputAge = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan AgedInputAge = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan HoldLastStateAge = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan SafeReleaseAge = TimeSpan.FromMilliseconds(750);

    private static readonly (TimeSpan Limit, string Tag)[] PassThroughBands =
    [
        (NormalInputAge, "pro2_ble_age_0_20"),
        (JitterInputAge, "pro2_ble_age_20_33"),
        (MissedCycleInputAge, "pro2_ble_age_33_50"),
        (AgedInputAge, "pro2_ble_age_50_100"),
        (HoldLastStateAge, "pro2_ble_age_gt100"),
    ];

    public static GamepadState Resolve(GamepadState latest, TimeSpan age, out string source)
    {
        foreach ((TimeSpan limit, string tag) in PassThroughBands)
        {
            if (age <= limit)
            {
                source = tag;
                return latest;
            }
        }

        if (age <= SafeReleaseAge)
        {
            source = "pro2_ble_danger_safe_hold";
            return SafeHoldAnalog(latest);
        }

        source = "neutral";
        return GamepadState.Neutral();
    }

    private static GamepadState SafeHoldAnalog(GamepadState state)
    {
        GamepadState result = state.Clone();
        result.Buttons = GamepadButtons.None;
        result.L2 = 0;
        result.R2 = 0;
        result.AccelValid = false;
        result.GyroValid = false;
        return result;
    }
}
