using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Pro2Plus;

public enum BlePreset { Fixed7p5, Flexible7p5To15, Fixed15 }

public sealed record BlePresetDefinition(BlePreset Preset, string Id, string Title,
    int IntervalMinUnits, int IntervalMaxUnits, int Latency, int TimeoutUnits)
{
    public IReadOnlyDictionary<string, int> Values => new ReadOnlyDictionary<string, int>(
        new Dictionary<string, int>(4, StringComparer.OrdinalIgnoreCase)
        {
            ["LEConnectionIntervalMin"] = IntervalMinUnits,
            ["LEConnectionIntervalMax"] = IntervalMaxUnits,
            ["LEConnectionLatency"] = Latency,
            ["LEConnectionLSTO"] = TimeoutUnits
        });
    public double MinimumIntervalMs => IntervalMinUnits * 1.25;
    public double MaximumIntervalMs => IntervalMaxUnits * 1.25;
}

/// <summary>
/// 已审查系统栈上可选的三档参数。这里是目录与命令行解析，不做 IO、不提权，
/// 也不承诺任何速率；查找改为线性扫描，避免每次构造 LINQ 谓词闭包。
/// </summary>
public static class BlePresetCatalog
{
    public const string SelectorPrefix = "--ble-turbo-stack-preset=";
    private const string SelectorName = "--ble-turbo-stack-preset";

    public static IReadOnlyList<BlePresetDefinition> Presets { get; } =
        Array.AsReadOnly(new[]
        {
            new BlePresetDefinition(BlePreset.Fixed7p5, "7p5", "固定 7.5ms", 6, 6, 0, 200),
            new BlePresetDefinition(BlePreset.Flexible7p5To15, "flexible", "7.5–15ms", 6, 12, 0, 200),
            new BlePresetDefinition(BlePreset.Fixed15, "15", "固定 15ms", 12, 12, 0, 200)
        });

    public static BlePresetDefinition Default => Presets[0];

    public static BlePresetDefinition Get(BlePreset preset)
    {
        foreach (BlePresetDefinition item in Presets)
        {
            if (item.Preset == preset) return item;
        }
        throw new ArgumentOutOfRangeException(nameof(preset));
    }

    public static BlePresetDefinition Get(string id)
    {
        foreach (BlePresetDefinition item in Presets)
        {
            if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) return item;
        }
        throw new ArgumentException("未知 Win10 BLE 预设；允许 7p5、flexible、15，未修改任何参数。", nameof(id));
    }

    public static BlePresetDefinition ParseCommand(IReadOnlyList<string> args)
    {
        string? selector = null;
        int matches = 0;
        foreach (string argument in args)
        {
            if (!argument.StartsWith(SelectorName, StringComparison.OrdinalIgnoreCase)) continue;
            matches++;
            if (matches > 1)
                throw new ArgumentException("同一操作只能指定一个 Win10 BLE 预设；未修改任何参数。", nameof(args));
            selector = argument;
        }
        if (selector == null) return Default;
        if (!selector.StartsWith(SelectorPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Win10 BLE 预设格式应为 --ble-turbo-stack-preset=7p5|flexible|15；未修改任何参数。", nameof(args));
        return Get(selector[SelectorPrefix.Length..]);
    }
}
