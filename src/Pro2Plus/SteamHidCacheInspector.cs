using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

public sealed record SteamCacheVerdict(
    bool SteamRunning,
    bool PotentialStaleIfHid,
    string Detail,
    string? ControllerLogPath);

public sealed record SteamRestartOutcome(
    bool Success,
    bool SteamWasRunning,
    string Detail,
    string? SteamExePath);

/// <summary>
/// 处理 Steam 进程内残留的旧 If_Hid 记录：只读诊断与一次正常的退出/重启。
/// 文本判定在 SteamControllerLogScanner，这里只做进程、文件与时序。
/// 任何情况下都不会强杀 Steam。
/// </summary>
public static class SteamHidCacheInspector
{
    private const int LogTailBytes = 2 * 1024 * 1024;

    private static readonly string[] InstallCandidates = BuildInstallCandidates();

    public static SteamCacheVerdict InspectRecentIfHid(
        string currentVid,
        string currentPid,
        TimeSpan recentWindow)
    {
        Process[] processes = Process.GetProcessesByName("steam");
        try
        {
            if (processes.Length == 0)
            {
                return new SteamCacheVerdict(false, false, "steam_not_running", null);
            }

            string? logPath = LocateControllerLog(ExecutableOf(processes));
            if (logPath == null)
            {
                return new SteamCacheVerdict(true, false, "controller_log_not_found", null);
            }

            IfHidSighting? latest = SteamControllerLogScanner.FindLast(ReadTail(logPath, LogTailBytes));
            if (latest == null)
            {
                return new SteamCacheVerdict(true, false, "recent_if_hid_not_found", logPath);
            }

            string expectedVid = SteamControllerLogScanner.NormalizeHexId(currentVid);
            string expectedPid = SteamControllerLogScanner.NormalizeHexId(currentPid);
            DateTime now = DateTime.Now;
            DateTime? startedAt = EarliestStart(processes);
            bool differs = SteamControllerLogScanner.IdentityDiffers(
                latest.Vid, latest.Pid, expectedVid, expectedPid);
            bool recent = SteamControllerLogScanner.IsRecent(
                now, latest.ObservedAt, startedAt, recentWindow);
            string detail = SteamControllerLogScanner.ComposeDetail(
                latest.Vid, latest.Pid, expectedVid, expectedPid,
                latest.ObservedAt, startedAt, recent, differs);
            return new SteamCacheVerdict(true, differs && recent, detail, logPath);
        }
        finally
        {
            Release(processes);
        }
    }

    public static async Task<SteamRestartOutcome> RefreshSteamProcessAsync(
        CancellationToken cancellationToken)
    {
        Process[] processes = Process.GetProcessesByName("steam");
        try
        {
            if (processes.Length == 0)
            {
                return new SteamRestartOutcome(
                    true, false, "steam_not_running_no_cache_to_refresh", FirstExistingInstall());
            }

            string? executable = ExecutableOf(processes) ?? FirstExistingInstall();
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                return new SteamRestartOutcome(false, true, "steam_exe_not_found", executable);
            }

            int[] originalPids = IdsOf(processes);
            StartDetached(executable, "-shutdown");
            bool exited = await WaitForExitAsync(
                originalPids, TimeSpan.FromSeconds(20), cancellationToken);
            if (!exited)
            {
                return new SteamRestartOutcome(
                    false, true, "steam_shutdown_timeout_no_force_kill", executable);
            }

            await Task.Delay(500, cancellationToken);
            StartDetached(executable, null);
            return new SteamRestartOutcome(
                true, true, "steam_restarted_clean_controller_process_cache", executable);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SteamRestartOutcome(
                false,
                processes.Length > 0,
                ex.GetType().Name + ": " + SteamControllerLogScanner.OneLine(ex.Message),
                ExecutableOf(processes));
        }
        finally
        {
            Release(processes);
        }
    }

    private static async Task<bool> WaitForExitAsync(
        int[] processIds,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool alive = false;
            foreach (int pid in processIds)
            {
                try
                {
                    using Process process = Process.GetProcessById(pid);
                    if (!process.HasExited)
                    {
                        alive = true;
                        break;
                    }
                }
                catch (ArgumentException)
                {
                    // 进程已退出，视为不再存活。
                }
            }

            if (!alive)
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private static void StartDetached(string executable, string? arguments)
    {
        var start = new ProcessStartInfo { FileName = executable, UseShellExecute = true };
        if (arguments != null)
        {
            start.Arguments = arguments;
        }

        Process.Start(start)?.Dispose();
    }

    private static string? ExecutableOf(IEnumerable<Process> processes)
    {
        foreach (Process process in processes)
        {
            try
            {
                string? path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
                // 权限不足或进程刚退出：换下一个。
            }
        }

        return null;
    }

    private static DateTime? EarliestStart(IEnumerable<Process> processes)
    {
        DateTime? earliest = null;
        foreach (Process process in processes)
        {
            try
            {
                DateTime startedAt = process.StartTime;
                if (!earliest.HasValue || startedAt < earliest.Value)
                {
                    earliest = startedAt;
                }
            }
            catch
            {
                // 取不到启动时间时按「未知」处理。
            }
        }

        return earliest;
    }

    private static int[] IdsOf(Process[] processes)
    {
        var ids = new int[processes.Length];
        for (int i = 0; i < processes.Length; i++)
        {
            ids[i] = processes[i].Id;
        }

        return ids;
    }

    private static string? FirstExistingInstall()
    {
        foreach (string candidate in InstallCandidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? LocateControllerLog(string? executable)
    {
        var candidates = new List<string>(InstallCandidates.Length + 1);
        AddLogCandidate(candidates, executable);
        foreach (string install in InstallCandidates)
        {
            if (File.Exists(install))
            {
                AddLogCandidate(candidates, install);
                break;
            }
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void AddLogCandidate(List<string> candidates, string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        string? root = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        string log = Path.Combine(root, "logs", "controller.txt");
        foreach (string existing in candidates)
        {
            if (string.Equals(existing, log, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        candidates.Add(log);
    }

    private static string ReadTail(string path, int maxBytes)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(Math.Max(0, stream.Length - maxBytes), SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    private static string[] BuildInstallCandidates()
    {
        string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string x64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return
        [
            Path.Combine(x86, "Steam", "steam.exe"),
            Path.Combine(x64, "Steam", "steam.exe")
        ];
    }

    private static void Release(Process[] processes)
    {
        foreach (Process process in processes)
        {
            process.Dispose();
        }
    }
}
