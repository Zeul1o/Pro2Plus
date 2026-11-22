using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pro2Plus;

/// <summary>已挂载虚拟设备的身份信息，供清理、诊断与槽位匹配使用。</summary>
public sealed record BridgeDeviceInfo(uint BusId, string DevId, string Vid, string Pid, string Type);

/// <summary>
/// 输出桥的纯线协议：请求行拼装、响应解码、JSON 报文解析与错误判定。
/// 不含 socket、超时或取消逻辑，所有分支都能离线逐例比对。
/// </summary>
internal static class OutputBridgeWire
{
    private static readonly char[] ReplyPadding = ['\0', '\r', '\n', ' '];

    internal static string BuildRequestLine(string path, string? payload) =>
        string.IsNullOrWhiteSpace(payload)
            ? string.Concat(path, "\0")
            : string.Concat(path, " ", payload, "\0");

    internal static string BuildStreamPath(uint busId, string devId) =>
        string.Concat("bus/", busId.ToString(), "/", devId, "\0");

    internal static string DecodeReply(ReadOnlySpan<byte> bytes)
    {
        string decoded = System.Text.Encoding.UTF8.GetString(bytes);
        ReadOnlySpan<char> trimmed = decoded.AsSpan().TrimEnd(ReplyPadding);
        return trimmed.Length == decoded.Length ? decoded : trimmed.ToString();
    }

    internal static void ThrowIfApiError(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(response);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("status", out JsonElement status) &&
                status.ValueKind == JsonValueKind.Number)
            {
                string title = Text(root, "title", "VIIPER API error");
                string detail = Text(root, "detail", "");
                throw new InvalidOperationException(status.GetInt32() + " " + title + ": " + detail);
            }
        }
        catch (JsonException)
        {
            // 非 JSON 的普通回包不是错误对象。
        }
    }

    internal static IReadOnlyList<uint> ParseBusIds(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var list = new List<uint>();
        if (doc.RootElement.TryGetProperty("buses", out JsonElement buses))
        {
            foreach (JsonElement bus in buses.EnumerateArray())
            {
                list.Add(bus.GetUInt32());
            }
        }

        return list;
    }

    internal static IReadOnlyList<BridgeDeviceInfo> ParseDevices(string json, uint fallbackBusId)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var list = new List<BridgeDeviceInfo>();
        if (!doc.RootElement.TryGetProperty("devices", out JsonElement devices))
        {
            return list;
        }

        foreach (JsonElement item in devices.EnumerateArray())
        {
            uint busId = item.TryGetProperty("busId", out JsonElement id)
                ? id.GetUInt32()
                : fallbackBusId;
            list.Add(new BridgeDeviceInfo(
                busId,
                Text(item, "devId", ""),
                Text(item, "vid", ""),
                Text(item, "pid", ""),
                Text(item, "type", "")));
        }

        return list;
    }

    internal static uint ParseCreatedBusId(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("busId").GetUInt32();
    }

    internal static BridgeDeviceInfo ParseAddedDevice(string json, string requestedType)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        return new BridgeDeviceInfo(
            root.GetProperty("busId").GetUInt32(),
            root.GetProperty("devId").GetString() ?? "",
            Text(root, "vid", ""),
            Text(root, "pid", ""),
            Text(root, "type", requestedType));
    }

    private static string Text(JsonElement owner, string name, string fallback) =>
        owner.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? fallback : fallback;
}
