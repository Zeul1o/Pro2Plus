using System;

namespace Pro2Plus;

/// <summary>
/// 诊断日志里的字段格式化。集中一处，保证同名字段在各条日志里长得一样
/// （两位小数、空值写 unknown、缺省写 none）。
/// </summary>
internal static class DiagnosticText
{
    internal static string Unknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    internal static string F1(double value) => value.ToString("F1");

    internal static string F2(double value) => value.ToString("F2");

    internal static string F2(double? value) => value?.ToString("F2") ?? "";

    internal static string F4(double value) => value.ToString("F4");

    /// <summary>电量按手柄的 0-9 档输出，超出范围一律 unknown，不猜百分比。</summary>
    internal static string BatteryLevel(byte level) => level <= 9 ? level + "/9" : "unknown";

    internal static string OrNone(string value) => value.Length == 0 ? "none" : value;
}
