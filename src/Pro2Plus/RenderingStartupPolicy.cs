using System;
using System.Collections.Generic;
using System.Windows.Interop;
using System.Windows.Media;

namespace Pro2Plus;

/// <summary>
/// 进程级的 WPF 渲染偏好，必须在构造任何 UI 之前生效；不写注册表、不改驱动、不碰手柄链路。
/// SoftwareOnly 只避开 WPF 的硬件加速绘制路径，不等于禁用所有原生图形调用，
/// 也无法修复一个已经损坏的原生进程。
/// </summary>
public static class RenderingStartupPolicy
{
    public const string SoftwareArgument = "--software-rendering";
#if PRO2_COMPATIBILITY_RENDERING
    public static bool IsCompatibilityBuild => true;
#else
    public static bool IsCompatibilityBuild => false;
#endif
    public static bool SoftwareRequested { get; private set; }

    public static bool SelectSoftwareRendering(bool compatibilityBuild, IEnumerable<string> arguments)
    {
        if (compatibilityBuild) return true;
        foreach (string argument in arguments)
        {
            if (string.Equals(argument, SoftwareArgument, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static void Apply(IEnumerable<string> arguments)
    {
        SoftwareRequested = SelectSoftwareRendering(IsCompatibilityBuild, arguments);
        // 普通构建保持系统默认（包括外部覆盖），只有显式要求时才切换。
        if (SoftwareRequested) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    public static string Telemetry =>
        "build=" + (IsCompatibilityBuild ? "ui_compatible" : "standard") +
        " software_requested=" + SoftwareRequested +
        " preference=" + RenderOptions.ProcessRenderMode +
        " scope=current_process registry_writes=false controller_path=unchanged";
}
