using System;
using System.IO;

namespace Pro2Plus.Connection;

/// <summary>
/// 链路层日志的兼容入口。旧代码里所有 <c>LinkDebug.Write(...)</c> 埋点现在统一转发到
/// <see cref="ConnectionDiag"/>（正式版常开、写到「我的文档\Pro2+\Logs」）。
///
/// <c>Enabled</c> 不再是「要不要记日志」，而是「要不要额外记 TICK/STICK 这类高频行」：
/// 只有环境变量 PRO2_LINK_DEBUG=1 或 exe 同目录 link-debug.on 存在时才开，避免刷屏。
/// 连接过程的关键事件（探测、打开、GATT、首帧、断开、失败）始终记录。
/// </summary>
internal static class LinkDebug
{
    /// <summary>详细模式：额外记录每拍状态。正式版默认关。</summary>
    public static bool Enabled { get; set; }

    public static string? FilePath => ConnectionDiag.CurrentFilePath;

    public static bool Requested(string baseDir)
    {
        if (Environment.GetEnvironmentVariable("PRO2_LINK_DEBUG") == "1") return true;
        try
        {
            return !string.IsNullOrEmpty(baseDir) &&
                   File.Exists(Path.Combine(baseDir, "link-debug.on"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开新会话（写分隔头 + 环境摘要）。</summary>
    public static void Open(string tag) => ConnectionDiag.StartSession(tag);

    public static void Write(string message)
    {
        if (string.IsNullOrEmpty(message)) return;

        // 高频行只在详细模式下写，其余一律进诊断日志。
        if (!Enabled &&
            (message.StartsWith("TICK ", StringComparison.Ordinal) ||
             message.StartsWith("STICK ", StringComparison.Ordinal)))
        {
            return;
        }

        ConnectionDiag.Raw(message);
    }
}
