using System;
using System.Collections.Generic;
using System.Text;

namespace Pro2Plus;

/// <summary>一次参数试验（改参数前/后）的对照测量。</summary>
public sealed record BleTrialMeasurement(
    string Phase, double DurationSeconds, double Fd2Hz, double ParsedHz,
    int Fd2Count, int ParsedCount, ushort ConnectionIntervalUnits,
    string RequestStatus, BleGapStatistics Gaps, double LastInputAgeMs = double.PositiveInfinity,
    bool ConnectedAtEnd = false, GapStatisticsSummary? ExactGapSummary = null);

/// <summary>
/// 一次 BLE 诊断的完整报告。除采样结果外还带上适配器身份、系统版本与电量快照，
/// 使一份日志足以判断「是链路没跑起来，还是解析没跟上」。
/// </summary>
public sealed record Win10BleDiagnosticReport(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    double DurationSeconds,
    string ExperimentProfile,
    string WindowsProductName,
    string WindowsDisplayVersion,
    string WindowsBuild,
    string RuntimeOsVersion,
    BluetoothAdapterDiagnosticInfo Adapter,
    string ConnectedPro2Address,
    string Fd2DescriptorHandle,
    string Fd2DescriptorUuid,
    ushort RequestedFd2ReportRateHz,
    string Fd2DescriptorValueBefore,
    string Fd2DescriptorWriteStatus,
    string Fd2DescriptorValueAfter,
    int RawNotifyCount,
    int Fd2NotifyCount,
    int ParsedCount,
    double RawNotifyHz,
    double Fd2NotifyHz,
    double ParsedHz,
    BleGapStatistics Fd2NotifyGaps,
    string Verdict)
{
    public double EffectiveInputHz => Math.Min(Fd2NotifyHz, ParsedHz);
    public double ParsedRatio => Fd2NotifyHz > 0 ? ParsedHz / Fd2NotifyHz : 0;

    // 连接参数平均值高，不等于一次真正跑完的实测；缺少连续性证据一律按未通过处理，
    // 包括旧版本反序列化出来的报告。
    public bool Input125HzAccepted => IsInputAccepted(SoftwareOnlyMode.MinimumAcceptedHz);
    public bool MeasurementCompleted { get; init; }
    public bool ConnectionContinuous { get; init; }
    public bool ConnectedAtEnd { get; init; }
    public string MeasurementFailure { get; init; } = "measurement_not_verified";
    public double? LastParsedInputAgeMs { get; init; }
    public double? MaximumFd2SilenceMs { get; init; }
    public double? MaximumParsedSilenceMs { get; init; }
    public bool GapSamplesTruncated { get; init; }

    public bool MeasurementValid => MeasurementCompleted && ConnectionContinuous && ConnectedAtEnd &&
        MeasurementFailure.Length == 0 && DurationSeconds >= 10 && double.IsFinite(DurationSeconds) &&
        RawNotifyCount > 0 && Fd2NotifyCount > 0 && ParsedCount > 0 && RawNotifyCount >= Fd2NotifyCount &&
        IsFresh(LastParsedInputAgeMs) && IsFresh(MaximumFd2SilenceMs) && IsFresh(MaximumParsedSilenceMs);

    public bool IsInputAccepted(double minimumHz) => MeasurementValid && double.IsFinite(minimumHz) && minimumHz > 0 &&
        double.IsFinite(Fd2NotifyHz) && double.IsFinite(ParsedHz) &&
        Fd2NotifyHz >= minimumHz && ParsedHz >= minimumHz &&
        ParsedRatio >= BleMeasurementVerdict.MinimumParsedRatio;

    public string CompatibilityRoute { get; init; } = "unknown";
    public string CompatibilityGuidance { get; init; } = "";
    public string SystemStackSha256 { get; init; } = "unknown";
    public bool Win10StackValuesConfigured { get; init; }
    public bool PublicRequestApiAvailable { get; init; }
    public bool PublicReadApiAvailable { get; init; }
    public byte BatteryLevelAtConnection { get; init; } = GamepadState.BatteryUnknown;
    public byte BatteryLastReadLevel { get; init; } = GamepadState.BatteryUnknown;
    public bool RuntimeBatteryRefreshEnabled { get; init; }
    public string RuntimeBatteryRefreshStatus { get; init; } = "unknown";
    public string RuntimeBatterySuspensionReason { get; init; } = "";
    public double BatteryLevelAgeSeconds { get; init; }
    public ushort BatteryVoltageMillivolts { get; init; }
    public bool BatteryChargeStateKnown { get; init; }
    public bool BatteryCharging { get; init; }
    public bool BatteryExternalPower { get; init; }
    public string BatteryTransport { get; init; } = "unknown";
    public ushort ActualConnectionIntervalUnits { get; init; }
    public double ActualConnectionIntervalMs => ActualConnectionIntervalUnits * 1.25;
    public ushort ActualConnectionLatency { get; init; }
    public ushort ActualLinkTimeoutUnits { get; init; }
    public string ConnectionPreferenceStatus { get; init; } = "unknown";
    public bool Custom7p5ExperimentEnabled { get; init; }
    public string ParameterRoute { get; init; } = "not_evaluated";
    public BleTrialMeasurement? BeforeParameters { get; init; }
    public BleTrialMeasurement? AfterCustomParameters { get; init; }

    private static bool IsFresh(double? milliseconds) => milliseconds is double value &&
        double.IsFinite(value) && value >= 0 && value <= BleMeasurementVerdict.MaximumAcceptedSilenceMs;

    /// <summary>按固定前缀分组输出，供 <c>ble-diagnostic-*.log</c> 逐行落盘。</summary>
    public IEnumerable<string> ToLogLines()
    {
        yield return "[BLE_COMPATIBILITY] route=" + CompatibilityRoute +
            " bthport_sha256=" + SystemStackSha256 + " win10_configured=" + Win10StackValuesConfigured +
            " request_api=" + PublicRequestApiAvailable + " read_api=" + PublicReadApiAvailable;
        yield return "[BLE_TURBO_TEST] profile=" + ExperimentProfile +
            " duration_s=" + DiagnosticText.F2(DurationSeconds) + " verdict=" + Verdict;
        yield return "[BLE_TURBO_LINK] actual_interval_units=" + ActualConnectionIntervalUnits +
            " actual_interval_ms=" + DiagnosticText.F2(ActualConnectionIntervalMs) +
            " latency=" + ActualConnectionLatency + " timeout_units=" + ActualLinkTimeoutUnits +
            " preference_status=" + ConnectionPreferenceStatus +
            " custom_7p5_experiment=" + Custom7p5ExperimentEnabled +
            " win11_parameter_route=" + ParameterRoute;

        foreach (BleTrialMeasurement trial in Trials())
        {
            yield return "[BLE_TRIAL_COMPARE] phase=" + trial.Phase +
                " duration_s=" + DiagnosticText.F2(trial.DurationSeconds) +
                " fd2_hz=" + DiagnosticText.F2(trial.Fd2Hz) + " parsed_hz=" + DiagnosticText.F2(trial.ParsedHz) +
                " actual_interval_ms=" + DiagnosticText.F2(trial.ConnectionIntervalUnits * 1.25) +
                " p50_gap_ms=" + DiagnosticText.F2(trial.Gaps.P50Ms) +
                " p95_gap_ms=" + DiagnosticText.F2(trial.Gaps.P95Ms) +
                " request_status=" + trial.RequestStatus;
        }

        yield return "[BLE_TURBO_OS] product=\"" + WindowsProductName +
            "\" display_version=" + WindowsDisplayVersion +
            " build=" + WindowsBuild +
            " runtime=" + RuntimeOsVersion;
        yield return "[BLE_TURBO_ADAPTER] name=\"" + Adapter.Name +
            "\" pnp_id=\"" + Adapter.PnpDeviceId +
            "\" vid=" + Adapter.Vid +
            " pid=" + Adapter.Pid +
            " provider=\"" + Adapter.DriverProvider +
            "\" driver_version=" + Adapter.DriverVersion +
            " driver_date=" + Adapter.DriverDate;
        yield return "[BLE_TURBO_FD2_DESCRIPTOR] handle=" + Fd2DescriptorHandle +
            " uuid=" + Fd2DescriptorUuid +
            " requested_hz=" + RequestedFd2ReportRateHz +
            " before=" + Fd2DescriptorValueBefore +
            " write_status=" + Fd2DescriptorWriteStatus +
            " after=" + Fd2DescriptorValueAfter;
        yield return "[BLE_TURBO_RATE] raw_notify_hz=" + DiagnosticText.F2(RawNotifyHz) +
            " fd2_notify_hz=" + DiagnosticText.F2(Fd2NotifyHz) +
            " parsed_hz=" + DiagnosticText.F2(ParsedHz) +
            " raw_count=" + RawNotifyCount +
            " fd2_count=" + Fd2NotifyCount +
            " parsed_count=" + ParsedCount;
        yield return "[BLE_TURBO_INPUT_ACCEPTANCE] effective_input_hz=" + DiagnosticText.F2(EffectiveInputHz) +
            " parsed_ratio=" + DiagnosticText.F4(ParsedRatio) + " accepted_125=" + Input125HzAccepted;
        yield return "[BLE_TURBO_VALIDITY] measurement_completed=" + MeasurementCompleted +
            " connection_continuous=" + ConnectionContinuous + " connected_at_end=" + ConnectedAtEnd +
            " measurement_valid=" + MeasurementValid + " failure=" + DiagnosticText.OrNone(MeasurementFailure) +
            " last_parsed_age_ms=" + DiagnosticText.F2(LastParsedInputAgeMs) +
            " max_fd2_silence_ms=" + DiagnosticText.F2(MaximumFd2SilenceMs) +
            " max_parsed_silence_ms=" + DiagnosticText.F2(MaximumParsedSilenceMs) +
            " gap_samples_truncated=" + GapSamplesTruncated + " rate_counts=full_capture";
        yield return "[PRO2_BATTERY_DIAGNOSTIC] connection_level=" +
            DiagnosticText.BatteryLevel(BatteryLevelAtConnection) +
            " last_read_level=" + DiagnosticText.BatteryLevel(BatteryLastReadLevel) +
            " runtime_refresh=" + RuntimeBatteryRefreshEnabled +
            " refresh_status=" + RuntimeBatteryRefreshStatus +
            " suspension_reason=" + RuntimeBatterySuspensionReason +
            " level_age_s=" + DiagnosticText.F1(BatteryLevelAgeSeconds) +
            " voltage_mv=" + BatteryVoltageMillivolts + " charge_known=" + BatteryChargeStateKnown +
            " charging=" + BatteryCharging + " external_power=" + BatteryExternalPower +
            " transport=" + BatteryTransport;
        yield return "[BLE_TURBO_GAP] p50_ms=" + DiagnosticText.F2(Fd2NotifyGaps.P50Ms) +
            " p90_ms=" + DiagnosticText.F2(Fd2NotifyGaps.P90Ms) +
            " p95_ms=" + DiagnosticText.F2(Fd2NotifyGaps.P95Ms) +
            " p99_ms=" + DiagnosticText.F2(Fd2NotifyGaps.P99Ms) +
            " min_ms=" + DiagnosticText.F2(Fd2NotifyGaps.MinMs) +
            " max_ms=" + DiagnosticText.F2(Fd2NotifyGaps.MaxMs) +
            " samples=" + Fd2NotifyGaps.SampleCount;
        yield return GapDistributionLine(Fd2NotifyGaps.Distribution);
    }

    private IEnumerable<BleTrialMeasurement> Trials()
    {
        if (BeforeParameters != null) yield return BeforeParameters;
        if (AfterCustomParameters != null) yield return AfterCustomParameters;
    }

    private static string GapDistributionLine(IReadOnlyDictionary<string, int> distribution)
    {
        // 结尾保留一个空格：这一行在日志里是定长前缀 + 变长分布，缺值时不加字段即可。
        var text = new StringBuilder("[BLE_TURBO_GAP_DISTRIBUTION] ");
        bool first = true;
        foreach (KeyValuePair<string, int> pair in distribution)
        {
            if (!first) text.Append(' ');
            text.Append(pair.Key).Append('=').Append(pair.Value);
            first = false;
        }

        return text.ToString();
    }
}
