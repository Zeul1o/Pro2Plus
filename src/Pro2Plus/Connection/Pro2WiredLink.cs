using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus.Connection;

/// <summary>
/// Switch 2 Pro（057E:2069）的 USB 有线链路。
///
/// 打开设备、初始化命令、报告解析和震动写回都交给
/// <see cref="Pro2WiredProtocol"/>（SDL3 协议，本机实测 498.7Hz）。
/// 这一层只补三件事：轻量探测（不打开设备）、读线程、把帧抛给上层。
///
/// 设备在 Windows 上由 libusbK/WinUSB 接管，不在 HIDClass 里，
/// 所以 hid.dll 枚举不到，必须走 libusb。
/// </summary>
public sealed class Pro2WiredLink : IDisposable
{
    public const ushort VendorId = 0x057E;
    public const ushort ProductId = 0x2069;

    public const int ReportLength = 64;

    private readonly object _gate = new();
    // USB 探测每 1.2 秒一次：只有设备列表真的变了才写档案，避免日志刷屏。
    private static readonly object ProbeGate = new();
    private static string? lastUsbSignature;
    private Pro2WiredProtocol? _pad;

    /// <summary>震动强度增益：Open 时带入新建的协议，改设置时即时推给已开的协议。</summary>
    private double _rumbleGain = 1.0;

    internal void SetRumbleGain(double gain)
    {
        _rumbleGain = double.IsFinite(gain) ? Math.Clamp(gain, 0, 3) : 1.0;
        _pad?.SetRumbleGain(_rumbleGain);
    }

    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private long _reportCount;

    /// <summary>收到一帧原始报告（调用方不要长期持有 buffer）。</summary>
    public event Action<byte[], long>? ReportReceived;

    /// <summary>链路意外断开时触发，参数是原因。</summary>
    public event Action<string>? Closed;

    public bool IsOpen => _pad is not null;

    /// <summary>累计收到的报告数（单帧丢失不修正）。</summary>
    public long ReportCount => Interlocked.Read(ref _reportCount);

    /// <summary>底层协议对象；连接成功后交给输入会话做解析与震动。</summary>
    internal Pro2WiredProtocol? Protocol => _pad;

