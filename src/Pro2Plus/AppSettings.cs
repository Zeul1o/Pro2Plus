using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pro2Plus;

public sealed class AppSettings
{
    private static readonly object FileGate = new();

    public double RumbleMultiplier { get; set; } = 0.6;
    public string PushRateLabel { get; set; } = OutputRateOption.Default.Label;
    public string BackendLabel { get; set; } = VirtualBackendOption.Default.Label;
    public string StickProcessingLabel { get; set; } = StickProcessingOption.Default.Label;

    /// <summary>
    /// 摇杆中心死区（原始轴值，0–204 ≈ 0–10%），按链路分开保存：
    /// 有线默认 2%（硬件回中残差大），蓝牙默认 0（自适应归零已经很干净）。
    /// 两条链路各读自己那份，互不干扰；显式写 0 才是真的关闭死区。
    /// 注意：死区越小越跟手，但过小的死区挡不住摇杆本身的残差，会产生不可避免的漂移。
    /// </summary>
    public int? WiredStickDeadzone { get; set; }
    public int? BleStickDeadzone { get; set; }

    /// <summary>旧版单一死区，仅用于迁移：首次加载时若新的分链路字段为空，用它填充两者。</summary>
    public int? StickDeadzone { get; set; }

    /// <summary>有线默认 2%（≈41 个原始单位）。</summary>
    public const int DefaultWiredStickDeadzone = 41;
    /// <summary>蓝牙默认 0。</summary>
    public const int DefaultBleStickDeadzone = 0;

    [JsonIgnore]
    public int EffectiveWiredStickDeadzone =>
        NormalizeStickDeadzone(WiredStickDeadzone ?? DefaultWiredStickDeadzone);

    [JsonIgnore]
    public int EffectiveBleStickDeadzone =>
        NormalizeStickDeadzone(BleStickDeadzone ?? DefaultBleStickDeadzone);

