using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Pro2Plus;

/// <summary>
/// 开机自启的唯一入口：HKCU 的 Run 键下只写一个字符串值。
/// 只作用于当前用户；读失败按「未启用」处理，不把注册表异常抛给界面。
/// </summary>
public static class LaunchAtLoginEntry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // 任务管理器的「启动」页显示的就是这个值名，用产品名而不是项目名。
    internal const string ValueName = "Pro2+";
    private const string LegacyValueName = "Pro2Plus";

    /// <summary>自启时一并写入的参数：进程起来后只保留托盘图标，不弹主界面。</summary>
    internal const string TrayStartArgument = "--tray";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key is null) return false;
            return IsUsable(key.GetValue(ValueName)) || IsUsable(key.GetValue(LegacyValueName));
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户开机启动注册表。请检查 Windows 权限策略。");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(ValueName, BuildRunValue(ResolveExecutable()), RegistryValueKind.String);
        // 老版本写的是项目名；不清掉会在「启动」页出现两条 Pro2+。
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    private static bool IsUsable(object? value) =>
        value is string text && !string.IsNullOrWhiteSpace(text);

    /// <summary>Run 键里要写的完整命令：带引号的 exe 路径 + 托盘启动参数。</summary>
    internal static string BuildRunValue(string executablePath) =>
        Quote(executablePath) + " " + TrayStartArgument;

    // 单文件发布时 ProcessPath 已是解包后的进程路径，优先用它；退回主模块路径。
    private static string ResolveExecutable()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("无法定位当前 EXE 路径，不能写入开机自启动。");
        }

        return path;
    }

    // Run 键里的路径必须带引号，否则带空格的目录会被系统截断成两个参数。
    internal static string Quote(string path) => "\"" + path.Trim().Trim('"') + "\"";
}
