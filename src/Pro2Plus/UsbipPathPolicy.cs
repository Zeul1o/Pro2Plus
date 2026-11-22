using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Pro2Plus;

/// <summary>
/// USBIP 定位的纯路径规则：注册表字符串解析、PATH 拆分、目录拼接与去重。
/// 不碰文件系统状态（除 Path.GetFullPath）、不读注册表、不起进程，
/// 因此可以离线用合成输入做逐项差分验证。
/// </summary>
public static class UsbipPathPolicy
{
    public const string ExecutableName = "usbip.exe";
    public const string BundledVersion = "v0.9.7.7";
    public const string InstallerFileName = "USBip-0.9.7.7-x64.exe";

    /// <summary>应用目录下逐级向上寻找的相对根目录，按优先级排列。</summary>
    public static readonly string[] LocalRelativeRoots =
    [
        Path.Combine("tools", "usbip-win2"),
        Path.Combine("tools", "usbip"),
        "usbip-win2",
        "usbip"
    ];

    /// <summary>内置安装包相对路径，按优先级排列。</summary>
    public static readonly string[] InstallerRelativePaths =
    [
        Path.Combine("usbip-win2", BundledVersion, InstallerFileName),
        Path.Combine("tools", "usbip-win2", BundledVersion, InstallerFileName),
        Path.Combine(BundledVersion, InstallerFileName),
        InstallerFileName
    ];

    /// <summary>Program Files 下的常见目录名。</summary>
    public static readonly string[] CommonDirectoryNames = ["USBip", "usbip-win2"];

    /// <summary>Path.Combine 的第二个参数：exe 在根目录或 bin 子目录。</summary>
    public static readonly string[] ExecutableRelativePaths =
        [ExecutableName, Path.Combine("bin", ExecutableName)];

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>展开环境变量、去首尾空白与引号；只有绝对路径才会被接受。</summary>
    public static string NormalizeAbsolutePath(string? value)
    {
        try
        {
            string text = Environment.ExpandEnvironmentVariables((value ?? "").Trim().Trim('"')).Trim();
            return Path.IsPathFullyQualified(text) ? Path.GetFullPath(text) : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>只接受文件名确实是 usbip.exe 的绝对路径，其余一律拒绝。</summary>
    public static string NormalizeExecutablePath(string? value)
    {
        string path = NormalizeAbsolutePath(value);
        return path.Length > 0 && string.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase)
            ? path
            : "";
    }

    /// <summary>
    /// 解析注册表里的 DisplayIcon / UninstallString。DisplayIcon 常带图标序号
    /// （"D:\Custom Folder\usbip.exe",0），必须先截掉再解析引号，否则会留下尾随引号。
    /// UninstallString 只用于推断同级目录，从不执行。
    /// </summary>
    public static string ParseRegisteredExecutablePath(string? value)
    {
        string text = (value ?? "").Trim();
        if (text.StartsWith('"'))
        {
            int closing = text.IndexOf('"', 1);
            if (closing < 0) return "";
            text = text.Substring(1, closing - 1);
        }
        else
        {
            int ending = text.LastIndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (ending < 0) return "";
            text = text[..(ending + 4)];
        }

        string path = NormalizeAbsolutePath(text);
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : "";
    }

    /// <summary>把 PATH 的每一段转成该目录下的 usbip.exe 候选。</summary>
    public static IReadOnlyList<string> GetPathCandidates(string? pathValue) =>
        SplitDirectories(pathValue)
            .Select(NormalizeAbsolutePath)
            .Where(directory => directory.Length > 0)
            .Select(directory => Path.Combine(directory, ExecutableName))
            .ToArray();

    /// <summary>从一条卸载记录里推出所有可能的位置：目录本身、bin 子目录、可执行文件。</summary>
    public static IReadOnlyList<string> GetInstallRecordCandidatePaths(
        string? installLocation, string? displayIcon, string? uninstallString)
    {
        var paths = new List<string>();
        AddDirectoryVariants(paths, installLocation);

        foreach (string? registered in new[] { displayIcon, uninstallString })
        {
            string executable = ParseRegisteredExecutablePath(registered);
            if (executable.Length == 0) continue;
            if (NormalizeExecutablePath(executable).Length > 0) paths.Add(executable);
            AddDirectoryVariants(paths, Path.GetDirectoryName(executable));
        }

        return paths.Distinct(PathComparer).ToArray();

        static void AddDirectoryVariants(List<string> sink, string? value)
        {
            string directory = NormalizeAbsolutePath(value);
            if (directory.Length == 0) return;
            foreach (string relative in ExecutableRelativePaths)
            {
                sink.Add(Path.Combine(directory, relative));
            }
        }
    }

    /// <summary>把 usbip 目录前置到 PATH；已经存在时原样返回，避免重复堆叠。</summary>
    public static string BuildPathWithDirectory(string currentPath, string directory)
    {
        string safeCurrentPath = currentPath ?? "";
        if (string.IsNullOrWhiteSpace(directory)) return safeCurrentPath;

        string[] parts = SplitDirectories(safeCurrentPath);
        return ContainsDirectory(parts, directory)
            ? safeCurrentPath
            : directory + Path.PathSeparator + safeCurrentPath;
    }

    public static bool ContainsDirectory(IEnumerable<string> parts, string target)
    {
        string fullTarget;
        try
        {
            fullTarget = Path.GetFullPath(target);
        }
        catch
        {
            return false;
        }

        foreach (string part in parts)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(part), fullTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // 单个非法条目不能影响其余判断。
            }
        }
        return false;
    }

    /// <summary>从应用目录向上最多 8 级，再补一组当前工作目录相对路径。</summary>
    public static IEnumerable<string> EnumerateSearchRoots(string? baseDirectory, string[] relativeRoots)
    {
        string? cursor = baseDirectory;
        for (int level = 0; level < 8 && !string.IsNullOrWhiteSpace(cursor); level++)
        {
            foreach (string relative in relativeRoots)
            {
                yield return Path.GetFullPath(Path.Combine(cursor!, relative));
            }
            cursor = Directory.GetParent(cursor!)?.FullName;
        }

        foreach (string relative in relativeRoots)
        {
            yield return Path.GetFullPath(relative);
        }
    }

    private static string[] SplitDirectories(string? value) =>
        (value ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
