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
    private const string ValueName = "Pro2Plus";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
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
            return;
        }

        key.SetValue(ValueName, Quote(ResolveExecutable()), RegistryValueKind.String);
    }

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
