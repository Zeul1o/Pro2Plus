using System;

namespace Pro2Plus;

/// <summary>
/// 体感 / 触觉增强管线的开关：正式发布默认启用，非发布构建保留按参数显式开启。
/// 命令行只做一次线性扫描，不引入 LINQ 比较器分配。
/// </summary>
public static class QualityFeatureFlags
{
    public const string GyroArgument = "--gyro-quality-test";
    public const string HapticsArgument = "--haptics-quality-test";
    public const string DisableGyroArgument = "--gyro-quality-off";
    public const string DisableHapticsArgument = "--haptics-quality-off";

    public static bool GyroEnabled { get; } = ResolveEnabled(Environment.GetCommandLineArgs(),
        GyroArgument, DisableGyroArgument, SoftwareOnlyMode.IsReleaseBuild);
    public static bool HapticsEnabled { get; } = ResolveEnabled(Environment.GetCommandLineArgs(),
        HapticsArgument, DisableHapticsArgument, SoftwareOnlyMode.IsReleaseBuild);

    public static bool IsEnabled(string[] arguments, string argument)
    {
        foreach (string item in arguments)
        {
            if (string.Equals(item, argument, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool ResolveEnabled(string[] arguments, string enableArgument, string disableArgument, bool defaultEnabled) =>
        !IsEnabled(arguments, disableArgument) && (defaultEnabled || IsEnabled(arguments, enableArgument));
}
