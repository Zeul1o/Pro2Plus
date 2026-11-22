using System;
using System.Collections.Generic;
using System.Net;

namespace Pro2Plus;

internal sealed record BridgePortPair(int ApiPort, int UsbPort, string Source);

/// <summary>主视图模型的纯策略函数：日志、端口、模式与文案判定，不持有任何 UI 状态。</summary>
internal static class ViewModelPolicies
{
    internal static string StickCalibrationKey(
        string address,
        StickCalibrationProfile? profile)
    {
        string normalizedAddress = AppSettings.NormalizeBleAddress(address);
        return normalizedAddress + "|" +
               (profile == null ? "fallback" : profile.Summary);
    }

    internal static string StickCalibrationFailureText(string result)
    {
        if (result.Contains("not_started", StringComparison.Ordinal))
        {
            return "采样流程被意外中止，请使用修复后的版本重新尝试。";
        }
        if (result.Contains("insufficient_samples", StringComparison.Ordinal))
        {
            return "收到的有效手柄数据不足，请确认手柄保持连接后重试。";
        }
        if (result.Contains("incomplete_directional_travel", StringComparison.Ordinal))
        {
            return "至少一个摇杆方向没有推到底。请让左右摇杆都沿外圈完整转动。";
        }
        if (result.Contains("sticks_moved", StringComparison.Ordinal))
        {
            return "零位采样期间检测到摇杆移动，请完全松开摇杆后重试。";
        }
        return result;
    }

    internal static TimeSpan Pro2ReconnectDelayForAttempt(int failedAttemptStreak)
    {
        if (failedAttemptStreak <= 3)
        {
            return TimeSpan.FromMilliseconds(2500);
        }
        if (failedAttemptStreak <= 6)
        {
            return TimeSpan.FromSeconds(5);
        }
        if (failedAttemptStreak <= 10)
        {
            return TimeSpan.FromSeconds(10);
        }

        return TimeSpan.FromSeconds(30);
    }

    private static readonly (int Offset, string Source)[] RequestedOffsets =
    [
        (100, "requested_plus_100"),
        (200, "requested_plus_200"),
        (1000, "requested_plus_1000")
    ];
    private static readonly int[] FallbackApiPorts = [33242, 34242, 35242, 36242, 37242];

    /// <summary>候选顺序与来源标签是诊断契约；去重改用值元组，不再拼字符串键。</summary>
    internal static IReadOnlyList<BridgePortPair> BuildBridgePortCandidates(int requestedApiPort)
    {
        var candidates = new List<BridgePortPair>(1 + RequestedOffsets.Length + FallbackApiPorts.Length);
        var seen = new HashSet<(int Api, int Usb)>();

        Add(requestedApiPort, "requested");
        if (requestedApiPort != 3242)
        {
            foreach ((int offset, string source) in RequestedOffsets)
            {
                Add(requestedApiPort + offset, source);
            }
        }

        for (int i = 0; i < FallbackApiPorts.Length; i++)
        {
            Add(FallbackApiPorts[i], "fallback_" + (i + 1));
        }

        return candidates;

        void Add(int apiPort, string source)
        {
            int usbPort = DeriveUsbPort(apiPort);
            if (!IsValidTcpPort(apiPort) || !IsValidTcpPort(usbPort) || apiPort == usbPort)
            {
                return;
            }

            if (seen.Add((apiPort, usbPort)))
            {
                candidates.Add(new BridgePortPair(apiPort, usbPort, source));
            }
        }
    }

    internal static int DeriveUsbPort(int apiPort)
    {
        if (apiPort == 3242)
        {
            return 3241;
        }

        return apiPort == 1 ? 2 : apiPort - 1;
    }

    internal static bool IsValidTcpPort(int port)
    {
        return port is >= 1 and <= 65535;
    }

