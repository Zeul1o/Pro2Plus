using System;
using System.Collections.Generic;

namespace Pro2Plus;

/// <summary>
/// 有界的预设清单与出计划规则。只有这四套 Win11 预设，不提供任意参数构造；
/// 之前测过的偏好预设只改变尝试顺序，不跳过测量。
/// </summary>
internal static class BleAdaptivePresetCatalog
{
    internal static readonly BleAdaptivePresetCandidate[] Windows11Candidates =
    [
        new(BleAdaptivePreset.Win11Fixed7p5, "win11_fixed_7p5", "固定 7.5ms", 6, 6, 0, 600, true),
        new(BleAdaptivePreset.Win11Flexible7p5To15, "win11_flexible_7p5_15", "7.5–15ms 协商", 6, 12, 0, 600, true),
        new(BleAdaptivePreset.Win11ThroughputOptimized, "win11_throughput", "系统吞吐量优先", null, null, null, null, false),
        new(BleAdaptivePreset.Win11Fixed15, "win11_fixed_15", "固定 15ms", 12, 12, 0, 600, true)
    ];

    internal static BleAdaptivePresetPlan CreatePlan(BleAdaptiveCapabilities capabilities,
        BleAdaptiveMeasurement native, BleAdaptivePreset? preferredPreset)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(native);

        bool win10Eligible = !capabilities.Win11 &&
            (capabilities.Win10AuditedStack || capabilities.Win10ExperimentalStack);
        if (!BleAdaptiveMeasurementRules.IsValid(native, requireRequestSuccess: false))
            return new([], false, win10Eligible, "native_measurement_not_ready");
        if (BleAdaptiveMeasurementRules.MeetsTarget(native))
            return new([], true, false, "native_target_preserved");
        if (!capabilities.Win11)
            return new([], false, win10Eligible,
                capabilities.Win10AuditedStack ? "win10_audited_configuration_requires_opt_in" :
                capabilities.Win10ExperimentalStack ? "win10_experimental_configuration_requires_opt_in" :
                "win10_native_only");

        // 系统上报的间隔只是旁证，不是测量真实 FD2 或发起设备级请求的前提。
        if (!capabilities.RequestApi)
            return new([], false, false, "win11_api_unavailable");

        BleAdaptivePresetCandidate[] ordered = (BleAdaptivePresetCandidate[])Windows11Candidates.Clone();
        if (preferredPreset is { } wanted)
        {
            int index = Array.FindIndex(ordered, candidate => candidate.Preset == wanted);
            if (index > 0)
            {
                BleAdaptivePresetCandidate first = ordered[index];
                Array.Copy(ordered, 0, ordered, 1, index);
                ordered[0] = first;
            }
        }

        return new(Array.AsReadOnly(ordered), false, false, "win11_bounded_presets");
    }

    internal static bool IsKnownPreset(BleAdaptivePreset preset)
    {
        foreach (BleAdaptivePresetCandidate candidate in Windows11Candidates)
            if (candidate.Preset == preset) return true;
        return false;
    }

    /// <summary>环境身份必须逐字段可用：任何一个「unknown」都不能参与缓存命中。</summary>
    internal static bool IsKnownEnvironment(BleAdaptiveEnvironmentKey key)
    {
        if (key.WindowsBuild < 22000) return false;
        string? sha = key.BluetoothStackSha256;
        if (sha == null || sha.Length != 64) return false;
        foreach (char digit in sha)
            if (!Uri.IsHexDigit(digit)) return false;
        return IsKnownValue(key.AdapterPnpId) && IsKnownValue(key.AdapterSetFingerprint) &&
            IsKnownValue(key.DriverProvider) && IsKnownValue(key.DriverVersion) &&
            IsKnownValue(key.DriverDate) && IsKnownValue(key.PolicyRevision);
    }

    private static bool IsKnownValue(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 &&
        !value.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("unavailable", StringComparison.OrdinalIgnoreCase);
}
