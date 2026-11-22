using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Pro2Plus;

/// <summary>
/// 输出选项目录重写的离线自检（PRO2_OPTIONS_TEST=1）。
/// 用旧实现的语义（All.FirstOrDefault + 散落的别名分支）作为期望值，
/// 逐标签比对新的 O(1) 查表实现，并校验格式化字符串未变。
/// </summary>
public static class OutputOptionsSmoke
{
    public static string Run()
    {
        var log = new StringBuilder();
        log.AppendLine("output options catalog smoke");
        int cases = 0, bad = 0;
        string first = "";

        void Check(string name, bool ok)
        {
            cases++;
            if (ok) return;
            bad++;
            if (first.Length == 0) first = "  first mismatch " + name;
        }

        // 旧语义：All.FirstOrDefault(label 相等) ?? Default
        static T Legacy<T>(IReadOnlyList<T> all, Func<T, string> labelOf, T fallback, string? label) where T : class =>
            all.FirstOrDefault(o => string.Equals(labelOf(o), label, StringComparison.Ordinal)) ?? fallback;

        var extra = new string?[]
        {
            null, "", "不存在的选项", " ", "125Hz（推荐）", "66Hz / source-paced",
            "250Hz（高性能）", "跟随真实 Pro2 BLE（推荐）", "source_60hz（推荐）",
            "hold_250hz", "hold_latest（推荐）"
        };

        void CheckCatalog<T>(string name, IReadOnlyList<T> all, Func<T, string> labelOf,
            Func<string?, T> fromLabel, T fallback,
            Dictionary<string, T>? aliases = null) where T : class
        {
            foreach (T item in all)
            {
                string label = labelOf(item);
                Check(name + " exact " + label, ReferenceEquals(fromLabel(label), item));
            }
            foreach (string? label in extra)
            {
                T expected = aliases != null && label != null && aliases.TryGetValue(label, out T? alias)
                    ? alias
                    : Legacy(all, labelOf, fallback, label);
                Check(name + " legacy " + (label ?? "null"), ReferenceEquals(fromLabel(label), expected));
            }
        }

        var rateAliases = new Dictionary<string, OutputRateOption>(StringComparer.Ordinal)
        {
            ["125Hz（推荐）"] = OutputRateOption.Default,
            ["66Hz / source-paced"] = OutputRateOption.Default,
            ["250Hz（高性能）"] = OutputRateOption.Default,
            ["跟随真实 Pro2 BLE（推荐）"] = OutputRateOption.Default
        };
        var imuAliases = new Dictionary<string, ImuResampleOption>(StringComparer.Ordinal)
        {
            ["source_60hz（推荐）"] = ImuResampleOption.Default,
            ["hold_250hz"] = ImuResampleOption.All[0]
        };

        CheckCatalog("OutputRateOption", OutputRateOption.All, o => o.Label,
            OutputRateOption.FromLabel, OutputRateOption.Default, rateAliases);
        CheckCatalog("ImuResampleOption", ImuResampleOption.All, o => o.Label,
            ImuResampleOption.FromLabel, ImuResampleOption.Default, imuAliases);
        CheckCatalog("GyroDirectionOption", GyroDirectionOption.All, o => o.Label,
            GyroDirectionOption.FromLabel, GyroDirectionOption.Default);
        CheckCatalog("VirtualBackendOption", VirtualBackendOption.All, o => o.Label,
            VirtualBackendOption.FromLabel, VirtualBackendOption.Default);
        CheckCatalog("StickProcessingOption", StickProcessingOption.All, o => o.Label,
            StickProcessingOption.FromLabel, StickProcessingOption.Default);
        CheckCatalog("Ps5ImuMappingOption", Ps5ImuMappingOption.All, o => o.Label,
            Ps5ImuMappingOption.FromLabel, Ps5ImuMappingOption.Default);

        // 格式化字符串：8 种布尔组合都要与旧拼接规则一致。
        for (int mask = 0; mask < 8; mask++)
        {
            var inv = new GyroAxisInversion((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
            string x = (mask & 1) != 0 ? "1" : "0";
            string y = (mask & 2) != 0 ? "1" : "0";
            string z = (mask & 4) != 0 ? "1" : "0";
            Check("GyroAxisInversion telemetry", inv.TelemetryValue == $"x{x},y{y},z{z}");
            Check("GyroAxisInversion display", inv.DisplayValue ==
                $"X{((mask & 1) != 0 ? "-" : "+")} Y{((mask & 2) != 0 ? "-" : "+")} Z{((mask & 4) != 0 ? "-" : "+")}");
        }

        var tuning = new Ps5OutputImuTuning(1.25, 0.5, 2.0);
        Check("Ps5OutputImuTuning display", tuning.DisplayValue == "Gyro 1.25x / 0.5x / 2x");
        Check("Ps5OutputImuTuning telemetry",
            tuning.TelemetryValue.StartsWith("accel=pro2_4096g_to_ds8192_+x+z-y;", StringComparison.Ordinal) &&
            tuning.TelemetryValue.Contains("scale_pitch=1.25,scale_yaw=0.5,scale_roll=2", StringComparison.Ordinal));
        Check("Ps5OutputImuTuning normalize", tuning.Normalize() ==
            new Ps5OutputImuTuning(
                AppSettings.NormalizePs5GyroScale(1.25),
                AppSettings.NormalizePs5GyroScale(0.5),
                AppSettings.NormalizePs5GyroScale(2.0)));

        var mapping = Ps5ImuMappingOption.Default.Mapping;
        Check("Ps5ImuMapping display", mapping.DisplayValue == "G +X,+Z,-Y / A +X,+Z,-Y");
        Check("Ps5ImuMapping telemetry", mapping.TelemetryValue == "g=+X,+Z,-Y;a=+X,+Z,-Y");

        if (first.Length > 0) log.AppendLine(first);
        log.AppendLine($"summary={(bad == 0 ? "PASS" : "FAIL")} cases={cases} mismatches={bad}");
        return log.ToString();
    }
}
