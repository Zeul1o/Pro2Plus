using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

/// <summary>
/// 旧的 manager_*.log 会话落盘已移除：连接排障统一由 <see cref="ConnectionDiag"/> 负责，
/// 它只写连接相关事件、自动轮转清理，并可直接导出回传。
///
/// 这里只保留内存中的「最近状态快照」，供主界面诊断导出拼接使用，不创建任何文件。
/// 构造函数参数与 FilePath / RecentFilePath / MaxLogBytes 保留为兼容接口，老调用点无需改动。
/// </summary>
public sealed class SessionLogWriter : IAsyncDisposable
{
    private readonly RecentEvidenceLog recent = new();
    private readonly object gate = new();
    private string recentText = "";
    private long dropped;

    public SessionLogWriter(string? directory = null, int capacity = 2048,
        long detailedLimitBytes = 0)
    {
        // 故意不碰磁盘
    }

    /// <summary>不再产生会话日志文件。</summary>
    public string FilePath => string.Empty;

    /// <summary>不再产生 recent 快照文件。</summary>
    public string RecentFilePath => string.Empty;

    public string RecentText => Volatile.Read(ref recentText);

    public static long MaxLogBytes => 0;

    public long DroppedCount => Interlocked.Read(ref dropped);

    public string? LastError => null;

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (gate)
        {
            try
            {
                recent.Add(text);
                Volatile.Write(ref recentText, recent.Snapshot);
            }
            catch
            {
                Interlocked.Increment(ref dropped);
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
