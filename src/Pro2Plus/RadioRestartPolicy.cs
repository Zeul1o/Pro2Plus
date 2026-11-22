using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Pro2Plus;

public sealed record RadioRestartTarget(string Name, string PnpId, string InterfaceId,
    string Provider, string DriverVersion, bool IsDefault)
{
    public string DisplayName => Name + (IsDefault ? "（系统默认）" : "");
    public string DriverSummary => Provider + " · " + DriverVersion;
    public bool CanRestart => RadioRestartPolicy.IsExactRadioTarget(this);
}

/// <summary>
/// 蓝牙适配器重启的白名单式校验：只接受能逐字核对到精确设备实例的接口路径，
/// 子进程参数里永远只带实例 ID，不带类、总线、强制或重启参数。
/// </summary>
public static class RadioRestartPolicy
{
    private static readonly Guid RadioInterface = new("92383b0e-f90e-4ac9-8d44-8c2d0d0ebda2");
    private static readonly string[] ExcludedBusPrefixes = ["SWD", "ROOT", "HTREE", "HID"];

    public static IReadOnlyList<RadioRestartTarget> Targets(SystemBluetoothSnapshot snapshot) => snapshot.Adapters
        .Select(r => new RadioRestartTarget(r.Driver.Name, r.Driver.PnpDeviceId, r.Driver.WinRtDeviceId,
            r.Driver.DriverProvider, r.Driver.DriverVersion, r.IsDefault))
        .GroupBy(r => r.InterfaceId, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
        .OrderByDescending(r => r.IsDefault).ThenBy(r => r.Name).ToArray();

    public static bool IsExactRadioTarget(RadioRestartTarget target)
    {
        string id = target.InterfaceId;
        if (id.Length > 4096 || !id.StartsWith(@"\\?\", StringComparison.Ordinal) || target.PnpId.Length > 512 ||
            target.PnpId.Any(c => char.IsControl(c) || c is '"' or '*' or '?' or '/') ||
            target.PnpId.Split('\\').Length != 3) return false;
        string[] parts = id[4..].Split('#');
        if (parts.Length != 4 || parts.Take(3).Any(string.IsNullOrWhiteSpace) ||
            !Guid.TryParse(parts[3], out Guid guid) || guid != RadioInterface) return false;
        string bus = parts[0];
        if (bus.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) || BusIsExcluded(bus)) return false;
        return string.Equals(string.Join("\\", parts.Take(3)), target.PnpId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool BusIsExcluded(string bus)
    {
        // 旧实现每次都新建一个字符串数组再线性 Contains，这里直接逐项比较。
        foreach (string prefix in ExcludedBusPrefixes)
        {
            if (string.Equals(bus, prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static string[] RestartArguments(RadioRestartTarget target)
    {
        if (!IsExactRadioTarget(target)) throw new ArgumentException("无法核实所选蓝牙适配器，未重启任何设备。");
        return new[] { "/restart-device", target.PnpId }; // 仅精确实例；不使用 /deviceid、类、总线、强制或重启参数。
    }

    public static bool IsVerifiedSuccess(int exitCode, bool presentStarted, bool parametersUnchanged, bool driverUnchanged) =>
        exitCode == 0 && presentStarted && parametersUnchanged && driverUnchanged;

    public static string DecodeNativeOutput(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        if (bytes.Length > 65536) throw new ArgumentException("设备工具输出过长。");
        int sampledPairs = Math.Min(bytes.Length / 2, 128);
        int zeroHighBytes = 0;
        for (int i = 0; i < sampledPairs; i++)
        {
            if (bytes[i * 2 + 1] == 0) zeroHighBytes++;
        }
        bool utf16 = bytes.Length > 1 && bytes[0] == 0xFF && bytes[1] == 0xFE ||
            bytes.Length > 3 && zeroHighBytes > sampledPairs / 3;
        if (utf16) return Encoding.Unicode.GetString(bytes).TrimStart('\uFEFF');
        if (bytes.Length > 1 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes).TrimStart('\uFEFF');
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage).GetString(bytes);
        }
    }

    public static string CompletionMessage(int? exitCode, bool verified) => verified
        ? "蓝牙已重启。请唤醒 Pro2，再点击“连接 Pro2”；输入频率需重新测量。"
        : exitCode == 3010 ? "蓝牙热重启未完成。请保存工作后重启 Windows。"
        : "未确认蓝牙重启成功。请查看诊断记录；程序不会强制禁用设备或重启 Windows。";
}
