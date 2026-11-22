using System;
using System.Collections.Generic;

namespace Pro2Plus;

public enum BleAdaptivePreset
{
    Native,
    Win11Fixed7p5,
    Win11Flexible7p5To15,
    Win11ThroughputOptimized,
    Win11Fixed15
}

public sealed record BleAdaptiveCapabilities(bool Win11, bool RequestApi, bool ReadApi,
    bool Win10AuditedStack = false, bool Win10ExperimentalStack = false);

public sealed record BleAdaptivePresetCandidate(BleAdaptivePreset Preset, string Id, string DisplayName,
    ushort? MinUnits, ushort? MaxUnits, ushort? Latency, ushort? TimeoutUnits, bool IsExperimental);

public sealed record BleAdaptivePresetPlan(IReadOnlyList<BleAdaptivePresetCandidate> Candidates,
    bool NativeTargetAlreadyMet, bool Win10OptInEligible, string Code);

/// <summary>Age is the age of the completed window, not the latest input packet.</summary>
public sealed record BleAdaptiveMeasurement(MeasuredTrialSample Sample,
    double CompletedAgeSeconds = 0, double? MaximumGapMs = null)
{
    public double EffectiveHz => Sample.EffectiveHz;
}

public sealed record BleAdaptivePresetDecision(bool Retain, string Code, string Summary);

/// <summary>
/// Every field participates in an exact match. An adapter family name alone is
/// not an environment identity. AdapterSetFingerprint also distinguishes multi-radio systems.
/// </summary>
public sealed record BleAdaptiveEnvironmentKey(int WindowsBuild, string BluetoothStackSha256,
    string AdapterPnpId, string AdapterSetFingerprint, string DriverProvider, string DriverVersion,
    string DriverDate, string PolicyRevision = "ble-adaptive-v1");

/// <summary>A preference to retest first, never permission to skip measurement or install/configure a driver.</summary>
public sealed record BleAdaptiveCachedPreset(BleAdaptiveEnvironmentKey Environment, BleAdaptivePreset Preset,
    BleAdaptiveMeasurement Native, BleAdaptiveMeasurement Verified, DateTimeOffset VerifiedAtUtc);

/// <summary>
/// Bounded, transport-free policy. No native API, driver, registry, pairing,
/// filesystem, timer or synthetic input operation is performed by this class.
/// 公开 API 留在这里；预设清单在 <see cref="BleAdaptivePresetCatalog"/>，
/// 测量合法性与比较在 <see cref="BleAdaptiveMeasurementRules"/>。
/// </summary>
public static class BleAdaptivePresetPolicy
{
    public const int MaximumTrials = 4;
    // 7.5ms 间隔的协议上限约 133Hz，实机通知率稳定落在 126~129。统一按 125Hz 验收：
    // 达到即采用，不再为 1~2Hz 的测量抖动去跑剩余预设，也不再区分两档门槛。
    public const double TargetHz = BleAdaptiveMeasurementRules.TargetHz;
    public const double PlayableHz = BleAdaptiveMeasurementRules.PlayableHz;
    public const double MinimumWindowSeconds = BleAdaptiveMeasurementRules.MinimumWindowSeconds;
    public const double MaximumWindowSeconds = BleAdaptiveMeasurementRules.MaximumWindowSeconds;
    public const double MaximumWindowAgeSeconds = BleAdaptiveMeasurementRules.MaximumWindowAgeSeconds;
    public const double RateNoiseToleranceHz = BleAdaptiveMeasurementRules.RateNoiseToleranceHz;
    public const int CacheLifetimeDays = 7;

    public static BleAdaptivePresetPlan CreatePlan(BleAdaptiveCapabilities capabilities,
        BleAdaptiveMeasurement native, BleAdaptivePreset? preferredPreset = null) =>
        BleAdaptivePresetCatalog.CreatePlan(capabilities, native, preferredPreset);

