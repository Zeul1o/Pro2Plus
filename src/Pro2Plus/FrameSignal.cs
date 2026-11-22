using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

/// <summary>
/// 出帧信号：把所有蓝牙回调合并成一个待处理标记。虚拟会话停止后信号可能挂起数分钟，
/// 因此只在计数为零时释放，既避免 SemaphoreFullException，也不为每个回调分配异常。
/// </summary>
public sealed class FrameSignal
{
    private readonly SemaphoreSlim pending = new(0, 1);
    private readonly object gate = new();

    public void Notify()
    {
        lock (gate)
        {
            if (pending.CurrentCount == 0) pending.Release();
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken token) =>
        pending.WaitAsync(timeout, token);
}
