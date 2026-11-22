using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Pro2Plus;

/// <summary>一次 Win10 BLE 参数配置操作的记录（只描述历史，不代表当前链路状态）。</summary>
public sealed record StackOperationRecord(bool Valid, string Code,
    string? Operation = null, string? Preset = null, DateTimeOffset? CompletedAt = null,
    bool? RestartSucceeded = null, bool? RestartRequired = null);

/// <summary>
/// 只读的历史配置证据：不写设置、不跑预设、不重启设备，
/// 也不会把旧的配置结果当成当前 BLE 实测数据。
/// </summary>
public static class BleStackOperationLog
{
    public const int MaximumFileBytes = 65536;

    private static readonly string[] RequiredFields =
        { "operation", "preset", "completedAt", "restartSucceeded", "restartRequired" };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Pro2Plus", "BLE-Turbo", "last-stack-operation.json");

    public static StackOperationRecord Parse(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return new StackOperationRecord(false, "history_absent");
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
            return new StackOperationRecord(false, "history_oversize");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object) return new StackOperationRecord(false, "history_invalid");

            // 重复字段会被多数解析器「后者覆盖前者」，这里直接判为不可用。
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (IsRequiredField(property.Name) && !seen.Add(property.Name))
                    return new StackOperationRecord(false, "history_duplicate_field");
            }

            if (RequiredFields.Any(name => !seen.Contains(name)) ||
                !TryString(item, "operation", out string? operation) ||
                !TryString(item, "preset", out string? preset) ||
                !TryString(item, "completedAt", out _) ||
                !item.GetProperty("completedAt").TryGetDateTimeOffset(out DateTimeOffset completedAt) ||
                completedAt > now ||
                !TryBoolean(item, "restartSucceeded", out bool succeeded) ||
                !TryBoolean(item, "restartRequired", out bool required))
            {
                return new StackOperationRecord(false, "history_invalid");
            }

            // 两个标志同真或同假都自相矛盾：要么重启过、要么还需要重启。
            if (succeeded == required) return new StackOperationRecord(false, "history_inconsistent_restart");
            if (!IsKnownPreset(operation!, preset!))
                return new StackOperationRecord(false, "history_invalid_operation_or_preset");

            return new StackOperationRecord(true, "history_valid", operation, preset, completedAt, succeeded, required);
        }
        catch (JsonException)
        {
            return new StackOperationRecord(false, "history_invalid_json");
        }
    }

    public static StackOperationRecord Read(string? path = null, DateTimeOffset? now = null)
    {
        try
        {
            // 限制读取本身，而不是只信先前的 FileInfo 长度：信息页打开期间
            // 别的进程可以替换或撑大这个标记文件。
            using var stream = new FileStream(path ?? DefaultPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumFileBytes) return new StackOperationRecord(false, "history_oversize");

            var bytes = new byte[MaximumFileBytes + 1];
            int count = 0;
            while (count < bytes.Length)
            {
                int read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }

            if (count > MaximumFileBytes) return new StackOperationRecord(false, "history_oversize");

            string json = new UTF8Encoding(false, true).GetString(bytes, 0, count);
            if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
            return Parse(json, now ?? DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new StackOperationRecord(false, "history_absent");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException)
        {
            return new StackOperationRecord(false, "history_read_" + ex.GetType().Name);
        }
    }

    public static string ReadAndFormat(bool isWindows10, string? path = null, DateTimeOffset? now = null) =>
        Format(isWindows10 ? Read(path, now) : null, isWindows10);

    public static string Format(StackOperationRecord? history, bool isWindows10 = true)
    {
        var text = new StringBuilder();
        text.AppendLine("Win10 系统 BLE 参数预设与配置记录");
        if (!isWindows10)
        {
            text.AppendLine("当前为 Win11：不套用 Win10 系统配置；请查看本连接的 API 预设结果。");
            return text.ToString().TrimEnd();
        }

        text.AppendLine("可用方法（首页授权后测试；此页只读，不执行修改）：");
        foreach (BlePresetDefinition preset in BlePresetCatalog.Presets)
        {
            text.AppendLine(preset.Id + " · " + preset.Title + "：Min=" + preset.IntervalMinUnits +
                " / Max=" + preset.IntervalMaxUnits + " / Latency=" + preset.Latency +
                " / LSTO=" + preset.TimeoutUnits + "（" + Number(preset.MinimumIntervalMs) +
                "–" + Number(preset.MaximumIntervalMs) + "ms；监督超时 " +
                Number(preset.TimeoutUnits * 10.0) + "ms）");
        }

        text.AppendLine("参数目录不是执行结果；以当前真实 FD2 / Parsed 测量判定速率。");
        if (!IsComplete(history))
        {
            text.AppendLine("历史配置操作：" + ExplainUnknown(history?.Code ?? "history_absent"));
            text.AppendLine("不能据此认定所有预设均未测试、请求被拒绝或蓝牙硬件不支持。");
            return text.ToString().TrimEnd();
        }

        // 即使调用方直接构造了记录，也只格式化通过校验的内容。
        if (history is not { CompletedAt: { } completedAt, Operation: { } operation, Preset: { } presetId,
                RestartSucceeded: { } restartSucceeded, RestartRequired: { } restartRequired })
        {
            return Format(new StackOperationRecord(false, "history_invalid"));
        }

        if (restartSucceeded == restartRequired || !IsKnownPreset(operation, presetId))
        {
            return Format(new StackOperationRecord(false, "history_invalid"));
        }

        text.AppendLine("历史配置操作（非本次连接验收）：");
        text.AppendLine("完成时间：" + completedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        string presetTitle = operation == "restore"
            ? "原始备份"
            : BlePresetCatalog.Presets.Single(p => p.Id == presetId).Title;
        text.AppendLine("操作：" + (operation == "restore" ? "恢复" : "应用") +
            "；预设：" + presetId + " / " + presetTitle);
        text.AppendLine("该次记录：restartSucceeded=" + (restartSucceeded ? "True" : "False") +
            "；restartRequired=" + (restartRequired ? "True" : "False"));
        text.AppendLine(restartSucceeded
            ? "该次适配器重启已完成；不证明本次连接采用了参数或达到 125Hz。"
            : "该次操作提示需重启 Windows；之后可能已重启，不能据此判定当前仍需重启。");
        text.AppendLine("此文件仅保留最近一次配置操作，不是三组预设的完整测量历史。");
        return text.ToString().TrimEnd();
    }

    private static bool IsRequiredField(string name) =>
        RequiredFields.Contains(name, StringComparer.Ordinal);

    private static bool IsKnownPreset(string operation, string preset) =>
        (operation == "apply" && BlePresetCatalog.Presets.Any(p => p.Id == preset)) ||
        (operation == "restore" && preset == "original");

    private static bool IsComplete(StackOperationRecord? history) =>
        history is { Valid: true, CompletedAt: not null, Operation: not null, Preset: not null,
            RestartSucceeded: not null, RestartRequired: not null };

    private static bool TryString(JsonElement item, string name, out string? value)
    {
        value = null;
        JsonElement field = item.GetProperty(name);
        if (field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryBoolean(JsonElement item, string name, out bool value)
    {
        value = false;
        JsonElement field = item.GetProperty(name);
        if (field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = field.GetBoolean();
        return true;
    }

    private static string Number(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string ExplainUnknown(string code) => code switch
    {
        "history_absent" => "未找到；执行结果未记录。",
        "history_oversize" => "文件超过 64KiB，未读取；结果未知。",
        _ => "记录不可用（" + code.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ') + "）；结果未知。"
    };
}
