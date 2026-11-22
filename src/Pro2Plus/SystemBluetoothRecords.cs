using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pro2Plus;

public sealed record SystemDiagnosticInfo(string Family, string Edition, string Release,
    string Version, string Architecture, string ProcessArchitecture, string AppVersion, string StackVersion);

public sealed record RadioDiagnosticInfo(BluetoothAdapterDiagnosticInfo Driver, bool IsDefault,
    string Address, bool LowEnergy, bool Central, bool Peripheral, bool Classic);

public sealed record SystemBluetoothSnapshot(DateTimeOffset CapturedAt, SystemDiagnosticInfo System,
    IReadOnlyList<RadioDiagnosticInfo> Adapters, string CompatibilityTelemetry, string Warning,
    Win10ConfigurationAudit? Win10Configuration = null, StackParameterAssessment? Win10Assessment = null);

public sealed record Win10ConfigurationAudit(bool Readable, bool Configured, string Values,
    bool OriginalBackupExists, bool RestartPending, string ProcessElevated)
{
    /// <summary>上一次「应用参数」的标记只能当线索：中间只要重启过，就说明参数状态未被验证。</summary>
    public static bool RequiresRestart(string markerJson, DateTimeOffset boot, DateTimeOffset now)
    {
        if (markerJson.Length > 65536 || boot > now) return false;
        try
        {
            using JsonDocument json = JsonDocument.Parse(markerJson);
            JsonElement item = json.RootElement;
            return item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("restartRequired", out JsonElement needed) && needed.ValueKind == JsonValueKind.True &&
                item.TryGetProperty("operation", out JsonElement operation) && operation.ValueKind == JsonValueKind.String &&
                operation.GetString() == "apply" && item.TryGetProperty("completedAt", out JsonElement time) &&
                time.ValueKind == JsonValueKind.String && time.TryGetDateTimeOffset(out DateTimeOffset completed) &&
                completed > boot && completed <= now;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
