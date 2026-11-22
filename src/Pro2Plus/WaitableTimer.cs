using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pro2Plus;

/// <summary>
/// 可等待定时器句柄。优先 CREATE_WAITABLE_TIMER_HIGH_RESOLUTION（Win10 1803+），
/// 不支持时退回普通可等待定时器；再失败就返回 null，由调用方改走 Stopwatch。
/// 只负责创建 / 上弦 / 等一次 / 释放，截止时刻一律由调用方用 Stopwatch tick 给出。
/// </summary>
internal sealed class WaitableTimer : IDisposable
{
    internal const uint Signaled = 0x00000000;
    internal const uint Failed = 0xFFFFFFFF;

    private const uint CreateHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;

    private readonly SafeWaitHandle handle;

    private WaitableTimer(SafeWaitHandle handle) => this.handle = handle;

    public static WaitableTimer? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;

        SafeWaitHandle handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateHighResolution, TimerAllAccess);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }
        }

        return new WaitableTimer(handle);
    }

    /// <summary>按相对 Stopwatch tick 上弦，到点一次性置信号（period=0，不自动重装）。</summary>
    public void Arm(long remainingStopwatchTicks, long stopwatchFrequency)
    {
        long remainingTimeSpanTicks = Math.Max(1, checked((long)Math.Round(
            Math.Max(1, remainingStopwatchTicks) * (double)TimeSpan.TicksPerSecond / stopwatchFrequency)));
        long dueTime = -remainingTimeSpanTicks;
        if (!SetWaitableTimerEx(handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Unable to arm the high-resolution waitable timer.");
    }

    public uint WaitOnce(uint milliseconds) => WaitForSingleObject(handle, milliseconds);

    public void Dispose() => handle.Dispose();

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(
        IntPtr timerAttributes,
        string? timerName,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(
        SafeWaitHandle timer,
        ref long dueTime,
        int periodMilliseconds,
        IntPtr completionRoutine,
        IntPtr completionRoutineArgument,
        IntPtr wakeContext,
        uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
