using System;
using System.IO;
using System.Text.Json;

namespace Pro2Plus;

public sealed record ConfigOperationResult(bool Known, bool RestartSucceeded, bool RestartRequired, string Code);

/// <summary>写入了数值、子进程也正常退出，仍不足以证明适配器重启真的完成。</summary>
public static class ConfigOperationEvidence
{
    public static ConfigOperationResult Parse(string json, string operation, string preset,
        DateTimeOffset requestedAt, DateTimeOffset now)
    {
        var unknown = new ConfigOperationResult(false, false, true, "completion_unknown");
        if (json.Length > 65536 || requestedAt > now) return unknown;
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("operation", out var action) || action.ValueKind != JsonValueKind.String || action.GetString() != operation ||
                !item.TryGetProperty("preset", out var choice) || choice.ValueKind != JsonValueKind.String || choice.GetString() != preset ||
                !item.TryGetProperty("completedAt", out var stamp) || stamp.ValueKind != JsonValueKind.String ||
                !stamp.TryGetDateTimeOffset(out var completed) || completed < requestedAt || completed > now ||
                !item.TryGetProperty("restartSucceeded", out var restarted) || restarted.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !item.TryGetProperty("restartRequired", out var needed) || needed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return unknown;
            bool success = restarted.GetBoolean(), required = needed.GetBoolean();
            if (success == required) return unknown;
            return new(true, success, required, success ? "adapter_restart_verified" : "system_restart_required");
        }
        catch (JsonException) { return unknown; }
    }

    public static ConfigOperationResult Read(string operation, string preset, DateTimeOffset requestedAt)
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Pro2Plus", "BLE-Turbo", "last-stack-operation.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) return new(false, false, true, "completion_absent");
            return Parse(File.ReadAllText(path), operation, preset, requestedAt, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(false, false, true, "completion_read_" + ex.GetType().Name); }
    }
}