    internal static bool LooksLikePortConflict(string text)
    {
        string value = text ?? "";
        return value.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("only one usage of each socket address", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("forbidden by its access permissions", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("通常每个套接字地址", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("bind:", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("listen tcp", StringComparison.OrdinalIgnoreCase) &&
               value.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool LooksLikeUsbipDriverIssue(string text)
    {
        string value = text ?? "";
        return value.Contains("usbip", StringComparison.OrdinalIgnoreCase) &&
               (value.Contains("driver", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("attach", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("access", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("denied", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>日志单行化：一次遍历完成换行与引号替换，不再产生三个中间字符串。</summary>
    internal static string SanitizeForLog(string value)
    {
        string text = value ?? "";
        if (text.Length == 0)
        {
            return "";
        }

        return string.Create(text.Length, text, static (chars, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char current = source[i];
                chars[i] = current is '\r' or '\n' ? ' ' : current == '"' ? '\'' : current;
            }
        });
    }

    internal static bool IsLoopbackHost(string value)
    {
        string candidate = (value ?? "").Trim();
        if (candidate.Length == 0 ||
            candidate.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(candidate, out IPAddress? address) &&
               IPAddress.IsLoopback(address);
    }

    internal static bool LooksLikeViiperPing(string response)
    {
        return response.Contains("\"server\":\"VIIPER\"", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsMultiSlotMode(ViiperVirtualMode mode)
    {
        return mode is ViiperVirtualMode.DualSenseLike
            or ViiperVirtualMode.Pro1
            or ViiperVirtualMode.Pro2
            or ViiperVirtualMode.Xbox;
    }

    internal static ViiperDeviceProfile ProfileFor(ViiperVirtualMode mode)
    {
        return mode switch
        {
            ViiperVirtualMode.DualSenseLike => ViiperDeviceProfile.DualSenseLike,
            ViiperVirtualMode.Pro1 => ViiperDeviceProfile.Pro1,
            ViiperVirtualMode.Pro2 => ViiperDeviceProfile.Pro2,
            ViiperVirtualMode.Xbox => ViiperDeviceProfile.Xbox,
            ViiperVirtualMode.KeyMouse => ViiperDeviceProfile.KeyMouse,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    internal static ViiperVirtualMode ModeFromKey(string? key)
    {
        return AppSettings.NormalizeModeKey(key) switch
        {
            "pro1" => ViiperVirtualMode.Pro1,
            "pro2" => ViiperVirtualMode.Pro2,
            "xbox" => ViiperVirtualMode.Xbox,
            "keymouse" => ViiperVirtualMode.KeyMouse,
            _ => ViiperVirtualMode.DualSenseLike
        };
    }

    internal static string ModeKey(ViiperVirtualMode mode)
    {
        return mode switch
        {
            ViiperVirtualMode.Pro1 => "pro1",
            ViiperVirtualMode.Pro2 => "pro2",
            ViiperVirtualMode.Xbox => "xbox",
            ViiperVirtualMode.KeyMouse => "keymouse",
            _ => "dualsense"
        };
    }

    // 纯配置展示，和连接排障无关，别塞进诊断日志。
    private static readonly string[] ConnectionLogPrefixes =
    [
        "[BLE", "[USB", "[LINK", "[PRO2_BLE", "[PRO2_FIRMWARE",
        "[PRO2_RECONNECT_TIMING", "[BLUETOOTH_RESTART", "[BLE_ADAPTER"
    ];

    internal static bool IsConnectionLogLine(string line)
    {
        if (line.StartsWith("[LINK_TUNING]", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string prefix in ConnectionLogPrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    internal static string FirstLine(string text)
    {
        string value = text ?? "";
        int newline = value.IndexOf('\n');
        string head = newline < 0 ? value : value[..newline];
        return head.Replace("\r", "");
    }

    internal static string PushCadenceTelemetry(OutputRateOption option) =>
        option.SourcePaced
            ? "source_adaptive(host_poll_cap_ms=" + option.Interval.TotalMilliseconds.ToString("F1") + ")"
            : "fixed_" + option.Hz.ToString("F1") + "hz";

}
