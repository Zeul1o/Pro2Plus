using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

// 输入源向消费端暴露的能力。消费端按需实现/检测其中若干接口，
// 不要把所有能力塞进一个接口：BLE、有线、回放来源能提供的时序保证并不相同。

public interface IGamepadInputSource : IAsyncDisposable
{
    bool IsRunning { get; }
    string Status { get; }
    bool TryGetLatest(out GamepadState state, out TimeSpan age);
}

public interface IGamepadInputMetricsSource
{
    string MetricsSummary { get; }
}

public interface IGamepadInputRateSource
{
    double CurrentParsedRateHz { get; }
}

public interface IGamepadSequentialInputSource
{
    bool TryGetNext(out GamepadState state, out TimeSpan age);
}

// A new virtual USB session must not replay BLE frames accumulated while the
// previous device was being removed and the replacement was enumerating.
public interface IGamepadSessionInputSource
{
    int PrepareForConsumerSession();
}

// A low-latency ordered consumer receives only newly arrived source frames.
// When the source queue is empty, the virtual device owns latest-state reuse.
public interface IGamepadFreshSequentialInputSource
{
    bool TryGetNextFresh(out GamepadState state, out TimeSpan age);
}

// A real-time consumer may discard already-stale queued notifications and
// publish only the newest state. Axis and IMU values are never altered.
public interface IGamepadRealtimeInputSource
{
    bool TryGetNewest(out GamepadState state, out TimeSpan age, out int supersededCount);
}

// Single-consumer wake-up; the bounded source queue, not the signal, owns data.
public interface IGamepadSignaledInputSource
{
    Task<bool> WaitForInputAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IGamepadRuntimeTelemetrySink
{
    void ReportOutputPushRate(double actualHz);
}

public interface IGamepadOutputSink
{
    bool IsOutputReady { get; }
    bool TryWriteOutputReport(ReadOnlySpan<byte> report, out string error);
    bool TryWriteOutputPacket(Pro2OutputPacket packet, out string error) =>
        TryWriteOutputReport(packet.Report, out error);
}
