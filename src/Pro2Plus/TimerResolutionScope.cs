using System;
using System.Runtime.InteropServices;

namespace Pro2Plus;

/// <summary>
/// 进程级的高精度定时器周期。Begin/Dispose 必须成对调用；非 Windows 或显式关闭时
/// 返回一个「未激活」的实例，不对系统做任何改动。
/// </summary>
public sealed class TimerResolutionScope : IDisposable
{
    private const uint TimePeriodMilliseconds = 1;
    private const uint TimerrNoError = 0;
    private bool disposed;

    private TimerResolutionScope(bool active, uint result)
    {
        IsActive = active;
        Result = result;
    }

    public bool IsActive { get; }
    public uint Result { get; }

    public static TimerResolutionScope Begin(bool enabled = true)
    {
        if (!enabled || !OperatingSystem.IsWindows())
        {
            return new TimerResolutionScope(active: false, result: uint.MaxValue);
        }

        uint result = timeBeginPeriod(TimePeriodMilliseconds);
        return new TimerResolutionScope(result == TimerrNoError, result);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (IsActive) _ = timeEndPeriod(TimePeriodMilliseconds);
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint periodMilliseconds);
}
