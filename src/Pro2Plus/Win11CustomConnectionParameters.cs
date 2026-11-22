using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;

namespace Pro2Plus;

/// <summary>
/// Own-object ABI route, validated on one Win11 stack/adapter tuple. Other
/// environments require explicit in-app or isolated CLI opt-in. NOT an official constructor.
/// It supplies the documented WinRT default interface, without modifying a
/// preset's memory, registry, system DLL, driver, or pairing state. The real
/// Win11 request may reject the object or clamp its values; only FD2 proves success.
/// </summary>
public sealed class Win11CustomConnectionParameters
{
    public const string EnableArgument = "--win11-7p5-abi-test";
    public const string DiagnosticArgument = "--ble-diagnostic";

    // 只接受游戏用的那几套预设：7.5ms 间隔（6 单位）、零延迟、200–600 单位的超时
    // （600 单位 = 6 秒，避开激进预设的 2 秒超时）。不是给用户的任意参数构造器。
    public const ushort MinimumIntervalUnits = 6;
    public const ushort MaximumIntervalUnits = 12;
    public const ushort MinimumTimeoutUnits = 200;
    public const ushort MaximumTimeoutUnits = 600;

    public const ushort IntervalUnits = 6;
    public const ushort Latency = 0;
    public const ushort TimeoutUnits = 600;

    public static readonly Guid ParametersIid = BleParameterAbi.ParametersIid;

    /// <summary>该组合是否属于受支持的预设范围。</summary>
    public static bool IsSupportedPreset(ushort minUnits, ushort maxUnits, ushort latency, ushort timeoutUnits) =>
        minUnits >= MinimumIntervalUnits && maxUnits >= minUnits && maxUnits <= MaximumIntervalUnits &&
        latency == Latency && timeoutUnits >= MinimumTimeoutUnits && timeoutUnits <= MaximumTimeoutUnits;

    /// <summary>需要同时给出 ABI 测试开关和诊断开关，避免误触该路线。</summary>
    public static bool IsCliRequested(IReadOnlyList<string> args) =>
        Contains(args, EnableArgument) && Contains(args, DiagnosticArgument);

    private static bool Contains(IReadOnlyList<string> args, string value)
    {
        for (int i = 0; i < args.Count; i++)
            if (string.Equals(args[i], value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private readonly CustomBleParameterObject source;
    private readonly BluetoothLEPreferredConnectionParameters projected;

    // Projection of our own IInspectable object only; no Win11 OS API is invoked here.
    // This permits an offline ABI round-trip on Win10, distinct from a real Win11 request.
#pragma warning disable CA1416
    public Win11CustomConnectionParameters(ushort minUnits = IntervalUnits, ushort maxUnits = IntervalUnits,
        ushort latency = Latency, ushort timeoutUnits = TimeoutUnits)
    {
        if (!IsSupportedPreset(minUnits, maxUnits, latency, timeoutUnits))
            throw new ArgumentOutOfRangeException(nameof(minUnits), "Unsupported bounded BLE preset.");

        source = new CustomBleParameterObject(minUnits, maxUnits, latency, timeoutUnits);
        IntPtr pointer = Marshal.GetComInterfaceForObject(source, typeof(ICustomBleParameterObject));
        try { projected = BluetoothLEPreferredConnectionParameters.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }

        if (projected.MinConnectionInterval != minUnits || projected.MaxConnectionInterval != maxUnits ||
            projected.ConnectionLatency != latency || projected.LinkTimeout != timeoutUnits)
            throw new InvalidOperationException("WinRT parameter ABI round-trip does not match the selected preset.");

        source.ResetCounters();
    }

    public BluetoothLEPreferredConnectionParameters Parameters => projected;

    /// <summary>离线回环自检：投影后的四个值，按 WinRT getter 顺序返回，避免数组下标歧义。</summary>
    public (ushort Min, ushort Max, ushort Latency, ushort Timeout) Readback() =>
        (projected.MinConnectionInterval, projected.MaxConnectionInterval,
         projected.ConnectionLatency, projected.LinkTimeout);
#pragma warning restore CA1416

    public string GetterTelemetry => "get_timeout=" + source.TimeoutReads + " get_latency=" + source.LatencyReads +
        " get_min=" + source.MinReads + " get_max=" + source.MaxReads;
}