    public static bool CanOptimizeInApp(bool win11, bool requestApi, bool connected,
        bool busy, bool measuring, bool alreadyTried, double measuredFd2Hz) =>
        win11 && requestApi && connected && !busy && !measuring && !alreadyTried &&
        double.IsFinite(measuredFd2Hz) && measuredFd2Hz > 0 && measuredFd2Hz < TargetHz;

    public static bool IsValidMeasurement(BleAdaptiveMeasurement measurement) =>
        BleAdaptiveMeasurementRules.IsValid(measurement, requireRequestSuccess: false);

    public static bool MeetsTarget(BleAdaptiveMeasurement measurement) =>
        BleAdaptiveMeasurementRules.MeetsTarget(measurement);

    public static bool IsPlayable(BleAdaptiveMeasurement measurement) =>
        BleAdaptiveMeasurementRules.IsPlayable(measurement);

    /// <summary>Positive means left is preferable. Invalid or old windows never outrank live evidence.</summary>
    public static int CompareMeasurements(BleAdaptiveMeasurement left, BleAdaptiveMeasurement right) =>
        BleAdaptiveMeasurementRules.Compare(left, right);

    /// <summary>
    /// Compare with both native and the best retained candidate, so a later
    /// preset cannot replace a better earlier one just because it beats native.
    /// A two-Hz allowance handles measurement noise, not substantial regression.
    /// </summary>
    public static BleAdaptivePresetDecision EvaluateCandidate(BleAdaptiveMeasurement native,
        BleAdaptiveMeasurement incumbent, BleAdaptiveMeasurement candidate)
    {
        BleAdaptivePresetDecision Reject(string code, string summary) => new(false, code, summary);
        if (!BleAdaptiveMeasurementRules.IsValid(native, false) ||
            !BleAdaptiveMeasurementRules.IsValid(incumbent, false) ||
            !BleAdaptiveMeasurementRules.IsValid(candidate, true))
            return Reject("invalid_or_stale_measurement", "连接、输入新鲜度、完整窗口或解析完整性未通过，不保留该预设。");
        if (!BleAdaptiveMeasurementRules.MatchingWindows(native, candidate) ||
            !BleAdaptiveMeasurementRules.MatchingWindows(incumbent, candidate))
            return Reject("unequal_windows", "前后窗口时长不匹配，不保留该预设。");
        if (BleAdaptiveMeasurementRules.MeetsTarget(native))
            return Reject("native_target_preserved", "原生输入已达标，保持原生连接。");
        if (BleAdaptiveMeasurementRules.Regressed(native, candidate) ||
            BleAdaptiveMeasurementRules.Regressed(incumbent, candidate))
            return Reject("measured_rate_regressed", "FD2 或 Parsed 明显低于原生／已保留预设，恢复此前最佳路线。");
        if (BleAdaptiveMeasurementRules.GapRegressed(incumbent, candidate))
            return Reject("measured_stability_regressed", "通知间隙明显恶化，不以较高平均频率掩盖卡顿。");
        if (BleAdaptiveMeasurementRules.Compare(candidate, incumbent) <= 0)
            return Reject("incumbent_is_better", "真实输入未优于已保留路线，恢复此前最佳路线。");
        if (BleAdaptiveMeasurementRules.MeetsTarget(candidate))
            return new(true, "target_reached", "真实 FD2 / Parsed 已达 125Hz 标准，立即采用该预设。");

        double increase = candidate.EffectiveHz - incumbent.EffectiveHz;
        if (increase >= 3 - 1e-9 && increase >= incumbent.EffectiveHz * .05 - 1e-9)
            return new(true, BleAdaptiveMeasurementRules.IsPlayable(candidate)
                    ? "playable_improvement" : "partial_improvement",
                "真实输入明显改善，暂保留；继续尝试剩余预设，不把部分改善标为达标。");
        if (Math.Abs(increase) <= RateNoiseToleranceHz &&
            BleAdaptiveMeasurementRules.GapImproved(incumbent, candidate))
            return new(true, "stability_improvement", "频率处于测量容差内，但通知间隙明显改善，暂保留较稳定路线。");
        return Reject("no_meaningful_improvement", "没有达到可确认的频率或稳定性改善，恢复此前最佳路线。");
    }

