using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Pro2Plus;

/// <summary>
/// Windows 音频端点 COM 互操作声明。GUID、vtable 顺序、结构体布局属于平台 ABI 契约，
/// 必须与系统头文件一致；类型命名与组织方式由本项目自定。
/// </summary>
internal enum AudioFlow
{
    Render = 0,
    Capture = 1,
    All = 2
}

internal enum EndpointRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

[ComImport]
[Guid("bcde0395-e52f-467c-8e3d-c4579291692e")]
internal sealed class EndpointEnumeratorComClass
{
}

[ComImport]
[Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal sealed class DefaultPolicyComClass
{
}

[ComImport]
[Guid("a95664d2-9614-4f35-a746-de8db63617e6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(AudioFlow flow, int stateMask, out IAudioEndpointCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(AudioFlow flow, EndpointRole role, out IAudioEndpointDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IAudioEndpointDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IAudioEndpointDevice device);
}

[ComImport]
[Guid("d666063f-1587-4e43-81f1-b948e807363f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointDevice
{
    [PreserveSig]
    int Activate(
        ref Guid iid,
        int clsCtx,
        IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);

    [PreserveSig]
    int OpenPropertyStore(int accessMode, out IAudioPropertyBag properties);

    [PreserveSig]
    int GetId(out IntPtr id);

    [PreserveSig]
    int GetState(out int state);
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPropertyBag
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out AudioPropertyKey key);

    [PreserveSig]
    int GetValue(ref AudioPropertyKey key, out AudioPropertyValue value);

    [PreserveSig]
    int SetValue(ref AudioPropertyKey key, ref AudioPropertyValue value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioDefaultPolicy
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, out IntPtr waveFormat);

    [PreserveSig]
    int GetDeviceFormat(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        [MarshalAs(UnmanagedType.Bool)] bool defaultFormat,
        out IntPtr waveFormat);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName);

    [PreserveSig]
    int SetDeviceFormat(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        IntPtr waveFormat,
        IntPtr mixFormat);

    [PreserveSig]
    int GetProcessingPeriod(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        [MarshalAs(UnmanagedType.Bool)] bool defaultPeriod,
        out long defaultPeriodValue,
        out long minimumPeriodValue);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ref long period);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr mode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr mode);

    [PreserveSig]
    int GetPropertyValue(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        ref AudioPropertyKey key,
        out AudioPropertyValue value);

    [PreserveSig]
    int SetPropertyValue(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        ref AudioPropertyKey key,
        ref AudioPropertyValue value);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceName, EndpointRole role);

    [PreserveSig]
    int SetEndpointVisibility(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceName,
        [MarshalAs(UnmanagedType.Bool)] bool visible);
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioPropertyKey
{
    public Guid FormatId;
    public int PropertyId;

    public AudioPropertyKey(Guid formatId, int propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioPropertyValue
{
    private ushort variantType;
    private ushort reserved1;
    private ushort reserved2;
    private ushort reserved3;
    private IntPtr pointerValue;
    private int intValue;

    public string AsString() =>
        variantType == 31 && pointerValue != IntPtr.Zero
            ? Marshal.PtrToStringUni(pointerValue) ?? ""
            : "";

    public static void Clear(ref AudioPropertyValue value) => AudioComRuntime.ClearVariant(ref value);
}

internal static class AudioComRuntime
{
    [DllImport("ole32.dll", EntryPoint = "PropVariantClear")]
    internal static extern int ClearVariant(ref AudioPropertyValue value);

    internal static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    /// <summary>只做字符串转换；CoTaskMem 的所有权仍归调用方，由其 finally 统一释放。</summary>
    internal static string ReadCoTaskString(IntPtr pointer) =>
        pointer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(pointer) ?? "";
}

/// <summary>
/// 通过 PnP 设备树向上回溯，识别「Wireless Controller」这类通用产品名背后
/// 的手柄音频接口；只查硬件 ID，不改动任何设备状态。
/// </summary>
internal static class ControllerAudioLineage
{
    private const string HardwareIdFragment = "VID_054C&PID_0CE6";
    private const int MaxAncestors = 8;
    private const int IdBufferChars = 512;

    internal static bool DescendsFromController(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId) ||
            CfgMgrApi.LocateNode(out uint node, instanceId, 0) != 0)
        {
            return false;
        }

        var buffer = new StringBuilder(IdBufferChars);
        for (int depth = 0; depth < MaxAncestors; depth++)
        {
            buffer.Clear();
            if (CfgMgrApi.ReadNodeId(node, buffer, buffer.Capacity, 0) == 0 &&
                buffer.ToString().Contains(HardwareIdFragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (CfgMgrApi.ReadParent(out uint parent, node, 0) != 0)
            {
                break;
            }

            node = parent;
        }

        return false;
    }

    private static class CfgMgrApi
    {
        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
        internal static extern uint LocateNode(out uint node, string id, uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
        internal static extern uint ReadParent(out uint parent, uint node, uint flags);

        [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
        internal static extern uint ReadNodeId(uint node, StringBuilder id, int length, uint flags);
    }
}
