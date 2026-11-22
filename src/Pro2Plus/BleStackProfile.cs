using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Pro2Plus;

/// <summary>
/// 可回退的 Win10 蓝牙栈参数试验。已审查的哈希只是证据，不是准入白名单。
/// 命令行授权一次线性扫描完成，不再为每条参数构造 LINQ 迭代器。
/// </summary>
public static class BleStackProfile
{
    public const string ExperimentalArgument = "--ble-turbo-stack-experimental";
    public const string RegistrySubKey = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters";
    public const string AuditedStackSha256 =
        "05A928CA6C072FB324B637A924C7C5F377FD897918D63854C2E646F7D0D62852";
    private const string ApplyArgument = "--ble-turbo-stack-apply";
    private const string RestoreArgument = "--ble-turbo-stack-restore";

    public static IReadOnlyDictionary<string, int> Values { get; } =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(4, StringComparer.OrdinalIgnoreCase)
        {
            ["LEConnectionIntervalMin"] = 6,
            ["LEConnectionIntervalMax"] = 6,
            ["LEConnectionLatency"] = 0,
            ["LEConnectionLSTO"] = 200
        });

    public static bool IsAudited(int windowsBuild, string sha256) =>
        windowsBuild >= 19041 && windowsBuild < 22000 &&
        string.Equals(sha256, AuditedStackSha256, StringComparison.OrdinalIgnoreCase);

    public static bool ParseExperimentalConsent(IReadOnlyList<string> args)
    {
        int flags = 0;
        bool exactFlag = true;
        bool applyPresent = false;
        bool restorePresent = false;
        foreach (string argument in args)
        {
            if (argument.StartsWith(ExperimentalArgument, StringComparison.OrdinalIgnoreCase))
            {
                flags++;
                if (!string.Equals(argument, ExperimentalArgument, StringComparison.OrdinalIgnoreCase))
                    exactFlag = false;
            }
            if (string.Equals(argument, ApplyArgument, StringComparison.OrdinalIgnoreCase)) applyPresent = true;
            if (string.Equals(argument, RestoreArgument, StringComparison.OrdinalIgnoreCase)) restorePresent = true;
        }
        if (flags == 0) return false;
        if (flags != 1 || !exactFlag)
            throw new ArgumentException("实验确认参数格式无效或重复；未修改任何参数。", nameof(args));
        if (!applyPresent || restorePresent)
            throw new ArgumentException("实验确认仅能用于 Win10 系统栈应用操作；未修改任何参数。", nameof(args));
        return true;
    }
}
