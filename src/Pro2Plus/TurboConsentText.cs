using System;
using System.Collections.Generic;

namespace Pro2Plus;

/// <summary>
/// 蓝牙参数试验的用户授权文案与子进程参数组装：只负责文本和参数，不改动系统本身。
/// 试验栈必须显式授权，且参数列表按容量预分配，避免热路径扩容。
/// </summary>
public static class TurboConsentText
{
    public static bool ShowHomeAction(bool windows, int build) => windows && build >= 10240 && build < 22000;

    public static bool IsExperimental(ConnectionPlan plan) =>
        plan.Route == ConnectionRoute.Win10ExperimentalStack;

    public static string SafetyNote(ConnectionPlan plan) => IsExperimental(plan)
        ? "此 Win10 系统栈尚无实机生效证据；可备份后授权试验，不因新设备或哈希不同而直接拒绝。全局设置会影响其他 BLE 连接，是否提速以真实输入为准。"
        : plan.Route == ConnectionRoute.Win10AuditedStack
            ? "此系统栈已完成参数读取机制审查；不同蓝牙设备仍须实测。全局设置影响其他 BLE 连接，已配置不等于达到 125Hz。"
            : plan.Guidance;

    public static string Confirmation(ConnectionPlan plan, BlePresetDefinition preset) =>
        "目标：" + preset.Title + "。" + (IsExperimental(plan) ? "当前系统尚未验证，属于参数试验。" : "") +
        "\n\n将备份并修改系统全局 BLE 参数，暂时断开当前手柄，其他蓝牙设备也可能断开；无法安全重启蓝牙时，会提示正常重启 Windows。" +
        "\n\n完成后重新连接实测，不保证 125Hz。可在首页恢复原参数，不更换驱动。继续？";

    public static IReadOnlyList<string> Arguments(bool restore, ConnectionPlan plan,
        BlePresetDefinition preset, bool experimentalConsent = false)
    {
        if (restore) return new[] { BleStackPresetTool.StackRestoreArgument };
        if (!plan.CanConfigureWin10Stack)
            throw new InvalidOperationException(plan.Title + "：" + plan.Guidance);
        if (IsExperimental(plan) && !experimentalConsent)
            throw new InvalidOperationException("此系统栈须在确认框明确授权可恢复试验；当前连接未停止，未修改参数。");
        BlePresetDefinition normalized = BlePresetCatalog.Get(preset.Preset);
        var args = new List<string>(3) { BleStackPresetTool.StackApplyArgument,
            BlePresetCatalog.SelectorPrefix + normalized.Id };
        if (IsExperimental(plan)) args.Add(BleStackProfile.ExperimentalArgument);
        return args;
    }
}
