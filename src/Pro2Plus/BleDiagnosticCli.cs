using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

internal static class BleDiagnosticCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        string directory = Path.Combine(AppPaths.LogsDirectory, "ble_turbo");
        Directory.CreateDirectory(directory);
        string runLog = Path.Combine(
            directory,
            "ble-cli-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        string latestMarker = Path.Combine(directory, "ble-cli-latest.txt");
        var logGate = new object();
        void WriteLine(string line)
        {
            string stamped = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + line;
            lock (logGate)
            {
                File.AppendAllText(runLog, stamped + Environment.NewLine, new UTF8Encoding(false));
            }
        }

        File.WriteAllText(latestMarker, runLog, new UTF8Encoding(false));
        int durationSeconds = ParseDurationSeconds(args);
        string? preferredAddress = ParseOption(args, "--preferred-address");
        bool explicitPreferredAddress = !string.IsNullOrWhiteSpace(preferredAddress);
        bool customWin11 = Win11CustomConnectionParameters.IsCliRequested(args);
        bool outputIsolation = false;
        bool notifyLayer = false;
        bool rateWatch = false;
        int watchSeconds = 0;
        if (Array.Exists(args, a => new[] { "--ble-output-isolation", "--ble-notify-layer", "--ble-layer-self-test", "--ble-scheduler-only", "--direct-known-target", "--ble-rate-watch", "--watch-seconds" }
            .Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            WriteLine("[BLE_DIAGNOSTIC] refused=source_only_option_not_available_in_normal_build radio_opened=false");
            return 7;
        }
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(rateWatch ? watchSeconds + 240 : outputIsolation ? 600 : notifyLayer ? 240 : durationSeconds + (customWin11 ? 100 : 45)));
        IProgress<string> progress = customWin11 ? new DirectProgress<string>(WriteLine) : new Progress<string>(WriteLine);
        var source = new Pro2BleInputSource();
        try
        {
            WriteLine("[BLE_TURBO_CLI] begin duration_s=" + durationSeconds +
                      " preferred_address=" + (preferredAddress ?? "none"));
            if (customWin11)
            {
                WriteLine("[WIN11_7P5_ABI] diagnostic_only=true virtual_device_started=false system_writes=false");
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
                    !EnvironmentRouteInspector.Current.Plan.CanRequestPreferredParameters ||
                    !EnvironmentRouteInspector.Current.Plan.CanReadConnectionParameters)
                {
                    WriteLine("[WIN11_7P5_ABI] unsupported_runtime=true; no BLE connection or parameter request attempted.");
                    return 7;
                }
                preferredAddress = BleDiagnosticTargetResolver.Resolve(preferredAddress,
                    AppSettings.Load().LastConnectedPro2Addresses);
                WriteLine("[BLE_TURBO_TARGET] address=" + (string.IsNullOrEmpty(preferredAddress) ? "none" : preferredAddress) +
                    " source=" + (explicitPreferredAddress ? "argument" : string.IsNullOrEmpty(preferredAddress) ? "new_scan" : "saved_settings") +
                    " saved_address_is_not_connection_proof=true settings_written=false");
            }
            IReadOnlySet<string>? preferred = string.IsNullOrWhiteSpace(preferredAddress)
                ? null
                : new HashSet<string>(
                    [preferredAddress],
                    StringComparer.OrdinalIgnoreCase);
            await source.StartAsync(
                progress,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                preferred,
                onlyPreferredAddresses: customWin11 ? explicitPreferredAddress : preferred != null,
                timeout.Token);
            if (!source.IsRunning)
            {
                WriteLine("[BLE_TURBO_CLI] no_live_fd2 status=\"" + source.Status + "\"");
                WriteLine("[BLE_TURBO_CLI] scan_diagnostic=\"" + source.LastScanDiagnostic + "\"");
                if (customWin11)
                    foreach (string line in await BleEnvironmentDiagnostics.CaptureAsync(timeout.Token)) WriteLine(line);
                return 2;
            }


            Win10BleDiagnosticReport report =
                await source.RunWin10BleDiagnosticAsync(
                    TimeSpan.FromSeconds(durationSeconds),
                    progress,
                    timeout.Token);
            (string jsonPath, string textPath) =
                await Win10BleDiagnostic.SaveAsync(report, timeout.Token);
            if (customWin11)
                foreach (string line in await BleEnvironmentDiagnostics.CaptureAsync(timeout.Token)) WriteLine(line);
            WriteLine("[BLE_TURBO_CLI] complete json=\"" + jsonPath +
                      "\" text=\"" + textPath + "\"");
            // The isolated Win11 experiment uses a tighter full-window threshold
            // than the historical startup guard. Always export the actual value.
            double requiredHz = customWin11 ? 125 : SoftwareOnlyMode.IsEnabled ? SoftwareOnlyMode.MinimumAcceptedHz : 60;
            bool passed = report.IsInputAccepted(requiredHz);
            WriteLine("[BLE_TURBO_CLI] acceptance_required_hz=" + requiredHz +
                " target_hz=" + (SoftwareOnlyMode.IsEnabled ? 125 : 60) +
                " measurement_valid=" + report.MeasurementValid.ToString().ToLowerInvariant() +
                " passed=" + passed.ToString().ToLowerInvariant());
            return passed ? 0 : 3;
        }
        catch (OperationCanceledException)
        {
            WriteLine("[BLE_TURBO_CLI] timeout_or_cancelled");
            return 4;
        }
        catch (Exception ex)
        {
            WriteLine("[BLE_TURBO_CLI] failed " + ex);
            return 5;
        }
        finally
        {
            try
            {
                WriteLine("[BLE_TURBO_CLI] cleanup_begin");
                Task cleanup = source.DisposeAsync().AsTask();
                Task completed = await Task.WhenAny(
                    cleanup,
                    Task.Delay(TimeSpan.FromSeconds(3)));
                if (ReferenceEquals(completed, cleanup))
                {
                    await cleanup;
                    WriteLine("[BLE_TURBO_CLI] cleanup_complete");
                }
                else
                {
                    WriteLine("[BLE_TURBO_CLI] cleanup_timeout_3s process_will_exit");
                }
            }
            catch (Exception ex)
            {
                WriteLine("[BLE_TURBO_CLI] cleanup_warning " + ex.Message);
            }
        }
    }

    private static int ParseDurationSeconds(string[] args)
    {
        string? text = ParseOption(args, "--duration");
        return int.TryParse(text, out int value)
            ? Math.Clamp(value, 10, 60)
            : 15;
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
