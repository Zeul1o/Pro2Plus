using System;
using System.IO;
using System.Text.Json;

namespace Pro2Plus;

/// <summary>
/// 本地保存的一条「精确环境」提示：只调整重测顺序，绝不跳过验收。
/// 写入采用临时文件 + 原子替换，读时同时校验环境与时效。
/// </summary>
public static class AdaptivePresetCache
{
    private static readonly object FileGate = new();

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pro2Plus", "ble-adaptive-preset-v1.json");

    public static BleAdaptiveCachedPreset? Load(BleAdaptiveEnvironmentKey? environment, out string code, string? path = null)
    {
        code = "cache_environment_unavailable";
        if (environment == null) return null;
        lock (FileGate)
        {
            try
            {
                var file = new FileInfo(path ?? DefaultPath);
                if (!file.Exists) { code = "cache_absent"; return null; }
                if (file.Length > 65536) { code = "cache_oversized"; return null; }
                var entry = JsonSerializer.Deserialize<BleAdaptiveCachedPreset>(File.ReadAllText(file.FullName));
                return entry != null && BleAdaptivePresetPolicy.IsCacheUsable(entry, environment, DateTimeOffset.UtcNow, out code)
                    ? entry : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
            { code = "cache_read_" + ex.GetType().Name; return null; }
        }
    }

    public static void Save(BleAdaptiveCachedPreset entry, string? path = null)
    {
        if (!BleAdaptivePresetPolicy.IsCacheUsable(entry, entry.Environment, DateTimeOffset.UtcNow, out _))
            throw new ArgumentException("Only fully measured exact-environment preferences may be stored.", nameof(entry));
        lock (FileGate)
        {
            string target = Path.GetFullPath(path ?? DefaultPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entry));
            File.Move(temp, target, overwrite: true);
        }
    }

    public static void Invalidate(BleAdaptiveEnvironmentKey? environment, string? path = null)
    {
        if (environment == null) return;
        lock (FileGate)
        {
            var entry = Load(environment, out _, path);
            // 绝不因为一次失效操作删掉其它环境的提示。
            if (entry != null) File.Delete(path ?? DefaultPath);
        }
    }
}
