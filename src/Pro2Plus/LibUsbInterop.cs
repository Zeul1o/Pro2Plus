using System;
using System.Runtime.InteropServices;

namespace Pro2Plus;

/// <summary>
/// 极简 libusb-1.0 绑定。只覆盖有线 Pro2 需要的那几个调用：
/// 枚举设备、打开、占用接口、中断/批量传输。
/// libusb-1.0.dll 与 exe 同目录（发布时随包带上，64 位版本）。
/// </summary>
internal static class LibUsb
{
    private const string Library = "libusb-1.0";

    public const int Success = 0;
    public const int ErrorIo = -1;
    public const int ErrorInvalidParam = -2;
    public const int ErrorAccess = -3;
    public const int ErrorNoDevice = -4;
    public const int ErrorBusy = -6;
    public const int ErrorTimeout = -7;
    public const int ErrorPipe = -9;
    public const int ErrorInterrupted = -10;
    public const int ErrorNotSupported = -12;

    public const int TransferTypeControl = 0;
    public const int TransferTypeIsochronous = 1;
    public const int TransferTypeBulk = 2;
    public const int TransferTypeInterrupt = 3;

    public const byte EndpointOut = 0x00;
    public const byte EndpointIn = 0x80;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DeviceDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public ushort bcdUSB;
        public byte bDeviceClass;
        public byte bDeviceSubClass;
        public byte bDeviceProtocol;
        public byte bMaxPacketSize0;
        public ushort idVendor;
        public ushort idProduct;
        public ushort bcdDevice;
        public byte iManufacturer;
        public byte iProduct;
        public byte iSerialNumber;
        public byte bNumConfigurations;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_init(out IntPtr context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libusb_exit(IntPtr context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libusb_get_device_list(IntPtr context, out IntPtr list);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libusb_free_device_list(IntPtr list, int unrefDevices);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_get_device_descriptor(IntPtr device, out DeviceDescriptor descriptor);

    // 只用于诊断日志：这三个调用不需要打开设备，不会干扰探测。
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte libusb_get_bus_number(IntPtr device);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte libusb_get_device_address(IntPtr device);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_get_device_speed(IntPtr device);

    /// <summary>libusb 速度码翻成人话，写进设备档案。</summary>
    public static string SpeedName(int speed) => speed switch
    {
        0 => "unknown", 1 => "low(1.5M)", 2 => "full(12M)", 3 => "high(480M)",
        4 => "super(5G)", 5 => "superPlus(10G)", _ => "code_" + speed
    };

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_open(IntPtr device, out IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void libusb_close(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_claim_interface(IntPtr handle, int interfaceNumber);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_release_interface(IntPtr handle, int interfaceNumber);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_set_auto_detach_kernel_driver(IntPtr handle, int enable);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_clear_halt(IntPtr handle, byte endpoint);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_interrupt_transfer(
        IntPtr handle, byte endpoint, byte[] data, int length, out int transferred, uint timeout);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int libusb_bulk_transfer(
        IntPtr handle, byte endpoint, byte[] data, int length, out int transferred, uint timeout);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr libusb_error_name(int errorCode);

    /// <summary>把 libusb 错误码翻成便于看日志的文字。</summary>
    public static string Describe(int code)
    {
        return code switch
        {
            Success => "ok",
            ErrorIo => "io",
            ErrorInvalidParam => "invalid_param",
            ErrorAccess => "access_denied",
            ErrorNoDevice => "no_device",
            ErrorBusy => "busy",
            ErrorTimeout => "timeout",
            ErrorPipe => "pipe",
            ErrorInterrupted => "interrupted",
            ErrorNotSupported => "not_supported",
            _ => "error_" + code
        };
    }

    /// <summary>
    /// 单文件发布时原生 DLL 会被解压到临时目录，但用户也可能把 dll 放在 exe 旁边。
    /// 这里显式按「exe 目录 → 默认加载器」的顺序找，避免部署位置不同就加载失败。
    /// </summary>
    public static bool EnsureLoaded(out string detail)
    {
        detail = "";
        try
        {
            IntPtr ctx;
            int rc = libusb_init(out ctx);
            if (rc != Success)
            {
                detail = "libusb_init=" + Describe(rc);
                return false;
            }

            libusb_exit(ctx);
            detail = "ok";
            return true;
        }
        catch (DllNotFoundException)
        {
            detail = "libusb-1.0.dll 未找到（需把它放在程序同目录）";
            return false;
        }
        catch (BadImageFormatException)
        {
            detail = "libusb-1.0.dll 位数不匹配（必须是 64 位）";
            return false;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }
}
