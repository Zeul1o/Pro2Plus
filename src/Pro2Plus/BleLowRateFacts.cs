using System;

namespace Pro2Plus;

/// <summary>
/// 低速诊断的派生事实：判定链与证据文本共用同一份，避免两边各算一遍之后悄悄分叉。
/// </summary>
internal readonly record struct BleLowRateFacts(
    bool Fresh,
    BleRateStabilitySnapshot? Health,
    bool HealthObserved,
    bool Healthy,
    bool Win10,
    Win10ConfigurationAudit? Configuration,
    bool Configured,
    bool CanOfferWin10Trial,
    bool ExperimentalWin10)
{
    public static BleLowRateFacts From(Pro2RateEvidence rate, EnvironmentSnapshot compatibility,
        SystemBluetoothSnapshot? environment)
    {
        BleRateStabilitySnapshot? health = rate.Stability;
        bool observed = health is { HasValidWindow: true };
        // 没有持续监测窗口时退回单点判定：阈值必须跟初始化验收口径一致。
        // 这里原来写死 128，统一到 125Hz 之后 125–127Hz 的连接会被误判成「输入偏低」。
        bool healthy = observed
            ? health!.IsStableHighRate
            : rate.HighRate && rate.Fd2Hz >= SoftwareOnlyMode.MinimumAcceptedHz &&
              rate.ParsedHz >= SoftwareOnlyMode.MinimumAcceptedHz;
        bool win10 = compatibility.Facts.Build < 22000;
        Win10ConfigurationAudit? configuration = environment?.Win10Configuration;
        return new(
            Fresh: rate.LastInputAgeMs is >= 0 and < 500,
            Health: health,
            HealthObserved: observed,
            Healthy: healthy,
            Win10: win10,
            Configuration: configuration,
            Configured: configuration?.Configured ?? compatibility.Facts.Win10StackValuesConfigured,
            CanOfferWin10Trial: win10 && compatibility.Plan.CanConfigureWin10Stack,
            ExperimentalWin10: compatibility.Plan.Route == ConnectionRoute.Win10ExperimentalStack);
    }
}
