using System;

namespace Pro2Plus;

/// <summary>
/// 纯软件发布模式的开关。环境变量名与构建常量属于对外契约，保持不变；
/// 只有明确的真值字符串才启用，其余一律视为关闭。
/// </summary>
internal static class SoftwareOnlyMode
{
    private const string EnvironmentVariable = "PRO2_PURE_SOFTWARE_133";

    public const double MinimumAcceptedHz = 125.0;
    public const double MaximumRepeatedStateRatio = 0.01;

#if PURE_SOFTWARE_RELEASE
    public const bool IsReleaseBuild = true;
#else
    public const bool IsReleaseBuild = false;
#endif

    public static bool IsEnabled => IsReleaseBuild || EnvironmentSwitches.IsEnabled(EnvironmentVariable);

    public static void EnableForCurrentProcess()
    {
        Environment.SetEnvironmentVariable(
            EnvironmentVariable,
            "1",
            EnvironmentVariableTarget.Process);
    }
}
