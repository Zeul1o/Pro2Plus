using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;

namespace Pro2Plus;

/// <summary>
/// 输出链路的诊断与发布节流判定。全部是纯函数，不持有会话状态、不碰设备。
/// </summary>
internal static class OutputFrameDiagnostics
{
    private const string HexDigits = "0123456789ABCDEF";

    /// <summary>报文前 8 字节的十六进制预览；一次分配，不产生中间数组与替换串。</summary>
    internal static string FormatHead(ReadOnlySpan<byte> frame)
    {
        int count = Math.Min(8, frame.Length);
        if (count == 0)
        {
            return "";
        }

        Span<char> chars = stackalloc char[count * 3 - 1];
        for (int i = 0; i < count; i++)
        {
            int offset = i * 3;
            byte value = frame[i];
            chars[offset] = HexDigits[value >> 4];
            chars[offset + 1] = HexDigits[value & 0x0F];
            if (i + 1 < count)
            {
                chars[offset + 2] = ' ';
            }
        }

        return new string(chars);
    }

    internal static double TicksToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>线性插值分位数；排序缓冲从池里租，避免热路径每秒新建数组。</summary>
    internal static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        int count = values.Count;
        if (count == 0)
        {
            return 0;
        }

        double[] sorted = ArrayPool<double>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                sorted[i] = values[i];
            }

            Array.Sort(sorted, 0, count);
            double position = (count - 1) * Math.Clamp(percentile, 0, 100) / 100.0;
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            if (lower == upper)
            {
                return sorted[lower];
            }

            double weight = position - lower;
            return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(sorted);
        }
    }

    internal static void CountAgeBucket(
        double ageMs,
        ref ulong age0To20,
        ref ulong age20To33,
        ref ulong age33To50,
        ref ulong age50To100,
        ref ulong ageOver100)
    {
        int bucket = ageMs switch
        {
            < 20 => 0,
            < 33 => 1,
            < 50 => 2,
            < 100 => 3,
            _ => 4
        };
        switch (bucket)
        {
            case 0: age0To20++; break;
            case 1: age20To33++; break;
            case 2: age33To50++; break;
            case 3: age50To100++; break;
            default: ageOver100++; break;
        }
    }

    /// <summary>限速时跳过这一帧写入：留 8% 余量，避免抖动导致速率被系统性压低。</summary>
    internal static bool ShouldSkipVirtualWrite(
        long nowTicks,
        long lastWriteTicks,
        double effectivePushTargetHz,
        double configuredPushTargetHz)
    {
        if (lastWriteTicks == 0 ||
            effectivePushTargetHz <= 0 ||
            effectivePushTargetHz >= configuredPushTargetHz - 0.1)
        {
            return false;
        }

        double minimumGapMs = 1000.0 / effectivePushTargetHz;
        return TicksToMilliseconds(nowTicks - lastWriteTicks) < minimumGapMs * 0.92;
    }

    internal static bool ShouldPublishDisconnectNeutral(
        bool sourceIsRunning,
        bool neutralAlreadyPublished) =>
        !sourceIsRunning && !neutralAlreadyPublished;

    internal static string GyroModeLabel(ImuResampleMode mode) => mode switch
    {
        ImuResampleMode.Hold250Hz => "hold_latest",
        ImuResampleMode.Source60Hz => "source_60hz_zero",
        ImuResampleMode.Scaled250Hz => "scaled_250hz",
        ImuResampleMode.Filtered250Hz => "filtered_hold",
        _ => mode.ToString()
    };

    internal static string SourceFamily(string source) =>
        source.StartsWith("pro2_ble_age_", StringComparison.Ordinal)
            ? "pro2_ble_live"
            : source;
}
