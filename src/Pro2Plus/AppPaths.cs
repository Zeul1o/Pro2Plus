using System;
using System.IO;

namespace Pro2Plus;

/// <summary>
/// 本机所有用户配置的落盘位置：<c>我的文档\Pro2+\</c>。
/// 不再写 exe 同目录——从下载目录/U 盘直接运行时那里往往不可写，也容易被清理工具删掉。
/// 文档目录不可用（被策略禁用、目录只读）时退回 <c>%LOCALAPPDATA%\Pro2+\</c>，最后退回临时目录。
/// </summary>
internal static class AppPaths
{
    private const string FolderName = "Pro2+";

    private static readonly Lazy<string> configDirectory = new(ResolveConfigDirectory);
    private static readonly Lazy<string> logsDirectory = new(ResolveLogsDirectory);

    /// <summary>配置根目录（首次访问时会创建）。</summary>
    public static string ConfigDirectory => configDirectory.Value;

    /// <summary>日志根目录（配置目录下的 Logs，首次访问时会创建）。</summary>
    public static string LogsDirectory => logsDirectory.Value;

    /// <summary>配置根目录下的一个文件路径。</summary>
    public static string ConfigFile(string fileName) => Path.Combine(ConfigDirectory, fileName);

    /// <summary>日志目录下的一个文件路径。</summary>
    public static string LogFile(string fileName) => Path.Combine(LogsDirectory, fileName);

    private static string ResolveLogsDirectory()
    {
        try
        {
            string candidate = Path.Combine(ConfigDirectory, "Logs");
            Directory.CreateDirectory(candidate);
            return candidate;
        }
        catch
        {
            string fallback = Path.Combine(Path.GetTempPath(), FolderName, "Logs");
            try { Directory.CreateDirectory(fallback); } catch { }
            return fallback;
        }
    }

    private static string ResolveConfigDirectory()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
        {
            string candidate = Path.Combine(documents, FolderName);
            if (TryEnsure(candidate)) return candidate;
        }

        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
        if (TryEnsure(local)) return local;

        return Path.Combine(Path.GetTempPath(), FolderName);
    }

    private static bool TryEnsure(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
