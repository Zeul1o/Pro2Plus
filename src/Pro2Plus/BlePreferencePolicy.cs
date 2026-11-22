namespace Pro2Plus;

public enum BlePreferenceDecision
{
    Request,
    RequestPartialImprovement,
    DeferUntilMeasured,
    SkipSlowerThanLive,
    SkipIncompatiblePreset,
    SkipNoMeasuredImprovement
}

/// <summary>
/// 请求成功不等于输入速率成功：先测未经改动的 FD2，再拒绝可能把事件节奏
/// 拉长的预设。这是保守的协商策略，并不主张 BLE 每个事件只能有一条通知；
/// 只有实测的原始与解析 FD2 才能确立成功。
/// </summary>
public static class BlePreferencePolicy
{
    public static int NativeObservationMilliseconds(bool windows11, bool pure133) =>
        windows11 && pure133 ? 10000 : 1800;

    public static BlePreferenceDecision Decide(
        bool pure133, string phase, uint currentIntervalUnits,
        uint preferredMinUnits, uint preferredMaxUnits, uint preferredLatency,
        double measuredNativeHz = double.NaN)
    {
        if (pure133 && phase != "fallback")
            return BlePreferenceDecision.DeferUntilMeasured;
        if (currentIntervalUnits != 0 && preferredMaxUnits > currentIntervalUnits)
            return BlePreferenceDecision.SkipSlowerThanLive;
        if (!pure133) return BlePreferenceDecision.Request;
        if (preferredMinUnits < 6 || preferredMaxUnits < preferredMinUnits || preferredLatency != 0)
            return BlePreferenceDecision.SkipIncompatiblePreset;
        if (currentIntervalUnits != 0 && preferredMaxUnits == currentIntervalUnits)
            return BlePreferenceDecision.SkipNoMeasuredImprovement;
        if (preferredMinUnits == 6 && preferredMaxUnits == 6)
            return BlePreferenceDecision.Request;
        // 45ms → 15ms 的改善同样有用，即便它没有达到 125Hz 标准；
        // 永远不要把「请求成功」和「测量验收」混为一谈。
        if (currentIntervalUnits > preferredMaxUnits ||
            (currentIntervalUnits == 0 && double.IsFinite(measuredNativeHz) &&
                measuredNativeHz > 0 && measuredNativeHz < 800.0 / preferredMaxUnits))
            return BlePreferenceDecision.RequestPartialImprovement;
        return BlePreferenceDecision.SkipNoMeasuredImprovement;
    }
}
