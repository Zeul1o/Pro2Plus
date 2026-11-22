using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Pro2Plus;

/// <summary>蓝牙适配器的只读身份信息（WinRT 接口名 + 驱动元数据）。</summary>
public sealed record BluetoothAdapterDiagnosticInfo(
    string Name,
    string PnpDeviceId,
    string Vid,
    string Pid,
    string DriverProvider,
    string DriverVersion,
    string DriverDate,
    string WinRtDeviceId);

/// <summary>
/// 适配器身份采集：先问 WinRT，缺字段时回落到注册表。只读，不打开无线电，
/// 也不因为拿不到名字就借用别的适配器信息。
/// </summary>
internal static class BleAdapterProbe
{
    // 只请求真正会读的四个属性：HardwareIds / Manufacturer 以前请求了却从未使用。
    private static readonly string[] RequestedProperties =
    [
        "System.Devices.DeviceInstanceId",
        "System.Devices.DriverProvider",
        "System.Devices.DriverVersion",
        "System.Devices.DriverDate"
    ];

    internal static async Task<BluetoothAdapterDiagnosticInfo> CaptureDefaultAsync()
    {
        BluetoothAdapter? adapter = await BluetoothAdapter.GetDefaultAsync();
        // 没有默认无线电：把原因写进 Name 字段，避免报告里出现空白的适配器块。
        return adapter == null
            ? Unknown("BluetoothAdapter.GetDefaultAsync returned null")
            : await CaptureAsync(adapter);
    }

    internal static async Task<BluetoothAdapterDiagnosticInfo> CaptureAsync(BluetoothAdapter adapter)
    {
        string name = "unknown";
        string pnpId = "";
        string provider = "";
        string version = "";
        string date = "";
        try
        {
            DeviceInformation info = await DeviceInformation.CreateFromIdAsync(adapter.DeviceId, RequestedProperties);
            name = DiagnosticText.Unknown(info.Name);
            pnpId = PropertyString(info, "System.Devices.DeviceInstanceId");
            provider = PropertyString(info, "System.Devices.DriverProvider");
            version = PropertyString(info, "System.Devices.DriverVersion");
            date = PropertyString(info, "System.Devices.DriverDate");
        }
        catch
        {
            // WinRT 侧失败不影响结论：下面的注册表回落仍能给出实例 ID 与驱动信息。
        }

        if (string.IsNullOrWhiteSpace(pnpId)) pnpId = InstanceIdFromInterfaceId(adapter.DeviceId);

        RegistryAdapterDetails registry = ReadRegistry(pnpId);
        if (string.IsNullOrWhiteSpace(pnpId)) pnpId = registry.PnpDeviceId;
        // WinRT 接口名有时是电脑名而不是无线电型号，注册表的 FriendlyName 更可信。
        if (!string.IsNullOrWhiteSpace(registry.Name)) name = DiagnosticText.Unknown(registry.Name);
        if (string.IsNullOrWhiteSpace(provider)) provider = registry.Provider;
        if (string.IsNullOrWhiteSpace(version)) version = registry.DriverVersion;
        if (string.IsNullOrWhiteSpace(date)) date = registry.DriverDate;

        (string vid, string pid) = SplitVidPid(pnpId);
        return new BluetoothAdapterDiagnosticInfo(
            DiagnosticText.Unknown(name),
            DiagnosticText.Unknown(pnpId),
            DiagnosticText.Unknown(vid),
            DiagnosticText.Unknown(pid),
            DiagnosticText.Unknown(provider),
            DiagnosticText.Unknown(version),
            DiagnosticText.Unknown(date),
            DiagnosticText.Unknown(adapter.DeviceId));
    }

