using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Pro2Plus;

/// <summary>
/// WinRT <c>BluetoothLEPreferredConnectionParameters</c> 的 ABI 契约：
/// IID 与 getter 顺序来自 Microsoft 的 windows-rs Devices/Bluetooth 元数据。
/// </summary>
public static class BleParameterAbi
{
    public static readonly Guid ParametersIid = new("F2F44344-7372-5F7B-9B34-29C944F5A715");

    public const string RuntimeClassName =
        "Windows.Devices.Bluetooth.BluetoothLEPreferredConnectionParameters";
}

/// <summary>WinRT 投影时按这个顺序取四个连接参数。</summary>
[ComVisible(true), Guid("F2F44344-7372-5F7B-9B34-29C944F5A715"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ICustomBleParameterObject
{
    [PreserveSig] int GetIids(out uint count, out IntPtr iids);
    [PreserveSig] int GetRuntimeClassName(out IntPtr name);
    [PreserveSig] int GetTrustLevel(out int level);
    [PreserveSig] int GetLinkTimeout(out ushort value);
    [PreserveSig] int GetConnectionLatency(out ushort value);
    [PreserveSig] int GetMinConnectionInterval(out ushort value);
    [PreserveSig] int GetMaxConnectionInterval(out ushort value);
}

/// <summary>IInspectable 部分：投影先取 IID 表，再取运行期类名。</summary>
[ComVisible(true), Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IParameterInspectable
{
    [PreserveSig] int GetIids(out uint count, out IntPtr iids);
    [PreserveSig] int GetRuntimeClassName(out IntPtr name);
    [PreserveSig] int GetTrustLevel(out int level);
}

/// <summary>
/// 自建的 WinRT 参数对象（CCW）。只回放一组固定值，不读写注册表、不改驱动、不调系统 API；
/// getter 调用次数用于证明投影确实读过这些值，而不是凭空构造。
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class CustomBleParameterObject : ICustomBleParameterObject, IParameterInspectable
{
    private readonly ushort minUnits, maxUnits, latency, timeoutUnits;
    private int timeoutReads, latencyReads, minReads, maxReads;

    public CustomBleParameterObject(ushort minUnits, ushort maxUnits, ushort latency, ushort timeoutUnits)
    {
        this.minUnits = minUnits;
        this.maxUnits = maxUnits;
        this.latency = latency;
        this.timeoutUnits = timeoutUnits;
    }

    public int TimeoutReads => Volatile.Read(ref timeoutReads);
    public int LatencyReads => Volatile.Read(ref latencyReads);
    public int MinReads => Volatile.Read(ref minReads);
    public int MaxReads => Volatile.Read(ref maxReads);

    public void ResetCounters() => timeoutReads = latencyReads = minReads = maxReads = 0;

    public int GetIids(out uint count, out IntPtr iids)
    {
        count = 1;
        iids = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
        Marshal.StructureToPtr(BleParameterAbi.ParametersIid, iids, false);
        return 0;
    }

    public int GetRuntimeClassName(out IntPtr name)
    {
        const string runtimeName = BleParameterAbi.RuntimeClassName;
        return WindowsCreateString(runtimeName, (uint)runtimeName.Length, out name);
    }

    public int GetTrustLevel(out int level)
    {
        level = 0;
        return 0;
    }

    public int GetLinkTimeout(out ushort value)
    {
        Interlocked.Increment(ref timeoutReads);
        value = timeoutUnits;
        return 0;
    }

    public int GetConnectionLatency(out ushort value)
    {
        Interlocked.Increment(ref latencyReads);
        value = latency;
        return 0;
    }

    public int GetMinConnectionInterval(out ushort value)
    {
        Interlocked.Increment(ref minReads);
        value = minUnits;
        return 0;
    }

    public int GetMaxConnectionInterval(out ushort value)
    {
        Interlocked.Increment(ref maxReads);
        value = maxUnits;
        return 0;
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string text, uint length, out IntPtr hstring);
}
