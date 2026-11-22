using System;
using System.Diagnostics;
using System.Globalization;

namespace Pro2Plus;

/// <summary>
/// 按手柄保存的陀螺仪零偏档案。
///
/// GyroStick 用的是投影后的 yaw/pitch（deg/s）；有线协议层用的是
/// IMU 原始三轴（counts）。两条链路各自学习、分别落盘，互不覆盖。
/// 保存位置是设置页同一份 v6_settings.json（我的文档\Pro2+），下次连接直接加载。
/// </summary>
public sealed class Pro2GyroBiasProfile
{
    public bool YawPitchCalibrated { get; set; }
    public double YawBiasDps { get; set; }
    public double PitchBiasDps { get; set; }

    public bool RawCalibrated { get; set; }
    public double RawX { get; set; }
    public double RawY { get; set; }
    public double RawZ { get; set; }

    /// <summary>auto_still / auto_fallback / manual / loaded。</summary>
    public string Source { get; set; } = "";
    public string SavedAtUtc { get; set; } = "";

    public bool HasAny => YawPitchCalibrated || RawCalibrated;

    public Pro2GyroBiasProfile Clone() => new()
    {
        YawPitchCalibrated = YawPitchCalibrated,
        YawBiasDps = YawBiasDps,
        PitchBiasDps = PitchBiasDps,
        RawCalibrated = RawCalibrated,
        RawX = RawX,
        RawY = RawY,
        RawZ = RawZ,
        Source = Source,
        SavedAtUtc = SavedAtUtc
    };

    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"yaw_pitch={(YawPitchCalibrated ? "saved" : "none")} " +
        $"yaw={YawBiasDps:F2}dps pitch={PitchBiasDps:F2}dps " +
        $"raw={(RawCalibrated ? "saved" : "none")} " +
        $"raw=({RawX:F0},{RawY:F0},{RawZ:F0}) " +
        $"source={Source} at={SavedAtUtc}");

    public static Pro2GyroBiasProfile Normalize(Pro2GyroBiasProfile? value)
    {
        var result = new Pro2GyroBiasProfile();
        if (value == null) return result;

        result.YawPitchCalibrated =
            value.YawPitchCalibrated &&
            IsSane(value.YawBiasDps, 360.0) &&
            IsSane(value.PitchBiasDps, 360.0);
        if (result.YawPitchCalibrated)
        {
            result.YawBiasDps = value.YawBiasDps;
            result.PitchBiasDps = value.PitchBiasDps;
        }

        result.RawCalibrated =
            value.RawCalibrated &&
            IsSane(value.RawX, 32768.0) &&
            IsSane(value.RawY, 32768.0) &&
            IsSane(value.RawZ, 32768.0);
        if (result.RawCalibrated)
        {
            result.RawX = value.RawX;
            result.RawY = value.RawY;
            result.RawZ = value.RawZ;
        }

        result.Source = (value.Source ?? "").Trim();
        result.SavedAtUtc = (value.SavedAtUtc ?? "").Trim();
        return result;
    }

    private static bool IsSane(double value, double limit) =>
        double.IsFinite(value) && Math.Abs(value) <= limit;
}

/// <summary>
/// 零偏档案的运行时入口。只负责把「当前手柄」和设置页文件接起来，
/// 输入的 GyroStick / Pro2WiredProtocol 不需要知道配置文件在哪。
/// </summary>
public static class Pro2GyroBiasStore
{
    public const string WiredKey = "wired";

    private const double SaveIntervalSec = 3.0;
    private const double SaveDeltaDps = 0.5;
    private const double SaveDeltaRaw = 4.0;

    private static readonly object Gate = new();
    private static AppSettings? settings;
    private static bool persist;
    private static string currentKey = WiredKey;
    private static Pro2GyroBiasProfile cache = new();
    private static long lastSaveTicks;

    public static string CurrentKey { get { lock (Gate) return currentKey; } }

    /// <summary>设置页实例，输入层只读它来取摇杆校准与灵敏度。</summary>
    public static AppSettings? Settings { get { lock (Gate) return settings; } }

    /// <summary>当前输出模式对应的原生陀螺仪灵敏度，由会话启动时设置。</summary>
    public static double ActiveGyroSensitivity { get; set; } = 1.0;