    /// <summary><c>\\?\USB#VID_8087&amp;PID_0026#7&amp;1f2e#0#{guid}</c> → <c>USB\VID_8087&amp;PID_0026\7&amp;1f2e\0</c></summary>
    internal static string InstanceIdFromInterfaceId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        string normalized = id.Trim();
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal)) normalized = normalized[4..];
        int classGuid = normalized.IndexOf("#{", StringComparison.Ordinal);
        if (classGuid >= 0) normalized = normalized[..classGuid];
        return normalized.Replace('#', '\\');
    }

    /// <summary>从 PNP 实例 ID 里取 VID_/PID_ 后四位；缺失或截断时返回空串。</summary>
    internal static (string Vid, string Pid) SplitVidPid(string pnpId)
    {
        string upper = pnpId.ToUpperInvariant();
        return (FourAfter(upper, "VID_"), FourAfter(upper, "PID_"));
    }

    private static string FourAfter(string text, string marker)
    {
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        return at >= 0 && text.Length >= at + 8 ? text.Substring(at + 4, 4) : "";
    }

    /// <summary>DeviceDesc 可能是 <c>@inf,,-1234;显示名</c> 形式的资源引用，取分号后的部分。</summary>
    internal static string UnwrapInfName(string name)
    {
        // 分号之前是 INF 资源串，不是给人看的名字。
        return name.StartsWith('@') && name.Contains(';') ? name[(name.LastIndexOf(';') + 1)..] : name;
    }

    private static RegistryAdapterDetails ReadRegistry(string pnpId)
    {
        if (!string.IsNullOrWhiteSpace(pnpId))
        {
            // 精确匹配该实例：非 USB 无线电（如某些 M.2 组合卡）也在 Enum 下。
            using RegistryKey? exact = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnpId);
            return ReadRegistryDetails(exact, pnpId);
        }

        // 没有实例 ID 时只能尽力而为：扫 USB 枚举，取第一个蓝牙类设备。
        using RegistryKey? enumUsb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (enumUsb == null) return new RegistryAdapterDetails();

        RegistryAdapterDetails? first = null;
        foreach (string hardware in enumUsb.GetSubKeyNames())
        {
            using RegistryKey? hardwareKey = enumUsb.OpenSubKey(hardware);
            if (hardwareKey == null) continue;
            foreach (string instance in hardwareKey.GetSubKeyNames())
            {
                using RegistryKey? deviceKey = hardwareKey.OpenSubKey(instance);
                if (!IsBluetoothClass(deviceKey)) continue;
                first ??= ReadRegistryDetails(deviceKey, "USB\\" + hardware + "\\" + instance);
            }
        }

        return first ?? new RegistryAdapterDetails();
    }

    private static bool IsBluetoothClass(RegistryKey? key) => string.Equals(
        key?.GetValue("ClassGUID")?.ToString(),
        "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}",
        StringComparison.OrdinalIgnoreCase);

    private static RegistryAdapterDetails ReadRegistryDetails(RegistryKey? deviceKey, string pnpId)
    {
        if (deviceKey == null) return new RegistryAdapterDetails(PnpDeviceId: pnpId);
        string name = deviceKey.GetValue("FriendlyName")?.ToString() ??
                      deviceKey.GetValue("DeviceDesc")?.ToString() ?? "";
        name = UnwrapInfName(name);
        string driver = deviceKey.GetValue("Driver")?.ToString() ?? "";
        string provider = deviceKey.GetValue("Mfg")?.ToString() ?? "";
        string version = "";
        string date = "";
        if (!string.IsNullOrWhiteSpace(driver))
        {
            using RegistryKey? driverKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\" + driver);
            provider = driverKey?.GetValue("ProviderName")?.ToString() ?? provider;
            version = driverKey?.GetValue("DriverVersion")?.ToString() ?? "";
            date = driverKey?.GetValue("DriverDate")?.ToString() ?? "";
        }

        return new RegistryAdapterDetails(name, pnpId, provider, version, date);
    }

    private static string PropertyString(DeviceInformation info, string name)
    {
        if (!info.Properties.TryGetValue(name, out object? value) || value == null) return "";
        return value switch
        {
            string[] array => string.Join(",", array),
            IEnumerable<string> strings => string.Join(",", strings),
            _ => value.ToString() ?? ""
        };
    }

    private static BluetoothAdapterDiagnosticInfo Unknown(string reason) =>
        new(reason, "unknown", "unknown", "unknown", "unknown", "unknown", "unknown", "unknown");

    private sealed record RegistryAdapterDetails(
        string Name = "",
        string PnpDeviceId = "",
        string Provider = "",
        string DriverVersion = "",
        string DriverDate = "");
}
