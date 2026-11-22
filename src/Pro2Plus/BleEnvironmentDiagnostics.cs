using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Pro2Plus;

/// <summary>On-demand export only. No writes, pairing, adapter restart, or BLE stream subscriptions.</summary>
internal static class BleEnvironmentDiagnostics
{
    private static readonly string[] ValueNames =
    [
        "LEConnectionIntervalMin", "LEConnectionIntervalMax", "LEConnectionLatency", "LEConnectionLSTO",
        "ConnectionIntervalMin", "ConnectionIntervalMax", "ConnectionLatency", "LinkSupervisionTimeout"
    ];

    public static async Task<IReadOnlyList<string>> CaptureAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        try
        {
            BluetoothAdapterDiagnosticInfo adapter = await Win10BleDiagnostic.CaptureAdapterAsync()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            lines.Add(DriverLine(adapter));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { lines.Add("[BLE_DRIVER_AUDIT] unavailable=" + ex.GetType().Name); }
        lines.AddRange(await Task.Run(ReadStack, cancellationToken));
        return lines;
    }

    internal static string DriverLine(BluetoothAdapterDiagnosticInfo adapter) =>
        "[BLE_DRIVER_AUDIT] name=\"" + adapter.Name + "\" pnp_id=\"" + adapter.PnpDeviceId +
        "\" vid=" + adapter.Vid + " pid=" + adapter.Pid + " provider=\"" + adapter.DriverProvider +
        "\" driver_version=" + adapter.DriverVersion + " driver_date=" + adapter.DriverDate;

    internal static string StackLine(string? bthportVersion, string runtimeOs, string stackSha256) =>
        "[BLE_STACK_AUDIT] bthport_version=" + bthportVersion + " runtime_os=" + runtimeOs + " sha256=" + stackSha256;

    internal static string OsLine(object? displayVersion, object? build, object? ubr) =>
        "[BLE_STACK_AUDIT] display_version=" + displayVersion + " build=" + build + " ubr=" + ubr;

    internal static string ValueLine(string name, object? value)
    {
        string text = value == null ? "absent" : value is int number ? number.ToString() : "non_dword";
        return "[BLE_STACK_VALUE] name=" + name + " value=" + text +
            " read_only=true configured_value_is_not_effectiveness_proof";
    }

    // 三段各自兜底：读 bthport 版本失败，不应该顺带丢掉注册表里的配置值。
    private static IReadOnlyList<string> ReadStack()
    {
        var lines = new List<string>();
        try
        {
            string path = Path.Combine(Environment.SystemDirectory, "drivers", "bthport.sys");
            lines.Add(StackLine(FileVersionInfo.GetVersionInfo(path).FileVersion,
                Environment.OSVersion.Version.ToString(), EnvironmentRouteInspector.Current.Facts.StackSha256));
        }
        catch (Exception ex) { lines.Add("[BLE_STACK_AUDIT] unavailable=" + ex.GetType().Name); }

        try
        {
            using RegistryKey? osKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            lines.Add(OsLine(osKey?.GetValue("DisplayVersion"), osKey?.GetValue("CurrentBuildNumber"),
                osKey?.GetValue("UBR")));
        }
        catch (Exception ex) { lines.Add("[BLE_STACK_AUDIT] os_unavailable=" + ex.GetType().Name); }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BleStackProfile.RegistrySubKey, writable: false);
            foreach (string name in ValueNames)
                lines.Add(ValueLine(name,
                    key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
        }
        catch (Exception ex) { lines.Add("[BLE_STACK_VALUE] unavailable=" + ex.GetType().Name); }

        return lines;
    }
}
