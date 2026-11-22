using System;

namespace Pro2Plus;

/// <summary>
/// 一次 10 秒采样的合格性判定。全部是纯函数：不读注册表、不碰设备、不看时钟，
/// 便于离线差分自检，也让报告的 failure 字段只有一个来源。
/// </summary>
internal static class BleMeasurementVerdict
{
    /// <summary>判定「输入仍在流动」的最大允许静默毫秒数。</summary>
    internal const double MaximumAcceptedSilenceMs = 500;

    /// <summary>报告达到 125Hz（软件模式最低速率）所需的解析比例下限。</summary>
    internal const double MinimumParsedRatio = 0.95;

    /// <summary>
    /// 采样窗未通过时的原因码，通过时返回空串。顺序即优先级：
    /// 先看采样本身是否完成，再看连接是否连续，最后才看数据新鲜度。
    /// </summary>
    internal static string Failure(Win10BleDiagnosticSamples samples)
    {
        if (!samples.MeasurementCompleted) return "measurement_not_completed";
        if (samples.DurationSeconds < 10) return "measurement_window_too_short";
        if (samples.Interruption.Length > 0) return "connection_interrupted:" + samples.Interruption;
        if (!samples.ConnectedAtEnd) return "disconnected_at_end";

        double? lastParsedAge = samples.LastParsedInputAgeMs;
        double? maxFd2Silence = samples.MaximumFd2SilenceMs;
        double? maxParsedSilence = samples.MaximumParsedSilenceMs;
        if (lastParsedAge == null || maxFd2Silence == null || maxParsedSilence == null)
        {
            return "freshness_not_verified";
        }

        if (samples.RawCount == 0 || samples.Fd2Count == 0 || samples.ParsedCount == 0) return "no_live_input";
        if (!IsNonNegative(lastParsedAge.Value) || !IsNonNegative(maxFd2Silence.Value) ||
            !IsNonNegative(maxParsedSilence.Value))
        {
            return "invalid_freshness";
        }

        if (lastParsedAge.Value > MaximumAcceptedSilenceMs ||
            maxFd2Silence.Value > MaximumAcceptedSilenceMs ||
            maxParsedSilence.Value > MaximumAcceptedSilenceMs)
        {
            return "input_stalled";
        }

        return "";
    }

    /// <summary>把接收速率与解析速率归到面向用户的判定文案。</summary>
    internal static string ClassifyRates(double fd2Hz, double parsedHz)
    {
        if (!double.IsFinite(fd2Hz) || !double.IsFinite(parsedHz) || fd2Hz < 0 || parsedHz < 0)
            return "invalid_rate_measurement";
        if (fd2Hz >= 30 && parsedHz < 30)
            return "input_parse_failure_fd2_receiving_but_parsed_below_30hz";
        if (fd2Hz >= 30 && parsedHz / fd2Hz < MinimumParsedRatio)
            return "input_parse_loss_not_accepted";
        return ClassifyEffectiveRate(Math.Min(fd2Hz, parsedHz));
    }

    internal static bool Is125HzInputAccepted(double fd2Hz, double parsedHz) =>
        double.IsFinite(fd2Hz) && double.IsFinite(parsedHz) &&
        fd2Hz >= SoftwareOnlyMode.MinimumAcceptedHz && parsedHz >= SoftwareOnlyMode.MinimumAcceptedHz &&
        parsedHz / fd2Hz >= MinimumParsedRatio;

    private static string ClassifyEffectiveRate(double effectiveHz) => effectiveHz switch
    {
        >= 100 => "high_performance_success_ge_100hz",
        >= 60 => "success_ge_60hz",
        >= 50 => "clear_improvement_below_target_50_60hz",
        >= 30 => "partial_improvement_30_50hz",
        _ => "no_clear_improvement_below_30hz"
    };

    private static bool IsNonNegative(double value) => double.IsFinite(value) && value >= 0;
}
