using System;

namespace Pro2Plus;

/// <summary>
/// 环境变量开关的唯一解析入口。只认 1 / true / yes / on（不区分大小写），
/// 其余一律视为关闭；空值与未设置等价。
/// </summary>
internal static class EnvironmentSwitches
{
    internal static bool IsEnabled(string name) => IsTruthy(Environment.GetEnvironmentVariable(name));

    internal static bool IsTruthy(string? value) => value != null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("on", StringComparison.OrdinalIgnoreCase));
}
