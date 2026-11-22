using System;

namespace Pro2Plus;

/// <summary>
/// 主报告 0x09 里的电量字节解析（位于载荷第 1 字节），与 FD2 报告的计数字节无关。
/// 百分比只是给需要数值的消费者用的近似换算，界面优先显示真实的档位。
/// </summary>
public readonly record struct BatteryLevelReading(byte RawPower)
{
    public byte Level => (byte)((RawPower >> 2) & 0x0f);
    public bool HasLevel => Level <= 9;
    public bool ExternalPower => (RawPower & 1) != 0;
    public bool Charging => (RawPower & 2) != 0;
    public byte ApproximatePercent => HasLevel
        ? (byte)((Level * 100 + 4) / 9) : GamepadState.BatteryUnknown;

    public static bool TryDecodePrimary(ReadOnlySpan<byte> payload, out BatteryLevelReading status)
    {
        status = default;
        if (payload.Length < 11) return false;
        status = new BatteryLevelReading(payload[1]);
        return true;
    }

    public void ApplyTo(GamepadState state)
    {
        state.BatteryLevel = HasLevel ? Level : GamepadState.BatteryUnknown;
        state.BatteryPercent = ApproximatePercent;
        state.BatteryPercentApproximate = HasLevel;
        state.BatteryPowerKnown = true;
        state.BatteryExternalPower = ExternalPower;
        state.BatteryCharging = Charging;
    }

    public string Telemetry
    {
        get
        {
            string level = HasLevel ? Level + "/9" : "unknown";
            string percent = HasLevel ? ApproximatePercent.ToString() : "unknown";
            string external = ExternalPower.ToString().ToLowerInvariant();
            string charging = Charging.ToString().ToLowerInvariant();
            return "raw_power=0x" + RawPower.ToString("X2") + " level=" + level +
                " percent_approx=" + percent +
                " external_power=" + external + " charging=" + charging;
        }
    }

    public static string Display(GamepadState state, bool fresh, bool levelAtConnection = false)
    {
        if (!fresh) return "电量未知";
        string charge = state.BatteryPowerKnown
            ? state.BatteryCharging ? " · 充电中" : state.BatteryExternalPower ? " · 外接电源" : ""
            : "";
        if (state.BatteryLevel <= 9)
            return state.BatteryLevel + "/9 格" + (levelAtConnection ? "（连接时）" : "") + charge;
        if (state.BatteryPercent <= 100)
            return (state.BatteryPercentApproximate ? "约 " : "") + state.BatteryPercent + "%" + charge;
        return "电量未知" + charge;
    }
}
