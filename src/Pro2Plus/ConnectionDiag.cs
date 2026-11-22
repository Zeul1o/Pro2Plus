using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Pro2Plus;

/// <summary>
/// 面向「回传修复」的连接专项诊断日志：只记 USB / 蓝牙连接全过程、设备档案、固件版本、
/// 失败原因与异常，不和界面日志混在一起。正式版常开，位置固定为
/// <c>我的文档\Pro2+\Logs\连接诊断-yyyyMMdd.log</c>。
///
/// 设计约束：写日志永远不能影响手柄，任何 IO 异常都吞掉并自动停用；
/// 单文件 2MB 轮转、只保留最近 7 天、目录总量上限 16MB，每 30 分钟自动清理一次。
/// </summary>
internal static class ConnectionDiag
{
    private const long MaxFileBytes = 2L * 1024 * 1024;
    private const long MaxTotalBytes = 16L * 1024 * 1024;
    private const int KeepDays = 7;
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(30);
    private static System.Threading.Timer? pruneTimer;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static string? currentPath;
    private static string? currentDay;
    private static int sessionNo;
    private static bool disabled;
    // 每 1.2 秒一次的 USB 探测会产生大量完全相同的 PROBE 行，合并成计数避免刷屏。
    private static string? lastKey;
    private static int repeatCount;

    /// <summary>日志目录（我的文档\Pro2+\Logs）。</summary>
    public static string DirectoryPath
    {
        get { try { return AppPaths.LogsDirectory; } catch { return ""; } }
    }

    /// <summary>当前会话正在写的日志文件。</summary>
    public static string CurrentFilePath => currentPath ?? "";

    /// <summary>开新会话：写一段分隔头和环境摘要，顺便清理过期日志。</summary>
    public static void StartSession(string tag)
    {
        lock (Gate)
        {
            if (disabled) return;
            try
            {
                string dir = AppPaths.LogsDirectory;
                Prune(dir);
                EnsurePruneTimer();
                sessionNo++;
                lastKey = null;
                repeatCount = 0;
                AppendRaw(Environment.NewLine +
                    "===== session=" + sessionNo + " tag=" + tag + " =====" + Environment.NewLine +
                    EnvironmentBlock());
            }
            catch { disabled = true; }
        }
    }

    // ── 写入口 ──────────────────────────────────────────────────────────

    /// <summary>普通连接步骤。</summary>
    public static void Step(string message) => Write("STEP", message);

    /// <summary>成功的关键节点。</summary>
    public static void Ok(string message) => Write("OK", message);

    /// <summary>值得注意但不致命的异常迹象（低帧率、静默、重试）。</summary>
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>连接失败：阶段 + 原始原因 + 可选异常（含 HResult 与堆栈）。</summary>
    public static void Failure(string stage, string detail, Exception? ex = null)
    {
        string extra = ex is null
            ? ""
            : " ex=" + ex.GetType().Name + " hresult=0x" + ex.HResult.ToString("X8") +
              " msg=\"" + OneLine(ex.Message) + "\"";
        Write("FAIL", stage + " detail=\"" + OneLine(detail) + "\"" + extra);
        if (ex is not null && !string.IsNullOrWhiteSpace(ex.StackTrace))
            Write("STACK", OneLine(ex.StackTrace));
    }

    /// <summary>未捕获异常。</summary>
    public static void Crash(string stage, Exception ex) => Failure("crash@" + stage, "进程异常", ex);

    /// <summary>设备档案：VID/PID、总线地址、蓝牙地址、名称、RSSI 等。</summary>
    public static void Device(string kind, string fields) => Write("DEVICE", kind + " " + OneLine(fields));

    /// <summary>设备固件 / 版本信息，source 标明来自 USB 描述符还是手柄报告。</summary>
    public static void Firmware(string source, string text) => Write("FIRMWARE", source + " " + OneLine(text));

    /// <summary>帧相关（首帧、帧率、丢帧、静默）。调用方要自己节流，别每帧写。</summary>
    public static void Frame(string message) => Write("FRAME", message);

    /// <summary>原始链路行（LinkDebug / 蓝牙 GATT 日志转发）。</summary>
    public static void Raw(string message) => Write("RAW", message);

    public static void Write(string level, string message)
    {
        string clean = OneLine(message);
        string key = level + "|" + clean;
        lock (Gate)
        {
            if (key == lastKey)
            {
                repeatCount++;
                return;
            }
            FlushRepeatsLocked();
            lastKey = key;
            AppendRaw(FormatLine(level, clean));
        }
    }

    /// <summary>必须在 Gate 内调用：把积压的重复行写成一条计数。</summary>
    private static void FlushRepeatsLocked()
    {
        if (repeatCount <= 0) return;
        AppendRaw(FormatLine("REPEAT", "count=" + repeatCount + " line=" + lastKey));
        repeatCount = 0;
    }