    private static int wiredStickDeadzone = AppSettings.DefaultWiredStickDeadzone;
    private static int bleStickDeadzone = AppSettings.DefaultBleStickDeadzone;

    /// <summary>有线链路的摇杆死区（原始轴值）。</summary>
    public static int WiredStickDeadzone => System.Threading.Volatile.Read(ref wiredStickDeadzone);

    /// <summary>蓝牙链路的摇杆死区（原始轴值）。</summary>
    public static int BleStickDeadzone => System.Threading.Volatile.Read(ref bleStickDeadzone);

    /// <summary>
    /// 当前链路的摇杆死区：由 currentKey 判断（wired / BLE:MAC）。
    /// 解析器每帧读它，设置页改完立刻生效，不用重连。
    /// </summary>
    public static int ActiveStickDeadzone =>
        IsWiredKey(currentKey) ? WiredStickDeadzone : BleStickDeadzone;

    private static bool IsWiredKey(string key) =>
        !key.StartsWith("BLE:", StringComparison.OrdinalIgnoreCase);

    /// <summary>按链路设置死区，两条链路互不干扰。</summary>
    public static void SetStickDeadzone(int raw, bool wired)
    {
        int normalized = AppSettings.NormalizeStickDeadzone(raw);
        if (wired)
            System.Threading.Volatile.Write(ref wiredStickDeadzone, normalized);
        else
            System.Threading.Volatile.Write(ref bleStickDeadzone, normalized);
    }

    /// <summary>兼容旧调用：设置当前链路的死区。</summary>
    public static void SetStickDeadzone(int raw) => SetStickDeadzone(raw, IsWiredKey(currentKey));

    /// <summary>
    /// 摇杆校准的存储身份：**同一支手柄**在 USB 与蓝牙下用同一份。
    /// 蓝牙用本机地址；有线没有地址，就沿用这只手柄上次蓝牙的地址，
    /// 这样「有线校准 → 拔线用蓝牙」和「蓝牙校准 → 插线用 USB」都能读到同一份档案。
    /// 从没连过蓝牙时退回 wired，行为与旧版一致。
    /// </summary>
    public static string StickCalibrationKey(string? key)
    {
        string text = (key ?? "").Trim();
        if (text.StartsWith("BLE:", StringComparison.OrdinalIgnoreCase))
        {
            return text[4..].Trim().ToUpperInvariant();
        }

        if (Connection.BleAddressStore.TryLoad(out Connection.Pro2BleCandidate saved) &&
            saved.Address != 0)
        {
            return saved.Address.ToString("X12");
        }

        return WiredKey;
    }

    /// <summary>当前手柄已保存的摇杆校准档案（两条链路共用一份）。</summary>
    public static StickCalibrationProfile? LoadStickCalibration()
    {
        lock (Gate)
        {
            if (settings is null) return null;
            string address = StickCalibrationKey(currentKey);
            return settings.TryGetStickCalibration(address, out StickCalibrationProfile profile)
                ? profile
                : null;
        }
    }

    /// <summary>链路切到另一只手柄时通知运行中的输出会话重新加载档案。</summary>
    public static event Action<string>? DeviceKeyChanged;

    /// <summary>档案写入或清除时通知设置页刷新状态。</summary>
    public static event Action? BiasSaved;

    public static void Configure(AppSettings userSettings, bool allowPersist)
    {
        lock (Gate)
        {
            settings = userSettings;
            persist = allowPersist;
            currentKey = WiredKey;
            cache = LoadNoLock(currentKey);
            lastSaveTicks = 0;
            wiredStickDeadzone = userSettings.EffectiveWiredStickDeadzone;
            bleStickDeadzone = userSettings.EffectiveBleStickDeadzone;
        }
    }

    /// <summary>连接层切换到具体手柄时调用：蓝牙按地址分档，有线共用一档。</summary>
    public static void SetDeviceKey(string key, out Pro2GyroBiasProfile loaded)
    {
        string normalized = string.IsNullOrWhiteSpace(key)
            ? WiredKey
            : key.Trim().ToUpperInvariant();
        bool changed;
        lock (Gate)
        {
            changed = !string.Equals(currentKey, normalized, StringComparison.Ordinal);
            if (changed)
            {
                currentKey = normalized;
                cache = LoadNoLock(currentKey);
                lastSaveTicks = 0;
            }
            loaded = cache.Clone();
        }
        // 回调不能放在锁内：订阅方会再读 Load()。
        if (changed) DeviceKeyChanged?.Invoke(normalized);
    }