    /// <summary>
    /// 只枚举、不打开，用于「有没有插线」的判断。
    /// 打开会被 Steam 之类的程序干扰，所以探测阶段保持只读。
    /// </summary>
    public static Pro2LinkProbe Probe()
    {
        if (!LibUsb.EnsureLoaded(out string libDetail))
        {
            ConnectionDiag.Failure("usb_libusb_load", libDetail);
            return Pro2LinkProbe.None("缺少 USB 通信组件，暂时无法用 USB 连接。");
        }

        if (LibUsb.libusb_init(out IntPtr context) != LibUsb.Success)
        {
            ConnectionDiag.Failure("usb_libusb_init", "libusb_init_failed");
            return Pro2LinkProbe.None("USB 通信组件没能启动。");
        }

        IntPtr list = IntPtr.Zero;
        try
        {
            IntPtr total = LibUsb.libusb_get_device_list(context, out list);
            if (total <= 0 || list == IntPtr.Zero)
            {
                NoteUsbEnumeration(0, new List<string>(), new List<string>(), new List<string>());
                return Pro2LinkProbe.None("没有检测到任何 USB 设备。");
            }

            int found = 0;
            var devices = new List<string>();
            var matched = new List<string>();
            var firmwares = new List<string>();

            for (int i = 0; i < total; i++)
            {
                IntPtr device = System.Runtime.InteropServices.Marshal.ReadIntPtr(list, i * IntPtr.Size);
                if (LibUsb.libusb_get_device_descriptor(device, out LibUsb.DeviceDescriptor d) != LibUsb.Success)
                {
                    continue;
                }

                string entry = "vid=" + d.idVendor.ToString("X4") +
                               " pid=" + d.idProduct.ToString("X4") +
                               " bcd=" + d.bcdDevice.ToString("X4") +
                               " bus=" + LibUsb.libusb_get_bus_number(device) +
                               " addr=" + LibUsb.libusb_get_device_address(device) +
                               " speed=" + LibUsb.SpeedName(LibUsb.libusb_get_device_speed(device));
                devices.Add(entry);

                if (d.idVendor == VendorId && d.idProduct == ProductId)
                {
                    found++;
                    matched.Add(entry +
                        " usb=" + d.bcdUSB.ToString("X4") +
                        " class=" + d.bDeviceClass + "/" + d.bDeviceSubClass + "/" + d.bDeviceProtocol +
                        " mfg_idx=" + d.iManufacturer + " product_idx=" + d.iProduct +
                        " serial_idx=" + d.iSerialNumber + " configs=" + d.bNumConfigurations);
                    firmwares.Add("vid=057E pid=2069 bcdDevice=" + d.bcdDevice.ToString("X4") +
                        " version=" + BcdVersion(d.bcdDevice));
                }
            }

            NoteUsbEnumeration(devices.Count, devices, matched, firmwares);

            return found > 0
                ? new Pro2LinkProbe(true, found, "找到 " + found + " 个 USB 手柄")
                : Pro2LinkProbe.None("没有检测到 USB 手柄，请确认线插好了。");
        }
        catch (Exception ex)
        {
            ConnectionDiag.Failure("usb_enum", "usb_enum_exception", ex);
            return Pro2LinkProbe.None("USB 设备枚举失败。");
        }
        finally
        {
            // 顺序很重要：device list 必须在 context 销毁前释放，
            // 否则 free 到属于已销毁 context 的内存会直接 AccessViolation。
            if (list != IntPtr.Zero)
            {
                LibUsb.libusb_free_device_list(list, 1);
            }

            LibUsb.libusb_exit(context);
        }
    }

    /// <summary>USB 描述符里的 bcdDevice 是设备自报的版本号，作为固件版本线索记录。</summary>
    private static string BcdVersion(ushort bcd) =>
        (bcd >> 8).ToString("X2") + "." + (bcd & 0xFF).ToString("X2");

    private static void NoteUsbEnumeration(int count, List<string> devices,
        List<string> matched, List<string> firmwares)
    {
        string signature = count + "|" + string.Join(";", devices);
        lock (ProbeGate)
        {
            if (signature == lastUsbSignature) return;
            lastUsbSignature = signature;
        }

        ConnectionDiag.Step("usb_enum count=" + count +
            " seen=\"" + string.Join(" ", devices.Take(16)) + "\"");
        foreach (string item in matched) ConnectionDiag.Device("usb_pro2", item);
        foreach (string item in firmwares) ConnectionDiag.Firmware("usb_descriptor", item);
        if (matched.Count == 0)
            ConnectionDiag.Warn("usb_pro2_absent expected=057E:2069 hint=pid_mismatch_or_driver_not_claimed");
    }

    /// <summary>打开设备并跑完初始化命令。失败时 detail 给出可读原因。</summary>
    public bool Open(out string detail)
    {
        lock (_gate)
        {
            if (_pad is not null)
            {
                detail = "已经打开";
                return true;
            }

            var pad = new Pro2WiredProtocol();
            try
            {
                if (pad.TryOpen(out string why))
                {
                    pad.SetRumbleGain(_rumbleGain);   // 带入已设置的震动强度
                    _pad = pad;
                    detail = "ok";
                    ConnectionDiag.Ok("usb_open 设备已打开，初始化命令完成");
                    return true;
                }

                // 保留协议层原始原因（Humanize 会把 libusb 错误码翻成一句人话，排查时不够用）。
                ConnectionDiag.Failure("usb_open", why);
                detail = Humanize(why);
            }
            catch (Exception ex)
            {
                ConnectionDiag.Failure("usb_open", "protocol_exception", ex);
                detail = "USB 打开失败：" + ex.Message;
            }

            try { pad.Dispose(); } catch { }
            return false;
        }
    }