    /// <summary>Final ten-second verify is required even after an earlier target window.</summary>
    public static BleAdaptivePresetDecision VerifyFinal(BleAdaptiveMeasurement retained,
        BleAdaptiveMeasurement final)
    {
        if (!BleAdaptiveMeasurementRules.IsValid(retained, false) ||
            !BleAdaptiveMeasurementRules.IsValid(final, false) ||
            !BleAdaptiveMeasurementRules.MatchingWindows(retained, final))
            return new(false, "final_measurement_invalid", "最终完整窗口无效，不能宣称稳定达标。");
        // 复核窗口只要仍在 125Hz 以上，就保留该预设。7.5ms 间隔下 p95 通知间隙偶发
        // 翻倍（8ms→15ms）属正常调度抖动，不足以把已经达标的连接退回 60ms 原生。
        if (BleAdaptiveMeasurementRules.MeetsTarget(final))
            return new(true, "final_target_verified", "最终窗口真实输入达到 125Hz 标准，保留该预设；后续仍持续监测。");
        if (BleAdaptiveMeasurementRules.Regressed(retained, final) ||
            BleAdaptiveMeasurementRules.GapRegressed(retained, final))
            return new(false, "final_verification_regressed", "最终复核发生降速或明显卡顿，不能宣称稳定达标。");

        // 走到这里一定未达标（达标已在上方返回），只需区分「可游玩」与「低速」。
        bool playable = BleAdaptiveMeasurementRules.IsPlayable(final);
        return new(true, playable ? "final_playable_verified" : "final_low_rate_verified",
            playable ? "最终窗口可游玩，但没有达到 125Hz 标准。" : "已记录当前最佳低速路线，不承诺最低 66Hz。");
    }

    /// <summary>
    /// 最终复核比最佳窗口略降、但仍可游玩且明显优于原生时，保留该预设。
    /// 一次调度抖动不应该把已经跑起来的连接退回 60ms 原生。
    /// </summary>
    public static BleAdaptivePresetDecision VerifyFinal(BleAdaptiveMeasurement native,
        BleAdaptiveMeasurement retained, BleAdaptiveMeasurement final)
    {
        BleAdaptivePresetDecision strict = VerifyFinal(retained, final);
        if (strict.Retain || !BleAdaptiveMeasurementRules.IsPlayable(final)) return strict;
        if (BleAdaptiveMeasurementRules.GapRegressed(retained, final) ||
            !BleAdaptiveMeasurementRules.Outranks(native, final)) return strict;
        return new(true, "final_playable_preserved",
            "最终窗口仍在 60Hz 以上且优于原连接，保留可游玩预设；未达到 125Hz 标准。");
    }

    public static bool IsCacheUsable(BleAdaptiveCachedPreset entry, BleAdaptiveEnvironmentKey current,
        DateTimeOffset nowUtc, out string code)
    {
        code = "cache_invalid";
        if (entry == null || current == null || entry.Environment == null ||
            !BleAdaptivePresetCatalog.IsKnownEnvironment(current) ||
            !BleAdaptivePresetCatalog.IsKnownEnvironment(entry.Environment)) return false;
        if (entry.Environment != current) { code = "cache_environment_changed"; return false; }
        if (!BleAdaptivePresetCatalog.IsKnownPreset(entry.Preset)) return false;

        double ageDays = (nowUtc - entry.VerifiedAtUtc).TotalDays;
        if (ageDays < 0 || ageDays > CacheLifetimeDays) { code = "cache_expired"; return false; }
        if (!EvaluateCandidate(entry.Native, entry.Native, entry.Verified).Retain)
        {
            code = "cache_not_measured_improvement";
            return false;
        }

        code = "cache_exact_environment_retest_preference";
        return true;
    }
}
