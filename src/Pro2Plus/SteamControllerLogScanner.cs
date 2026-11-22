using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Pro2Plus;

public sealed record IfHidSighting(string Vid, string Pid, DateTime ObservedAt);

/// <summary>
/// Steam 控制器日志的纯文本分析：只处理字符串，不碰进程与磁盘。
/// 时间戳格式、块结构与判定式都集中在这里，便于离线差分。
/// </summary>
internal static class SteamControllerLogScanner
{
    internal const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    // Steam 在 controller.txt 里以「Local Device Found + type + Product: If_Hid」
    // 三段记录一次枚举；下面的片段是日志格式本身，不是第三方实现代码。
    private const string FoundLine =
        @"\[(?<time>\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})[^\]]*\]\s+Local Device Found\s*\r?\n";
    private const string IdentityLine =
        @"\s*type:\s*(?<vid>[0-9a-fA-F]{4})\s+(?<pid>[0-9a-fA-F]{4})";
    private const string ProductLine =
        @"(?:(?:\r?\n)[^\r\n]*){0,7}?Product:\s*If_Hid\s*(?:\r?\n|$)";

    private static readonly Regex IfHidBlock = new(
        FoundLine + IdentityLine + ProductLine,
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 只取最后一个匹配块（与旧行为一致：末块时间不可解析就当作没有观测，
    /// 不回退到更早的块）。反向索引避免把整份日志的所有匹配物化进 LINQ 迭代器。
    /// </summary>
    internal static IfHidSighting? FindLast(string? controllerLogText)
    {
        MatchCollection matches = IfHidBlock.Matches(controllerLogText ?? "");
        if (matches.Count == 0)
        {
            return null;
        }

        Match last = matches[matches.Count - 1];
        if (!DateTime.TryParseExact(
                last.Groups["time"].Value,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime observedAt))
        {
            return null;
        }

        return new IfHidSighting(
            last.Groups["vid"].Value.ToUpperInvariant(),
            last.Groups["pid"].Value.ToUpperInvariant(),
            observedAt);
    }

    internal static string NormalizeHexId(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return text.PadLeft(4, '0').ToUpperInvariant();
    }

    /// <summary>观测是否落在「本次 Steam 进程启动之后、且仍算近期」的窗口内。</summary>
    internal static bool IsRecent(
        DateTime now,
        DateTime observedAt,
        DateTime? steamStartedAt,
        TimeSpan window) =>
        now - observedAt <= window &&
        observedAt <= now.AddMinutes(1) &&
        (!steamStartedAt.HasValue || observedAt >= steamStartedAt.Value.AddSeconds(-2));

    internal static bool IdentityDiffers(
        string staleVid, string stalePid, string expectedVid, string expectedPid) =>
        !string.Equals(staleVid, expectedVid, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(stalePid, expectedPid, StringComparison.OrdinalIgnoreCase);

    internal static string ComposeDetail(
        string staleVid,
        string stalePid,
        string expectedVid,
        string expectedPid,
        DateTime observedAt,
        DateTime? steamStartedAt,
        bool recent,
        bool identityDiffers) =>
        "if_hid=" + staleVid + ":" + stalePid +
        " current=" + expectedVid + ":" + expectedPid +
        " observed=" + observedAt.ToString(TimestampFormat) +
        " steam_started=" + (steamStartedAt?.ToString(TimestampFormat) ?? "unknown") +
        " recent=" + Bool(recent) +
        " identity_mismatch=" + Bool(identityDiffers);

    internal static string OneLine(string? value) =>
        (value ?? "").Replace("\r", " ").Replace("\n", " ");

    private static string Bool(bool value) => value ? "true" : "false";
}