    /// <summary>补发初始化序列，用于手柄插线后只跑在 250Hz 的情况。</summary>
    public void ReapplyInit()
    {
        lock (_gate)
        {
            _pad?.ReapplyInit();
        }
    }

    /// <summary>把协议层的技术失败原因翻成一句能看懂的话。</summary>
    private static string Humanize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "USB 连接没有建立。";
        if (raw.Contains("libusb", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("LIBUSB", StringComparison.Ordinal))
        {
            return "USB 通信组件没能初始化，请重新插拔手柄后再试。";
        }
        if (raw.Contains("open", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("BUSY", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("ACCESS", StringComparison.OrdinalIgnoreCase))
        {
            return "USB 手柄被其它程序占用了（常见是 Steam），关掉占用它的程序后再试。";
        }
        if (raw.Contains("claim", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("interface", StringComparison.OrdinalIgnoreCase))
        {
            return "没能接管手柄的输入通道，可能有其它程序正在读取它。";
        }
        return "USB 连接没有建立，请重新插拔手柄后再试。";
    }

    /// <summary>启动读线程。Open 成功后调用一次即可。</summary>
    public void StartReading()
    {
        lock (_gate)
        {
            if (_pad is null || _readTask is not null)
            {
                return;
            }

            _readCts = new CancellationTokenSource();
            CancellationToken token = _readCts.Token;
            _readTask = Task.Run(() => ReadLoop(token), token);
        }
    }

    private async Task ReadLoop(CancellationToken token)
    {
        string reason = "已断开。";
        Pro2WiredProtocol? pad = _pad;
        if (pad is null) return;

        // 拔线后 libusb 有时不报错、只是一直超时；所以再加一层静默检测：
        // 连续 1.5 秒收不到有效帧就当断开。有线手柄正常时 500Hz 不会停顿。
        long lastFrameTicks = Stopwatch.GetTimestamp();

        try
        {
            while (!token.IsCancellationRequested)
            {
                byte[] buffer = new byte[ReportLength];
                int length;
                try
                {
                    length = pad.ReadReport(buffer, 200);
                }
                catch (Exception ex)
                {
                    reason = "读取手柄失败：" + ex.Message;
                    break;
                }

                if (length < 0)
                {
                    reason = "USB 线已断开。";
                    break;
                }

                if (length == 0)
                {
                    if (Stopwatch.GetElapsedTime(lastFrameTicks).TotalMilliseconds >= 1500)
                    {
                        reason = "USB 线已断开。";
                        break;
                    }

                    await Task.Yield();
                    continue;
                }

                lastFrameTicks = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref _reportCount);
                ReportReceived?.Invoke(buffer, lastFrameTicks);
            }
        }
        catch (Exception ex)
        {
            reason = "USB 读取异常：" + ex.Message;
        }
        finally
        {
            ConnectionDiag.Warn("usb_link_closed reason=\"" + reason +
                                "\" frames=" + Interlocked.Read(ref _reportCount));
            Closed?.Invoke(reason);
        }
    }

    /// <summary>写一包震动输出。有线走中断 OUT 端点，队列由协议对象自己节流。</summary>
    public bool WriteOutput(byte[] packet)
    {
        Pro2WiredProtocol? pad = _pad;
        if (pad is null) return false;
        try { return pad.QueueRumble(packet); }
        catch { return false; }
    }

    public void Close()
    {
        CancellationTokenSource? cts;
        Task? readTask;
        Pro2WiredProtocol? pad;
        lock (_gate)
        {
            cts = _readCts;
            readTask = _readTask;
            _readCts = null;
            _readTask = null;
            pad = _pad;
            _pad = null;
        }

        try { cts?.Cancel(); } catch { }
        try { readTask?.Wait(300); } catch { }
        cts?.Dispose();

        try { pad?.SendNeutralRumble(); } catch { }
        try { pad?.Dispose(); } catch { }

        Interlocked.Exchange(ref _reportCount, 0);
    }

    public void Dispose() => Close();
}