    private static string FormatLine(string level, string message)
    {
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string elapsed = Clock.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture).PadLeft(11);
        return now + "  +" + elapsed + "ms  t" +
               Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture).PadLeft(2) +
               "  [" + level + "] " + message + Environment.NewLine;
    }

    // ── 回传 ────────────────────────────────────────────────────────────

    /// <summary>把最近几天的连接日志连同环境摘要合成一个可直接发回的 txt，返回路径。</summary>
    public static string Export()
    {
        lock (Gate)
        {
            FlushRepeatsLocked();
            string dir = AppPaths.LogsDirectory;
            string path = Path.Combine(dir,
                "Pro2+-连接诊断回传-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            var sb = new StringBuilder();
            sb.AppendLine("# pro2plus connection diagnostics");
            sb.AppendLine("# generated=" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
            sb.AppendLine("# log_dir=" + dir);
            sb.Append(EnvironmentBlock());
            sb.AppendLine("# files");

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "连接诊断-*.log")
                    .OrderByDescending(f => f).Take(4).Reverse().ToArray();
            }
            catch { files = Array.Empty<string>(); }

            if (files.Length == 0) sb.AppendLine("# no_logs");

            foreach (string file in files)
            {
                sb.AppendLine("# file=" + Path.GetFileName(file));
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Utf8, true);
                    sb.AppendLine(reader.ReadToEnd());
                }
                catch (Exception ex)
                {
                    sb.AppendLine("# read_error=" + OneLine(ex.Message));
                }
            }

            File.WriteAllText(path, sb.ToString(), Utf8);
            return path;
        }
    }

    /// <summary>在资源管理器里打开日志目录并选中指定文件。</summary>
    public static void Reveal(string? path = null)
    {
        string dir = DirectoryPath;
        try
        {
            string args = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? "/select,\"" + path + "\""
                : "\"" + dir + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"")
                { UseShellExecute = true });
            }
            catch { }
        }
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    private static void AppendRaw(string block)
    {
        if (disabled) return;
        try
        {
            string day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (currentDay != day || currentPath is null)
            {
                currentDay = day;
                currentPath = Path.Combine(AppPaths.LogsDirectory, "连接诊断-" + day + ".log");
            }

            var info = new FileInfo(currentPath);
            if (info.Exists && info.Length > MaxFileBytes)
            {
                string rotated = Path.ChangeExtension(currentPath, ".1.log");
                try { File.Copy(currentPath, rotated, true); } catch { }
                try
                {
                    File.WriteAllText(currentPath,
                        "[ROTATED] size>2MB prev=" + Path.GetFileName(rotated) + Environment.NewLine,
                        Utf8);
                }
                catch { }
            }

            File.AppendAllText(currentPath, block, Utf8);
        }
        catch { disabled = true; }
    }

    private static string EnvironmentBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# env");
        sb.AppendLine("app_version=" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown"));
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length > 0)
                sb.AppendLine("product_version=" + (FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "unknown"));
        }
        catch { }
        sb.AppendLine("os=" + RuntimeInformation.OSDescription);
        sb.AppendLine("os_version=" + Environment.OSVersion.VersionString);
        sb.AppendLine("runtime=" + RuntimeInformation.FrameworkDescription);
        sb.AppendLine("arch=" + RuntimeInformation.ProcessArchitecture +
                      " / os_arch=" + RuntimeInformation.OSArchitecture);
        sb.AppendLine("process_id=" + Environment.ProcessId + " x64=" + Environment.Is64BitProcess);
        sb.AppendLine("admin=" + IsElevated());
        sb.AppendLine("exe=" + (Environment.ProcessPath ?? "unknown"));
        sb.AppendLine("log_dir=" + DirectoryPath);
        sb.AppendLine("# /env");
        return sb.ToString();
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// 定期清理：Logs 是程序专用目录，删掉超过 KeepDays 的日志文本；
    /// 总量超过 MaxTotalBytes 时按时间从旧到新删（保留当前正在写的文件）。
    /// 只处理日志类扩展名，不碰用户放进来的其它文件。
    /// </summary>
    private static void Prune(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-KeepDays);
            var all = new List<FileInfo>();
            foreach (string path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string ext = Path.GetExtension(path);
                    if (!ext.Equals(".log", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(path);
                    if (info.Exists) all.Add(info);
                }
                catch { }
            }
            all.Sort((a, b) => a.LastWriteTime.CompareTo(b.LastWriteTime));

            long total = 0;
            foreach (FileInfo file in all)
            {
                if (file.LastWriteTime < cutoff) { TryDelete(file); continue; }
                total += file.Length;
            }

            if (total <= MaxTotalBytes) return;
            foreach (FileInfo file in all)
            {
                if (total <= MaxTotalBytes) break;
                if (string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                long size = file.Length;
                if (TryDelete(file)) total -= size;
            }
        }
        catch { }
    }

    private static bool TryDelete(FileInfo file)
    {
        try { file.Delete(); return true; } catch { return false; }
    }

    /// <summary>进程内只建一个定时器；回调与写入共用 Gate，不会并发踩到同一个文件。</summary>
    private static void EnsurePruneTimer()
    {
        if (pruneTimer is not null) return;
        try
        {
            pruneTimer = new System.Threading.Timer(_ =>
            {
                lock (Gate)
                {
                    try { Prune(DirectoryPath); } catch { }
                }
            }, null, PruneInterval, PruneInterval);
        }
        catch { }
    }

    /// <summary>日志必须单行，换行会破坏按行检索。</summary>
    private static string OneLine(string text) =>
        (text ?? "").Replace("\r", " ").Replace('\n', ' ').Replace('\t', ' ').Trim();
}
