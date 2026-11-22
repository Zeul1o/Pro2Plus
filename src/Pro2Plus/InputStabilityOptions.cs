using System;
using System.Text;

namespace Pro2Plus;

/// <summary>
/// 输入稳定性滤波器的可调参数。Normalize 把所有数值收敛到安全区间，
/// 防止手写配置或旧版本设置把阈值推到破坏手感的范围；环境变量开关只在构造时读取一次。
/// </summary>
public sealed class InputStabilityOptions
{
    public int AxisNormalDeltaThreshold { get; init; } = 500;
    public int AxisSpikeDeltaThreshold { get; init; } = 900;
    public int AxisReturnToGoodThreshold { get; init; } = 280;
    public double AxisMinConfirmMs { get; init; } = 110;
    public double AxisMaxDeltaPerMs { get; init; } = 75;
    public bool AxisRampEnabled { get; init; } = true;
    public double AxisRampMaxDeltaPerMs { get; init; } = 48;
    public double AxisIdleMinConfirmMs { get; init; } = 110;
    public double AxisActiveMinConfirmMs { get; init; } = 0;
    public double AxisFastReversalConfirmMs { get; init; } = 0;
    public double AxisIdleRampMaxDeltaPerMs { get; init; } = 48;
    public double AxisActiveRampMaxDeltaPerMs { get; init; } = 135;
    public double AxisFastReversalRampMaxDeltaPerMs { get; init; } = 220;
    public double AxisMotionActiveWindowMs { get; init; } = 140;
    public bool AxisLowLatencyMotionBypassEnabled { get; init; } = true;
    public int AxisLowLatencyBypassDeltaThreshold { get; init; } = 700;
    public double AxisLinkRecoveryGapMs { get; init; } = 80;
    public int AxisMotionDeltaThreshold { get; init; } = 180;
    public int AxisCenterCrossThreshold { get; init; } = 260;
    public double AxisInputSwallowDetectMs { get; init; } = 50;
    public bool RawIntegrityModeEnabled { get; init; } = EnvironmentSwitches.IsEnabled("PRO2_RAW_INTEGRITY_MODE");
    public bool AxisSpikeLogEnabled { get; init; } = true;
    public bool AxisSpikeRawDumpEnabled { get; init; } = true;
    public int AxisSpikeRingBufferFramesBefore { get; init; } = 8;
    public int AxisSpikeRingBufferFramesAfter { get; init; } = 8;
    public int AxisSpikeRingBufferCapacity { get; init; } = 64;
    public int AxisSpikeDumpRateLimitPer10Seconds { get; init; } = 3;

    public static InputStabilityOptions Default { get; } = new();

    public string Summary
    {
        get
        {
            // 一次 StringBuilder 成型，避免二十多次字符串拼接产生的中间对象。
            var text = new StringBuilder(640);
            text.Append("AxisNormalDeltaThreshold=").Append(AxisNormalDeltaThreshold);
            text.Append(" AxisSpikeDeltaThreshold=").Append(AxisSpikeDeltaThreshold);
            text.Append(" AxisReturnToGoodThreshold=").Append(AxisReturnToGoodThreshold);
            text.Append(" AxisMinConfirmMs=").Append(AxisMinConfirmMs.ToString("F1"));
            text.Append(" AxisMaxDeltaPerMs=").Append(AxisMaxDeltaPerMs.ToString("F1"));
            text.Append(" AxisRampEnabled=").Append(AxisRampEnabled);
            text.Append(" AxisRampMaxDeltaPerMs=").Append(AxisRampMaxDeltaPerMs.ToString("F1"));
            text.Append(" AxisIdleMinConfirmMs=").Append(AxisIdleMinConfirmMs.ToString("F1"));
            text.Append(" AxisActiveMinConfirmMs=").Append(AxisActiveMinConfirmMs.ToString("F1"));
            text.Append(" AxisFastReversalConfirmMs=").Append(AxisFastReversalConfirmMs.ToString("F1"));
            text.Append(" AxisIdleRampMaxDeltaPerMs=").Append(AxisIdleRampMaxDeltaPerMs.ToString("F1"));
            text.Append(" AxisActiveRampMaxDeltaPerMs=").Append(AxisActiveRampMaxDeltaPerMs.ToString("F1"));
            text.Append(" AxisFastReversalRampMaxDeltaPerMs=").Append(AxisFastReversalRampMaxDeltaPerMs.ToString("F1"));
            text.Append(" AxisMotionActiveWindowMs=").Append(AxisMotionActiveWindowMs.ToString("F1"));
            text.Append(" AxisLowLatencyMotionBypassEnabled=").Append(AxisLowLatencyMotionBypassEnabled);
            text.Append(" AxisLowLatencyBypassDeltaThreshold=").Append(AxisLowLatencyBypassDeltaThreshold);
            text.Append(" AxisLinkRecoveryGapMs=").Append(AxisLinkRecoveryGapMs.ToString("F1"));
            text.Append(" AxisMotionDeltaThreshold=").Append(AxisMotionDeltaThreshold);
            text.Append(" AxisCenterCrossThreshold=").Append(AxisCenterCrossThreshold);
            text.Append(" AxisInputSwallowDetectMs=").Append(AxisInputSwallowDetectMs.ToString("F1"));
            text.Append(" RawIntegrityModeEnabled=").Append(RawIntegrityModeEnabled);
            text.Append(" AxisSpikeLogEnabled=").Append(AxisSpikeLogEnabled);
            text.Append(" AxisSpikeRawDumpEnabled=").Append(AxisSpikeRawDumpEnabled);
            text.Append(" AxisSpikeRingBufferFramesBefore=").Append(AxisSpikeRingBufferFramesBefore);
            text.Append(" AxisSpikeRingBufferFramesAfter=").Append(AxisSpikeRingBufferFramesAfter);
            text.Append(" AxisSpikeRingBufferCapacity=").Append(AxisSpikeRingBufferCapacity);
            text.Append(" AxisSpikeDumpRateLimitPer10Seconds=").Append(AxisSpikeDumpRateLimitPer10Seconds);
            return text.ToString();
        }
    }

