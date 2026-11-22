using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;

namespace Pro2Plus;

/// <summary>
/// BLE 诊断的门面：把采样窗、间隔统计、判定与报告串起来，并把报告落盘。
/// 采集/统计/判定的实现都在各自文件里；这里只做组装，方便逐段单测。
/// </summary>
internal static class Win10BleDiagnostic
{
    public static Task<BluetoothAdapterDiagnosticInfo> CaptureAdapterAsync() => BleAdapterProbe.CaptureDefaultAsync();

    public static Task<BluetoothAdapterDiagnosticInfo> CaptureAdapterAsync(BluetoothAdapter adapter) =>
        BleAdapterProbe.CaptureAsync(adapter);

    internal static BleGapStatistics BuildGapStatistics(long[] ticks) => BleGapStatisticsBuilder.Build(ticks);

    public static Win10BleDiagnosticReport BuildReport(
        Win10BleDiagnosticSamples samples,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        BluetoothAdapterDiagnosticInfo adapter,
        string connectedAddress,
        ushort descriptorHandle,
        string descriptorUuid,
        ushort requestedHz,
        string descriptorBefore,
        string descriptorWriteStatus,
        string descriptorAfter)
    {
        double seconds = samples.DurationSeconds;
        // 速率按整个采集窗计算：断连或静默的尾巴必须把平均速率拉下来，
        // 而不是在「首末包之间」的算法里消失。
        double rawHz = samples.RawCount / seconds;
        double fd2Hz = samples.Fd2Count / seconds;
        double parsedHz = samples.ParsedCount / seconds;
        string failure = BleMeasurementVerdict.Failure(samples);
        EnvironmentSnapshot compatibility = EnvironmentRouteInspector.Refresh();
        return new Win10BleDiagnosticReport(
            startedAtUtc,
            completedAtUtc,
            seconds,
            ReadActiveExperimentProfile(),
            compatibility.Facts.Build >= 22000
                ? "Windows 11 " + ReadWindowsValue("EditionID")
                : ReadWindowsValue("ProductName"),
            ReadWindowsValue("DisplayVersion"),
            ReadWindowsBuild(),
            Environment.OSVersion.VersionString,
            adapter,
            DiagnosticText.Unknown(connectedAddress),
            descriptorHandle == 0 ? "none" : "0x" + descriptorHandle.ToString("X4"),
            DiagnosticText.Unknown(descriptorUuid),
            requestedHz,
            DiagnosticText.Unknown(descriptorBefore),
            DiagnosticText.Unknown(descriptorWriteStatus),
            DiagnosticText.Unknown(descriptorAfter),
            samples.RawCount,
            samples.Fd2Count,
            samples.ParsedCount,
            rawHz,
            fd2Hz,
            parsedHz,
            BuildGapStatistics(samples.Fd2Ticks),
            failure.Length == 0 ? BleMeasurementVerdict.ClassifyRates(fd2Hz, parsedHz) : failure)
        {
            MeasurementCompleted = samples.MeasurementCompleted,
            ConnectionContinuous = samples.Interruption.Length == 0,
            ConnectedAtEnd = samples.ConnectedAtEnd,
            MeasurementFailure = failure,
            LastParsedInputAgeMs = samples.LastParsedInputAgeMs,
            MaximumFd2SilenceMs = samples.MaximumFd2SilenceMs,
            MaximumParsedSilenceMs = samples.MaximumParsedSilenceMs,
            GapSamplesTruncated = samples.GapSamplesTruncated,
            CompatibilityRoute = compatibility.Plan.Route.ToString(),
            CompatibilityGuidance = compatibility.Plan.Guidance,
            SystemStackSha256 = compatibility.Facts.StackSha256,
            Win10StackValuesConfigured = compatibility.Facts.Win10StackValuesConfigured,
            PublicRequestApiAvailable = compatibility.Facts.RequestParametersApiAvailable,
            PublicReadApiAvailable = compatibility.Facts.ReadParametersApiAvailable
        };
    }

    public static async Task<(string JsonPath, string TextPath)> SaveAsync(
        Win10BleDiagnosticReport report,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(AppPaths.LogsDirectory, "ble_turbo");
        Directory.CreateDirectory(directory);
        string stem = "ble-diagnostic-" +
                      SanitizeFileName(report.ExperimentProfile) + "-" +
                      DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string jsonPath = Path.Combine(directory, stem + ".json");
        string textPath = Path.Combine(directory, stem + ".log");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, options),
            new UTF8Encoding(false),
            cancellationToken);
        await File.WriteAllLinesAsync(
            textPath,
            report.ToLogLines(),
            new UTF8Encoding(false),
            cancellationToken);
        return (jsonPath, textPath);
    }

    private static string ReadWindowsValue(string name)
    {
        return DiagnosticText.Unknown(Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            name,
            "")?.ToString());
    }

    private static string ReadWindowsBuild()
    {
        string build = ReadWindowsValue("CurrentBuildNumber");
        string ubr = ReadWindowsValue("UBR");
        return ubr == "unknown" ? build : build + "." + ubr;
    }

    private static string ReadActiveExperimentProfile()
    {
        // 刚验证过的全局路线不能被误标成旧的「适配器级 A 实验」。
        EnvironmentSnapshot compatibility = EnvironmentRouteInspector.Current;
        if (compatibility.Facts.Build >= 22000) return "WIN11_PUBLIC_OR_NATIVE";
        if (compatibility.Facts.Win10StackValuesConfigured)
            return compatibility.Plan.CanConfigureWin10Stack ? "WIN10_STACK_7P5MS" : "WIN10_EXISTING_STACK_UNAUDITED";
        try
        {
            string marker = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Pro2Plus",
                "BLE-Turbo",
                "active-profile.json");
            if (!File.Exists(marker)) return "BASELINE";
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(marker));
            if (document.RootElement.TryGetProperty("profile", out JsonElement value))
            {
                return DiagnosticText.Unknown(value.GetString()).ToUpperInvariant();
            }
        }
        catch
        {
            // 标记文件只是线索，缺失或损坏都按基线处理。
        }

        return "BASELINE";
    }

    private static string SanitizeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.ToString();
    }
}
