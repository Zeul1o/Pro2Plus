using System;
using System.Diagnostics;
using System.IO;

namespace Pro2Plus;

/// <summary>
/// 清掉上一次会话残留的 VIIPER 进程。
///
/// 正常退出会自己收干净；但如果主程序被任务管理器结束、崩溃，或用户在
/// 托盘选「退出」时正好赶上有会话，VIIPER 会留下来，Windows 的设备和
/// 打印机里就多一个空手柄。这里只处理由本程序解压出来的那一份，
/// 不会碰用户自己安装的其它 VIIPER。
/// </summary>
internal static class OrphanViiperCleanup
{
    public static string LastSummary { get; private set; } = "";

    public static void Cleanup()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pro2Plus",
            "embedded");

        if (!Directory.Exists(root)) return;

        int removed = 0;
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return; }

        foreach (Process process in processes)
        {
            try
            {
                if (!process.ProcessName.StartsWith("viiper", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? path;
                try { path = process.MainModule?.FileName; }
                catch { continue; }

                if (path is null ||
                    !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
                removed++;
            }
            catch
            {
                // 进程可能刚好自己退出了，忽略即可。
            }
            finally
            {
                process.Dispose();
            }
        }

        if (removed > 0)
        {
            LastSummary = "[ORPHAN] removed_viiper_processes=" + removed;
        }
    }
}