    public string SelectedModeKey { get; set; } = "pro2";
    public RearKeyConfiguration RearButtons { get; set; } = new();
    public bool LaunchAtLoginEnabled { get; set; }
    public bool AutoReconnectOnStartupEnabled { get; set; }
    public string[] LastConnectedPro2Addresses { get; set; } = new string[4];
    public bool[] EnabledPro2Slots { get; set; } = [true, false, false, false];
    public string[] SlotModeKeys { get; set; } = [];
    public Dictionary<string, StickCalibrationProfile> Pro2StickCalibrations { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>按手柄地址保存的陀螺零偏；键为 "wired" 或 "ble:XXXXXXXXXXXX"。</summary>
    public Dictionary<string, Pro2GyroBiasProfile> Pro2GyroBiases { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public bool RuntimeBatteryRefreshEnabled { get; set; } = true;
    public bool AutoSleepEnabled { get; set; }
    public int AutoSleepMinutes { get; set; } = 10;
    public bool AutoOptimizeBle { get; set; } = true;
    public string UsbipExecutablePath { get; set; } = "";
    /// <summary>PS5 原生陀螺仪映射灵敏度；1.0 为原样。</summary>
    public double Ps5NativeGyroSensitivity { get; set; } = 1.0;
    /// <summary>Pro1 原生陀螺仪映射灵敏度；1.0 为原样。</summary>
    public double Pro1NativeGyroSensitivity { get; set; } = 1.0;
    public double Ps5GyroScalePitch { get; set; } = 1.0;
    public double Ps5GyroScaleYaw { get; set; } = 1.0;
    public double Ps5GyroScaleRoll { get; set; } = 1.0;
    public bool ProfessionalInvertGyroPitch { get; set; }
    public bool ProfessionalInvertGyroYaw { get; set; }
    public bool ProfessionalInvertGyroRoll { get; set; }

    public static AppSettings Load()
    {
        lock (FileGate)
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return new AppSettings();
                }

                AppSettings? loaded =
                    JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllText(SettingsPath));
                if (loaded == null)
                {
                    return new AppSettings();
                }
                loaded.RumbleMultiplier =
                    NormalizeRumbleMultiplier(loaded.RumbleMultiplier);
                loaded.PushRateLabel =
                    OutputRateOption.FromLabel(loaded.PushRateLabel).Label;
                loaded.BackendLabel =
                    VirtualBackendOption.FromLabel(loaded.BackendLabel).Label;
                loaded.StickProcessingLabel =
                    StickProcessingOption.FromLabel(loaded.StickProcessingLabel).Label;
                loaded.SelectedModeKey =
                    NormalizeModeKey(loaded.SelectedModeKey);
                loaded.RearButtons = (loaded.RearButtons ?? new()).Normalize();
                loaded.UsbipExecutablePath = UsbipRuntimeLocator.NormalizeExecutablePath(loaded.UsbipExecutablePath);
                loaded.LastConnectedPro2Addresses =
                    NormalizeAddressSlots(loaded.LastConnectedPro2Addresses);
                loaded.EnabledPro2Slots = NormalizeEnabledSlots(loaded.EnabledPro2Slots);
                loaded.SlotModeKeys = SlotModeCatalog.Normalize(loaded.SlotModeKeys, loaded.SelectedModeKey);
                loaded.Pro2StickCalibrations =
                    NormalizeStickCalibrations(loaded.Pro2StickCalibrations);
                loaded.Pro2GyroBiases = NormalizeGyroBiases(loaded.Pro2GyroBiases);
                // 分链路死区：只读新字段。旧版 StickDeadzone 是程序自动写入的
                //（用户没主动调也写成 0/21），不可信，忽略它、直接用分链路默认值
                //（有线 2%、蓝牙 0）。用户这次调整后落盘的就是新字段。
                loaded.StickDeadzone = null;
                loaded.WiredStickDeadzone = loaded.WiredStickDeadzone is { } wiredDz
                    ? NormalizeStickDeadzone(wiredDz)
                    : null;
                loaded.BleStickDeadzone = loaded.BleStickDeadzone is { } bleDz
                    ? NormalizeStickDeadzone(bleDz)
                    : null;
                loaded.Ps5NativeGyroSensitivity = NormalizeGyroSensitivity(loaded.Ps5NativeGyroSensitivity);
                loaded.Pro1NativeGyroSensitivity = NormalizeGyroSensitivity(loaded.Pro1NativeGyroSensitivity);
                loaded.Ps5GyroScalePitch =
                    NormalizePs5GyroScale(loaded.Ps5GyroScalePitch);
                loaded.Ps5GyroScaleYaw =
                    NormalizePs5GyroScale(loaded.Ps5GyroScaleYaw);
                loaded.Ps5GyroScaleRoll =
                    NormalizePs5GyroScale(loaded.Ps5GyroScaleRoll);
                return loaded;
            }
            catch
            {
                return new AppSettings();
            }
        }
    }

    public void Save()
    {
        lock (FileGate)
        {
            EnabledPro2Slots = NormalizeEnabledSlots(EnabledPro2Slots);
            string directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            RumbleMultiplier = NormalizeRumbleMultiplier(RumbleMultiplier);
            PushRateLabel = OutputRateOption.FromLabel(PushRateLabel).Label;
            BackendLabel = VirtualBackendOption.FromLabel(BackendLabel).Label;
            StickProcessingLabel = StickProcessingOption.FromLabel(StickProcessingLabel).Label;
            SelectedModeKey = NormalizeModeKey(SelectedModeKey);
            SlotModeKeys = SlotModeCatalog.Normalize(SlotModeKeys, SelectedModeKey);
            RearButtons = (RearButtons ?? new()).Normalize();
            UsbipExecutablePath = UsbipRuntimeLocator.NormalizeExecutablePath(UsbipExecutablePath);
            LastConnectedPro2Addresses = NormalizeAddressSlots(LastConnectedPro2Addresses);
            Pro2StickCalibrations = NormalizeStickCalibrations(Pro2StickCalibrations);
            Pro2GyroBiases = NormalizeGyroBiases(Pro2GyroBiases);
            // 落盘一律写具体值，免得以后改默认值时把用户已经调好的手感顶掉。
            WiredStickDeadzone = EffectiveWiredStickDeadzone;
            BleStickDeadzone = EffectiveBleStickDeadzone;
            StickDeadzone = null;   // 旧字段只做迁移，不再写回，免得两份值打架
            Ps5NativeGyroSensitivity = NormalizeGyroSensitivity(Ps5NativeGyroSensitivity);
            Pro1NativeGyroSensitivity = NormalizeGyroSensitivity(Pro1NativeGyroSensitivity);
            Ps5GyroScalePitch = NormalizePs5GyroScale(Ps5GyroScalePitch);
            Ps5GyroScaleYaw = NormalizePs5GyroScale(Ps5GyroScaleYaw);
            Ps5GyroScaleRoll = NormalizePs5GyroScale(Ps5GyroScaleRoll);
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    this,
                    new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
    }

    public static double NormalizeRumbleMultiplier(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 1.0;
        }
        return Math.Round(Math.Clamp(value, 0.0, 3.0), 1);
    }

    public static double NormalizeGyroSensitivity(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return 1.0;
        return Math.Round(Math.Clamp(value, 0.25, 3.0), 2);
    }

    /// <summary>摇杆死区上限 204（12 位轴值的 10%），超过这个手感就钝了。</summary>
    public const int MaxStickDeadzone = 204;

    public static int NormalizeStickDeadzone(int value) =>
        Math.Clamp(value, 0, MaxStickDeadzone);

    public static double NormalizePs5GyroScale(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 1.0;
        }
        return Math.Round(Math.Clamp(value, 0.1, 4.0), 2);
    }

    public static string NormalizeModeKey(string? value)
    {
        return (value ?? "").Trim().ToLowerInvariant() switch
        {
            "dualsenseedge" or "dualsense_edge" or "edge" or "ps5edge" => "dualsense",
            "pro1" or "nspro" or "switchpro" => "pro1",
            "dualsenseproimu" or "dualsense_professional_imu" or "ps5proimu" or "ps5_professional_imu" => "dualsenseproimu",
            "pro2" or "ns2pro" or "nintendo" => "pro2",
            "xboxproimu" or "xbox_professional_imu" or "xinputproimu" => "xboxproimu",
            "xbox" or "xinput" or "xbox360" => "xbox",
            "keymouse" or "key_mouse" or "kbm" or "keyboard" or "键鼠" => "keymouse",
            _ => "dualsense"
        };
    }

    public static bool[] NormalizeEnabledSlots(bool[]? value)
    {
        bool[] slots = [true, false, false, false];
        for (int i = 1; i < 4; i++) slots[i] = value != null && i < value.Length && value[i];
        return slots;
    }

    public static string[] NormalizeAddressSlots(string[]? value)
    {
        string[] normalized = new string[4];
        if (value == null)
        {
            return normalized;
        }

        for (int i = 0; i < normalized.Length && i < value.Length; i++)
        {
            normalized[i] = NormalizeBleAddress(value[i]);
        }
        return normalized;
    }

    public static string NormalizeBleAddress(string? value)
    {
        string text = (value ?? "").Trim().ToUpperInvariant();
        if (text.Length == 0)
        {
            return "";
        }
        return text.Replace("-", ":");
    }

    public bool TryGetStickCalibration(
        string? address,
        out StickCalibrationProfile profile)
    {
        string normalizedAddress = NormalizeBleAddress(address);
        if (normalizedAddress.Length > 0 &&
            Pro2StickCalibrations.TryGetValue(normalizedAddress, out StickCalibrationProfile? saved))
        {
            profile = StickCalibrationProfile.Normalize(saved);
            if (IsPlausibleCenter(profile))
            {
                return true;
            }

            // 旧版在蓝牙上采样的是「已被归中的值」，存下来的四个中心恒为 2048（假零位）。
            // 这种档案一旦套到有线链路上会把中心按 2048 处理，而真实零位可能差 8%，
            // 还不如当它不存在，让链路自己重新学。
            profile = new StickCalibrationProfile();
            return false;
        }

        profile = new StickCalibrationProfile();
        return false;
    }

    /// <summary>四个中心恰好全等于出厂中心 = 当年采到的假零位，判为不可用。</summary>
    private static bool IsPlausibleCenter(StickCalibrationProfile profile)
    {
        if (!profile.CenterCalibrated) return false;
        return !(profile.CenterLx == GamepadState.AxisCenter &&
                 profile.CenterLy == GamepadState.AxisCenter &&
                 profile.CenterRx == GamepadState.AxisCenter &&
                 profile.CenterRy == GamepadState.AxisCenter);
    }

    public void SetStickCalibration(
        string address,
        StickCalibrationProfile profile)
    {
        string normalizedAddress = NormalizeBleAddress(address);
        StickCalibrationProfile normalizedProfile =
            StickCalibrationProfile.Normalize(profile);
        if (normalizedAddress.Length == 0 || !normalizedProfile.CenterCalibrated)
        {
            return;
        }

        Pro2StickCalibrations[normalizedAddress] = normalizedProfile;
    }

    public static Dictionary<string, StickCalibrationProfile> NormalizeStickCalibrations(
        IDictionary<string, StickCalibrationProfile>? value)
    {
        var normalized =
            new Dictionary<string, StickCalibrationProfile>(StringComparer.OrdinalIgnoreCase);
        if (value == null)
        {
            return normalized;
        }

        foreach ((string address, StickCalibrationProfile profile) in value)
        {
            string normalizedAddress = NormalizeBleAddress(address);
            StickCalibrationProfile normalizedProfile =
                StickCalibrationProfile.Normalize(profile);
            if (normalizedAddress.Length > 0 && normalizedProfile.CenterCalibrated)
            {
                normalized[normalizedAddress] = normalizedProfile;
            }
        }
        return normalized;
    }

    public Pro2GyroBiasProfile? GetGyroBias(string key)
    {
        string normalized = NormalizeGyroBiasKey(key);
        if (normalized.Length == 0 ||
            !Pro2GyroBiases.TryGetValue(normalized, out Pro2GyroBiasProfile? profile))
        {
            return null;
        }
        return Pro2GyroBiasProfile.Normalize(profile);
    }

    public void SetGyroBias(string key, Pro2GyroBiasProfile profile)
    {
        string normalized = NormalizeGyroBiasKey(key);
        Pro2GyroBiasProfile validated = Pro2GyroBiasProfile.Normalize(profile);
        if (normalized.Length == 0 || !validated.HasAny) return;
        Pro2GyroBiases[normalized] = validated;
    }

    public void ClearGyroBias(string key)
    {
        string normalized = NormalizeGyroBiasKey(key);
        if (normalized.Length > 0) Pro2GyroBiases.Remove(normalized);
    }

    public static string NormalizeGyroBiasKey(string? key)
    {
        string text = (key ?? "").Trim().ToUpperInvariant();
        if (text.Length == 0) return "";
        if (text.Equals("WIRED", StringComparison.Ordinal) ||
            text.Equals("USB", StringComparison.Ordinal))
        {
            return Pro2GyroBiasStore.WiredKey;
        }
        if (text.StartsWith("BLE:", StringComparison.Ordinal))
            return "BLE:" + NormalizeBleAddress(text[4..]);
        return "BLE:" + NormalizeBleAddress(text);
    }

    public static Dictionary<string, Pro2GyroBiasProfile> NormalizeGyroBiases(
        IDictionary<string, Pro2GyroBiasProfile>? value)
    {
        var normalized = new Dictionary<string, Pro2GyroBiasProfile>(StringComparer.OrdinalIgnoreCase);
        if (value == null) return normalized;
        foreach ((string key, Pro2GyroBiasProfile profile) in value)
        {
            string normalizedKey = NormalizeGyroBiasKey(key);
            Pro2GyroBiasProfile validated = Pro2GyroBiasProfile.Normalize(profile);
            if (normalizedKey.Length > 0 && validated.HasAny)
            {
                normalized[normalizedKey] = validated;
            }
        }
        return normalized;
    }

    private static string SettingsPath => AppPaths.ConfigFile("v6_settings.json");
}
