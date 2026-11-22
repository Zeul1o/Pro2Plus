using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;
using Windows.Foundation.Metadata;

namespace Pro2Plus;

public sealed record EnvironmentSnapshot(EnvironmentFacts Facts, ConnectionPlan Plan, string ProbeWarning,
    bool ApiProbeSucceeded = true, bool ConfigurationProbeSucceeded = true, bool StackProbeSucceeded = true,
    StackParameterAssessment? Win10Assessment = null)
{
    public string Telemetry => "route=" + Plan.Route + " build=" + Facts.Build +
        " request_api=" + Facts.RequestParametersApiAvailable + " read_api=" + Facts.ReadParametersApiAvailable +
        " win10_configured=" + Facts.Win10StackValuesConfigured + " bthport_sha256=" + Facts.StackSha256 +
        " api_probe_ok=" + ApiProbeSucceeded + " configuration_probe_ok=" + ConfigurationProbeSucceeded +
        " stack_probe_ok=" + StackProbeSucceeded +
        (Win10Assessment == null ? "" : " win10_parameter_evidence=" + Win10Assessment.EvidenceLevel +
            " win10_parameter_names=" + Win10Assessment.ParameterNamesFound.Count + "/4" +
            " win10_experiment_available=" + Win10Assessment.CanExperiment +
            " win10_explicit_experiment_required=" + Win10Assessment.RequiresExperimentalConsent) +
        " warning=" + ProbeWarning.Replace('\r', ' ').Replace('\n', ' ');
}

/// <summary>
/// 只读的环境探测，每进程缓存一次；绝不在 Notify 热路径上重新探测。
/// 探测任一步失败都会降级为「未验证」并留下告警，不臆造 API 或系统栈状态。
/// </summary>
public static class EnvironmentRouteInspector
{
    private static readonly Lazy<EnvironmentSnapshot> cached = new(Inspect);
    private static EnvironmentSnapshot? refreshed;

    public static EnvironmentSnapshot Current => Volatile.Read(ref refreshed) ?? cached.Value;

    public static EnvironmentSnapshot Refresh()
    {
        EnvironmentSnapshot snapshot = Inspect();
        Volatile.Write(ref refreshed, snapshot);
        return snapshot;
    }

    private static EnvironmentSnapshot Inspect()
    {
        bool windows = OperatingSystem.IsWindows();
        int build = Environment.OSVersion.Version.Build;
        bool request = false, read = false, configured = false;
        bool apiProbeOk = true, configurationProbeOk = true, stackProbeOk = true;
        StackParameterAssessment? assessment = null;
        string hash = "unavailable", warning = "";
        if (windows && build >= 22000)
        {
            try
            {
                request = ApiInformation.IsMethodPresent("Windows.Devices.Bluetooth.BluetoothLEDevice", "RequestPreferredConnectionParameters") &&
                    ApiInformation.IsPropertyPresent("Windows.Devices.Bluetooth.BluetoothLEPreferredConnectionParameters", "ThroughputOptimized");
                read = ApiInformation.IsMethodPresent("Windows.Devices.Bluetooth.BluetoothLEDevice", "GetConnectionParameters") &&
                    ApiInformation.IsEventPresent("Windows.Devices.Bluetooth.BluetoothLEDevice", "ConnectionParametersChanged");
            }
            catch (Exception ex) { apiProbeOk = false; warning = "api_probe:" + ex.GetType().Name; }
        }
        if (windows)
        {
            try
            {
                if (build >= 19041 && build < 22000)
                {
                    assessment = StackParameterAssessment.ReadCurrent();
                    hash = assessment.StackSha256;
                    stackProbeOk = assessment.ValidSystemX64Image;
                    if (!stackProbeOk) warning += " stack_probe:" + assessment.Reason;
                }
                else
                {
                    using var stream = File.OpenRead(Path.Combine(Environment.SystemDirectory, "drivers", "bthport.sys"));
                    hash = Convert.ToHexString(SHA256.HashData(stream));
                }
            }
            catch (Exception ex) { stackProbeOk = false; warning += " stack_probe:" + ex.GetType().Name; }
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BleStackProfile.RegistrySubKey);
                configured = key != null && BleStackProfile.Values.All(pair =>
                    key.GetValue(pair.Key) is int actual && actual == pair.Value);
            }
            catch (Exception ex) { configurationProbeOk = false; warning += " configuration_probe:" + ex.GetType().Name; }
        }

        var facts = new EnvironmentFacts(windows, build, request, read, hash, configured);
        ConnectionPlan plan = ConnectionRoutePolicy.Select(facts);
        if (assessment is { CanExperiment: false })
            plan = plan with
            {
                Route = ConnectionRoute.NativeNotifyOnly,
                Title = "Win10 · 系统文件检查未通过",
                Guidance = assessment.EvidenceSummary,
                CanConfigureWin10Stack = false
            };
        return new(facts, plan, warning, apiProbeOk, configurationProbeOk, stackProbeOk, assessment);
    }
}