    public static InputStabilityOptions Normalize(InputStabilityOptions? options)
    {
        options ??= Default;
        return new InputStabilityOptions
        {
            AxisNormalDeltaThreshold = Clamp(options.AxisNormalDeltaThreshold, 1, GamepadState.AxisMax),
            AxisSpikeDeltaThreshold = Clamp(
                Math.Max(options.AxisSpikeDeltaThreshold, options.AxisNormalDeltaThreshold + 1),
                2, GamepadState.AxisMax),
            AxisReturnToGoodThreshold = Clamp(options.AxisReturnToGoodThreshold, 1, GamepadState.AxisMax),
            AxisMinConfirmMs = Clamp(options.AxisMinConfirmMs, 30, 250),
            AxisMaxDeltaPerMs = Clamp(options.AxisMaxDeltaPerMs, 5, 300),
            AxisRampEnabled = options.AxisRampEnabled,
            AxisRampMaxDeltaPerMs = Clamp(options.AxisRampMaxDeltaPerMs, 5, 300),
            AxisIdleMinConfirmMs = Clamp(options.AxisIdleMinConfirmMs, 30, 250),
            AxisActiveMinConfirmMs = Clamp(options.AxisActiveMinConfirmMs, 0, 90),
            AxisFastReversalConfirmMs = Clamp(options.AxisFastReversalConfirmMs, 0, 80),
            AxisIdleRampMaxDeltaPerMs = Clamp(options.AxisIdleRampMaxDeltaPerMs, 5, 300),
            AxisActiveRampMaxDeltaPerMs = Clamp(options.AxisActiveRampMaxDeltaPerMs, 20, 500),
            AxisFastReversalRampMaxDeltaPerMs = Clamp(options.AxisFastReversalRampMaxDeltaPerMs, 20, 600),
            AxisMotionActiveWindowMs = Clamp(options.AxisMotionActiveWindowMs, 30, 500),
            AxisLowLatencyMotionBypassEnabled = options.AxisLowLatencyMotionBypassEnabled,
            AxisLowLatencyBypassDeltaThreshold = Clamp(options.AxisLowLatencyBypassDeltaThreshold, 1, GamepadState.AxisMax),
            AxisLinkRecoveryGapMs = Clamp(options.AxisLinkRecoveryGapMs, 45, 500),
            AxisMotionDeltaThreshold = Clamp(options.AxisMotionDeltaThreshold, 1, GamepadState.AxisMax),
            AxisCenterCrossThreshold = Clamp(options.AxisCenterCrossThreshold, 1, GamepadState.AxisMax / 2),
            AxisInputSwallowDetectMs = Clamp(options.AxisInputSwallowDetectMs, 15, 250),
            RawIntegrityModeEnabled = options.RawIntegrityModeEnabled,
            AxisSpikeLogEnabled = options.AxisSpikeLogEnabled,
            AxisSpikeRawDumpEnabled = options.AxisSpikeRawDumpEnabled,
            AxisSpikeRingBufferFramesBefore = Clamp(options.AxisSpikeRingBufferFramesBefore, 0, 64),
            AxisSpikeRingBufferFramesAfter = Clamp(options.AxisSpikeRingBufferFramesAfter, 0, 64),
            AxisSpikeRingBufferCapacity = Clamp(options.AxisSpikeRingBufferCapacity, 32, 512),
            AxisSpikeDumpRateLimitPer10Seconds = Clamp(options.AxisSpikeDumpRateLimitPer10Seconds, 0, 60)
        };
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return min;
        return Math.Clamp(value, min, max);
    }
}