    public static Pro2GyroBiasProfile Load()
    {
        lock (Gate) return cache.Clone();
    }

    /// <summary>投影零偏（陀螺输出层）。force 用于学习完成/断开时立即落盘。</summary>
    public static void SaveYawPitch(double yawDps, double pitchDps, string source, bool force = false)
    {
        bool written;
        lock (Gate)
        {
            if (!double.IsFinite(yawDps) || !double.IsFinite(pitchDps)) return;
            if (Math.Abs(yawDps) > 360.0 || Math.Abs(pitchDps) > 360.0) return;
            if (!force && !ShouldSaveNoLock(yawDps, pitchDps, cache.RawX, cache.RawY, cache.RawZ,
                    SaveDeltaDps, SaveDeltaDps, SaveDeltaRaw)) return;

            Pro2GyroBiasProfile next = cache.Clone();
            next.YawPitchCalibrated = true;
            next.YawBiasDps = yawDps;
            next.PitchBiasDps = pitchDps;
            next.Source = source;
            written = WriteNoLock(next);
        }
        if (written) BiasSaved?.Invoke();
    }

    /// <summary>IMU 原始三轴零偏（有线解析层）。</summary>
    public static void SaveRaw(double x, double y, double z, string source, bool force = false)
    {
        bool written;
        lock (Gate)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return;
            if (Math.Abs(x) > 32768.0 || Math.Abs(y) > 32768.0 || Math.Abs(z) > 32768.0) return;
            if (!force && !ShouldSaveNoLock(cache.YawBiasDps, cache.PitchBiasDps, x, y, z,
                    SaveDeltaDps, SaveDeltaDps, SaveDeltaRaw)) return;

            Pro2GyroBiasProfile next = cache.Clone();
            next.RawCalibrated = true;
            next.RawX = x;
            next.RawY = y;
            next.RawZ = z;
            next.Source = source;
            written = WriteNoLock(next);
        }
        if (written) BiasSaved?.Invoke();
    }

    /// <summary>设置页点「重新校准」时清掉当前手柄的旧档案。</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            settings?.ClearGyroBias(currentKey);
            PersistNoLock();
            cache = new Pro2GyroBiasProfile();
            lastSaveTicks = 0;
        }
        BiasSaved?.Invoke();
    }

    private static Pro2GyroBiasProfile LoadNoLock(string key)
    {
        Pro2GyroBiasProfile? found = settings?.GetGyroBias(key);
        return Pro2GyroBiasProfile.Normalize(found);
    }

    private static bool ShouldSaveNoLock(
        double yaw, double pitch, double x, double y, double z,
        double deltaYaw, double deltaPitch, double deltaRaw)
    {
        if (lastSaveTicks == 0) return true;
        if (!cache.HasAny) return true;
        if (Stopwatch.GetElapsedTime(lastSaveTicks, Stopwatch.GetTimestamp()).TotalSeconds < SaveIntervalSec)
            return false;
        return Math.Abs(yaw - cache.YawBiasDps) > deltaYaw ||
               Math.Abs(pitch - cache.PitchBiasDps) > deltaPitch ||
               Math.Abs(x - cache.RawX) > deltaRaw ||
               Math.Abs(y - cache.RawY) > deltaRaw ||
               Math.Abs(z - cache.RawZ) > deltaRaw;
    }

    private static bool WriteNoLock(Pro2GyroBiasProfile profile)
    {
        profile.SavedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        profile = Pro2GyroBiasProfile.Normalize(profile);
        settings?.SetGyroBias(currentKey, profile);
        cache = profile;
        lastSaveTicks = Stopwatch.GetTimestamp();
        PersistNoLock();
        return true;
    }

    private static void PersistNoLock()
    {
        if (!persist || settings is null) return;
        try
        {
            settings.Save();
        }
        catch
        {
            // 设置页写盘失败不能影响输入线程；下次静止跟踪还会再试。
        }
    }
}
