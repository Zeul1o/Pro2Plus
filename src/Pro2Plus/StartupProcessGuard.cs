using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Pro2Plus;

public static class StartupProcessGuard
{
    private static readonly string[] ManagerNamePrefixes =
    [
        "Pro2Plus-"
    ];

    public static string LastSummary { get; private set; } = "";

    public static string CleanupConflictingProcesses()
    {
        int currentId = Environment.ProcessId;
        var stopped = new List<string>();
        var failed = new List<string>();

        foreach (Process process in Process.GetProcesses())
        {
            if (process.Id == currentId)
            {
                process.Dispose();
                continue;
            }

            string processName;
            try
            {
                processName = process.ProcessName;
            }
            catch
            {
                process.Dispose();
                continue;
            }

            string? path = SafeMainModulePath(process);
            if (!IsConflictingManager(processName) &&
                !IsManagedViiperServer(processName, path))
            {
                process.Dispose();
                continue;
            }

            try
            {
                int id = process.Id;
                process.Kill(entireProcessTree: true);
                bool exited = process.WaitForExit(2500);
                stopped.Add(
                    processName +
                    "#" + id +
                    (exited ? "" : "(kill_pending)") +
                    (string.IsNullOrWhiteSpace(path) ? "" : " " + path));
            }
            catch (Exception ex)
            {
                failed.Add(processName + "#" + process.Id + " " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                process.Dispose();
            }
        }

        LastSummary = stopped.Count == 0 && failed.Count == 0
            ? "[STARTUP_GUARD] no stale Manager/VIIPER process found."
            : "[STARTUP_GUARD] stopped=" + string.Join(" | ", stopped) +
              (failed.Count == 0 ? "" : " failed=" + string.Join(" | ", failed));
        WriteStartupLog(LastSummary);
        return LastSummary;
    }

    private static bool IsConflictingManager(string processName)
    {
        if (processName.Equals("Pro2+" , StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("Pro2+-en", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("Pro2Plus", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ManagerNamePrefixes.Any(prefix =>
            processName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsManagedViiperServer(string processName, string? path)
    {
        if (!processName.Equals("viiper-haptic", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        return path.Contains(
                   @"Pro2Plus",
                   StringComparison.OrdinalIgnoreCase) ||
               path.Contains(
                   @"tools\viiper",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeMainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteStartupLog(string summary)
    {
        // 不再单独生成 startup_guard_*.log 碎片，统一进连接诊断日志（自动轮转与清理）。
        ConnectionDiag.Raw("startup_guard " + summary);
    }
}
