using System;
using System.Collections.Generic;

namespace Pro2Plus;

/// <summary>
/// 采集文件占用登记（进程内）。同一路径只允许一个写入者：两个实例同时写会互相截断，
/// 采出来的文件既不是 A 的也不是 B 的。路径比较不区分大小写，和 Windows 文件系统一致。
/// </summary>
internal static class CapturePathLease
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Claimed = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryAcquire(string fullPath)
    {
        lock (Gate) return Claimed.Add(fullPath);
    }

    public static void Release(string fullPath)
    {
        lock (Gate) Claimed.Remove(fullPath);
    }

    /// <summary>自检用：当前被占用的路径数。</summary>
    internal static int ClaimedCount
    {
        get { lock (Gate) return Claimed.Count; }
    }
}
