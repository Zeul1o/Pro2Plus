// 陀螺仪调教 —— 思路来自 Switch2Gyro/gyro_pad.py，实现已重写（与原版差异较大）。
//
// 链路：Switch IMU 原始样本 → 重力投影(玩家空间) → 零偏学习 → 抗抖滤波 →
//       摇杆量 → 死区补偿 → 叠加到物理右摇杆。
//
// 参数按 gyro 段 / tune 段分组，
// 可以直接把调好的参数存成 gyro.json 放到程序目录用。

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pro2Plus;

public sealed class GyroSection
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("start_enabled")] public bool StartEnabled { get; set; } = true;

    [JsonPropertyName("full_speed_dps")] public double FullSpeedDps { get; set; } = 85.0;

    // 陀螺仪灵敏度倍率（界面上唯一的灵敏度旋钮）。
    // 1.0 = full_speed_dps 原值。实际满转速度 = full_speed_dps / scale，
    // 所以倍率调大 = 更灵敏。手柄模式（摇杆输出）和键鼠模式（鼠标速度）
    // 都会按同一比例变化：键鼠的鼠标速度 = 陀螺输出 × gyro_aim_speed，
    // 而陀螺输出本身已经按 full_speed_dps 归一化过，不需要再乘一次。
    [JsonPropertyName("sensitivity_scale")] public double SensitivityScale { get; set; } = 1.0;

    [JsonIgnore]
    public double EffectiveFullSpeedDps
    {
        get
        {
            double scale = double.IsFinite(SensitivityScale) && SensitivityScale > 0
                ? Math.Clamp(SensitivityScale, 0.25, 5.0)
                : 1.0;
            return FullSpeedDps / scale;
        }
    }
    [JsonPropertyName("scale_yaw")] public double ScaleYaw { get; set; } = 1.25;
    [JsonPropertyName("scale_pitch")] public double ScalePitch { get; set; } = 1.0;
    // 只放大「准星向下」（压枪）那一侧的俯仰输出。默认 1.0 = 与 scale_pitch 完全一致，
    // 不改变既有手感。压枪时手腕下压行程短、握姿受限，实机上手感偏软时用它可以单独补，
    // 抬头方向与偏航轴完全不受影响。
    [JsonPropertyName("scale_pitch_down")] public double ScalePitchDown { get; set; } = 1.0;
    [JsonPropertyName("invert_yaw")] public bool InvertYaw { get; set; } = true;
    [JsonPropertyName("invert_pitch")] public bool InvertPitch { get; set; } = true;

    [JsonPropertyName("stick_passthrough")] public bool StickPassthrough { get; set; } = true;
    [JsonPropertyName("right_stick_deadzone")] public double RightStickDeadzone { get; set; } = 0.05;
    [JsonPropertyName("anti_deadzone")] public double AntiDeadzone { get; set; } = 0.18;

    [JsonPropertyName("gravity_tau_sec")] public double GravityTauSec { get; set; } = 0.25;
    [JsonPropertyName("zero_learn_sec")] public double ZeroLearnSec { get; set; } = 1.2;
    [JsonPropertyName("gyro_bias_tau_sec")] public double GyroBiasTauSec { get; set; } = 6.0;

    // 陀螺开关：c / gr / gl / l3 / r3 / none
    [JsonPropertyName("gyro_off_hold")] public string GyroToggleButton { get; set; } = "c";
}

public sealed class GyroTuneSection
{
    [JsonPropertyName("hystRiseTime")] public double HystRiseTime { get; set; } = 0.0029;
    [JsonPropertyName("hystFallTime")] public double HystFallTime { get; set; } = 0.065;
    [JsonPropertyName("peakDecayTime")] public double PeakDecayTime { get; set; } = 0.065;
    [JsonPropertyName("peakMinTrigger")] public double PeakMinTrigger { get; set; } = 2.0;
    [JsonPropertyName("freezeDurationSec")] public double FreezeDurationSec { get; set; } = 0.016;
    [JsonPropertyName("freezeOutMul")] public double FreezeOutMul { get; set; } = 0.35;
    [JsonPropertyName("freezeSuppressThr")] public double FreezeSuppressThr { get; set; } = 0.5;
    [JsonPropertyName("signChangeThr")] public double SignChangeThr { get; set; } = 1.2;
    [JsonPropertyName("enableVectorReversal")] public bool EnableVectorReversal { get; set; } = true;
    [JsonPropertyName("dirChangeMagRatio")] public double DirChangeMagRatio { get; set; } = 0.15;
    [JsonPropertyName("dirChangeAngleRad")] public double DirChangeAngleRad { get; set; } = 2.2;
    [JsonPropertyName("oneEuroFcMin")] public double OneEuroFcMin { get; set; } = 1.7;
    [JsonPropertyName("oneEuroBeta")] public double OneEuroBeta { get; set; } = 0.058;
    [JsonPropertyName("smoothThreshold")] public double SmoothThreshold { get; set; } = 7.0;
    [JsonPropertyName("smoothTimeSec")] public double SmoothTimeSec { get; set; } = 0.0075;
    [JsonPropertyName("dropSensitivity")] public double DropSensitivity { get; set; } = 2.0;
    [JsonPropertyName("dropCurve")] public double DropCurve { get; set; } = 2.0;
    // 抑制上限（0~1）：无论甩得多猛，输出最多被压掉这么多。
    // 1.0 = 旧行为（能压到 0，急停那一下等于把这一帧吃掉，手感像回弹）；
    // 0.4~0.5 = 甩枪仍然跟手，只是略微收一收尾巴。
    [JsonPropertyName("maxSuppress")] public double MaxSuppress { get; set; } = 0.45;
    // 冷知识：抑制是「乘 (1-抑制)」，只能让输出变小，不会反向；
    // 感觉「往回弹」其实是甩出去的距离被削短了，回正时全额生效造成的落差。
    // 所以真正的旋钮是 maxSuppress 和 hystFallTime（松开速度）。
    [JsonPropertyName("driftHoldTimeSec")] public double DriftHoldTimeSec { get; set; } = 0.15;
    [JsonPropertyName("driftThreshold")] public double DriftThreshold { get; set; } = 1.5;
    [JsonPropertyName("enableDriftHold")] public bool EnableDriftHold { get; set; } = true;
}

public sealed class GyroConfig
{
    [JsonPropertyName("gyro")] public GyroSection Gyro { get; set; } = new();
    [JsonPropertyName("tune")] public GyroTuneSection Tune { get; set; } = new();
    // 键鼠模式参数（灵敏度、死区、组合键窗口）。缺这一段时用内置默认值。
    [JsonPropertyName("keymouse")] public KeyMouseSection KeyMouse { get; set; } = new();

    public const string FileName = "gyro.json";

    public static string DefaultPath => AppPaths.ConfigFile(FileName);

    /// <summary>把当前参数写回 gyro.json（界面点保存时用）。</summary>
    public bool TrySave(out string error)
    {
        try
        {
            KeyMouse?.Normalize();
            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// C 键切换陀螺仪时只改 gyro.json 的 start_enabled 一项：
    /// 不能把设置页里还没点保存的草稿顺手写进磁盘。
    /// </summary>
    public static void PersistStartEnabled(bool enabled)
    {
        if (!AllowPersist) return;
        try
        {
            GyroConfig stored = LoadOrDefault(out _);
            stored.Gyro.StartEnabled = enabled;
            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(stored,
                new JsonSerializerOptions { WriteIndented = true }));
            StartEnabledPersisted?.Invoke(enabled);
        }
        catch
        {
            // 配置目录只读时保留内存状态，至少本次运行内仍然沿用最后一次切换。
        }
    }

    /// <summary>落盘成功后通知界面同步「启动陀螺仪」开关。</summary>
    public static event Action<bool>? StartEnabledPersisted;

    /// <summary>预览 / 自检模式不写用户配置。</summary>
    public static bool AllowPersist { get; set; } = true;

    public static GyroConfig LoadOrDefault(out string note)
    {
        string path = DefaultPath;
        if (!File.Exists(path))
        {
            note = "[GYRO] 未找到 " + path + "，使用内置默认参数（当前定版）。";
            var defaults = new GyroConfig();
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(defaults,
                    new JsonSerializerOptions { WriteIndented = true }));
                note += " 已生成模板文件。";
            }
            catch
            {
                // 目录只读时静默
            }
            return defaults;
        }

        try
        {
            string text = File.ReadAllText(path);
            GyroConfig? loaded = JsonSerializer.Deserialize<GyroConfig>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (loaded == null)
            {
                note = "[GYRO] 配置文件为空，使用默认参数。";
                return new GyroConfig();
            }
            note = "[GYRO] 已加载参数：" + path;
            return loaded;
        }
        catch (Exception ex)
        {
            note = "[GYRO] 参数文件解析失败（" + ex.Message + "），使用默认参数。";
            return new GyroConfig();
        }
    }
}

/// 陀螺单轴状态
internal sealed class GyroAxis
{
    public double PeakValue;
    public double PrevPeakValue;
    public double PrevOut;
    public double PrevAbsV;
    public double PrevV;
    public double StillTimeSec;
    public double FreezeRemainingSec;
    public double SuppressHyst;
    public double OneEuroPrev;

    public void Reset()
    {
        PeakValue = 0; PrevPeakValue = 0; PrevOut = 0; PrevAbsV = 0; PrevV = 0;
        StillTimeSec = 0; FreezeRemainingSec = 0; SuppressHyst = 0; OneEuroPrev = 0;
    }
}

/// 两轴陀螺滤波器
public sealed class GyroFilter
{
    private const double DtMin = 1e-3;
    private const double DtMax = 0.05;
    private const double DropSpeedRefHz = 1000.0;
    private const double FastFallRatio = 0.446;

    private readonly GyroAxis sx = new();
    private readonly GyroAxis sy = new();
    private double dirX;
    private double dirY;
    private bool primed;

    public GyroTuneSection P { get; }

    public GyroFilter(GyroTuneSection p) => P = p;

    public double SuppressXDeg => sx.SuppressHyst;
    public double SuppressYDeg => sy.SuppressHyst;

    public void Reset()
    {
        sx.Reset();
        sy.Reset();
        dirX = 0;
        dirY = 0;
        primed = false;
    }

    public void Apply(double x, double y, double dt, out double outX, out double outY)
    {
        dt = Clamp(dt, DtMin, DtMax);
        if (!primed)
        {
            sx.Reset();
            sy.Reset();
            dirX = 0;
            dirY = 0;
            primed = true;
        }

        double alpha = 1.0 - Math.Exp(-dt / Math.Max(P.SmoothTimeSec, 1e-6));

        bool force = false;
        if (P.EnableVectorReversal)
        {
            double prevLen2 = dirX * dirX + dirY * dirY;
            double curLen2 = x * x + y * y;
            if (prevLen2 > 1e-12 && curLen2 > 1e-12)
            {
                double prevLen = Math.Sqrt(prevLen2);
                double curLen = Math.Sqrt(curLen2);
                double dot = dirX * x + dirY * y;
                double cosTheta = Clamp(dot / (prevLen * curLen), -1.0, 1.0);
                if (cosTheta < Math.Cos(P.DirChangeAngleRad))
                {
                    double sinTheta = Math.Sqrt(Clamp(1.0 - cosTheta * cosTheta, 0.0, 1.0));
                    force = sinTheta > P.DirChangeMagRatio;
                }
            }
        }

        // 先比较再吸收（顺序不能反，否则方向 EWMA 会被当前帧污染）
        dirX += alpha * (x - dirX);
        dirY += alpha * (y - dirY);

        double speed = Math.Sqrt(x * x + y * y);
        double w = TierWeight(speed, P.SmoothThreshold);
        outX = DoFilter(sx, x, dt, force, w, speed);
        outY = DoFilter(sy, y, dt, force, w, speed);
    }

    private static double TierWeight(double speed, double threshold)
    {
        if (threshold <= 0.0) return 1.0;
        double bottom = threshold * 0.5;
        if (speed <= bottom) return 0.0;
        if (speed >= threshold) return 1.0;
        return (speed - bottom) / (threshold - bottom);
    }

    private double DoFilter(GyroAxis st, double v, double dt, bool forceSuppress,
        double tierWeight, double vectorSpeed)
    {
        double absV = Math.Abs(v);

        double dropSpeed = 0.0;
        if (st.PeakValue > P.PeakMinTrigger)
        {
            double delta = st.PrevAbsV - absV;
            if (delta > 0.0)
            {
                dropSpeed = delta / (dt * DropSpeedRefHz) / (st.PeakValue + 0.001);
            }
        }

        double riseFactor = 1.0 - Math.Exp(-dt / Math.Max(P.HystRiseTime, 1e-6));
        double fastFallFactor = 1.0 - Math.Exp(-dt / Math.Max(P.HystFallTime * FastFallRatio, 1e-6));
        double slowFallFactor = 1.0 - Math.Exp(-dt / Math.Max(P.HystFallTime, 1e-6));

        double fallFactor = fastFallFactor + (slowFallFactor - fastFallFactor) * st.SuppressHyst;
        fallFactor = Clamp(fallFactor, 0.0, 1.0);

        st.PrevPeakValue = st.PeakValue;
        if (absV > st.PeakValue) st.PeakValue = absV;
        else st.PeakValue *= Math.Exp(-dt / Math.Max(P.PeakDecayTime, 1e-6));

        if (P.EnableDriftHold && P.DriftHoldTimeSec > 0.0)
        {
            if (absV < P.DriftThreshold) st.StillTimeSec += dt;
            else st.StillTimeSec = 0.0;
            if (st.StillTimeSec >= P.DriftHoldTimeSec)
            {
                st.Reset();
                return 0.0;
            }
        }

        double rawSuppress = 0.0;
        if (st.PeakValue > P.PeakMinTrigger)
        {
            double raw = Clamp(dropSpeed * P.DropSensitivity, 0.0, 1.0);
            rawSuppress = Math.Pow(raw, P.DropCurve);
        }

        if (P.SignChangeThr > 0.0 && st.PeakValue > P.PeakMinTrigger)
        {
            bool bothNonZero = Math.Abs(st.PrevV) > 1e-9 && Math.Abs(v) > 1e-9;
            bool signedFlip = bothNonZero && ((st.PrevV < 0.0) != (v < 0.0));
            double refDelta = Math.Abs(v - st.PrevV) / (dt * DropSpeedRefHz);
            if (signedFlip && refDelta > P.SignChangeThr)
            {
                rawSuppress = 1.0;
                st.SuppressHyst = 1.0;
            }
        }

        if (forceSuppress)
        {
            rawSuppress = 1.0;
            st.SuppressHyst = 1.0;
        }

        if (rawSuppress > st.SuppressHyst)
        {
            st.SuppressHyst = st.SuppressHyst * (1.0 - riseFactor) + rawSuppress * riseFactor;
        }
        else
        {
            st.SuppressHyst = st.SuppressHyst * (1.0 - fallFactor) + rawSuppress * fallFactor;
        }
        st.SuppressHyst = Clamp(st.SuppressHyst, 0.0, 1.0);

        // 抑制上限：旧行为能压到 0，急停那一帧等于完全吃掉，甩出去的距离变短，
        // 回正时却全额生效 → 主观上就是「弹回来了」。这里给一个硬上限。
        double suppress = Clamp(st.SuppressHyst, 0.0, Clamp(P.MaxSuppress, 0.0, 1.0));
        st.PrevAbsV = absV;

        // 分层平滑：拆输入（直通 + 平滑），不是混输出
        double direct = v * tierWeight;
        double smoothed = OneEuro(v * (1.0 - tierWeight), dt,
            P.OneEuroFcMin + P.OneEuroBeta * vectorSpeed, st);
        double outValue = direct + smoothed;

        outValue *= 1.0 - suppress;

        if (P.FreezeDurationSec > 0.0)
        {
            if (suppress > P.FreezeSuppressThr) st.FreezeRemainingSec = P.FreezeDurationSec;
            if (st.FreezeRemainingSec > 0.0)
            {
                outValue *= P.FreezeOutMul;
                st.FreezeRemainingSec -= dt;
                if (st.FreezeRemainingSec < 0.0) st.FreezeRemainingSec = 0.0;
            }
        }

        st.PrevOut = outValue;
        st.PrevV = v;
        return outValue;
    }

    private static double OneEuro(double x, double dt, double fc, GyroAxis st)
    {
        double tau = 1.0 / (2.0 * Math.PI * fc + 0.00001);
        double alpha = 1.0 / (1.0 + tau / dt);
        double output = alpha * x + (1.0 - alpha) * st.OneEuroPrev;
        st.OneEuroPrev = output;
        return output;
    }

    internal static double Clamp(double v, double lo, double hi) =>
        v < lo ? lo : (v > hi ? hi : v);
}

/// 用重力方向定义 yaw 轴，pitch 用本地俯仰轴（对应 gyro_pad.py 的 PlayerSpace）
public sealed class PlayerSpace
{
    private readonly double tau;
    private double gx, gy, gz;
    private bool primed;

    public PlayerSpace(double gravityTauSec) => tau = Math.Max(gravityTauSec, 1e-3);

    /// <summary>当前估计的重力方向（项目坐标）。陀螺的 yaw 就是绕它的分量，诊断用。</summary>
    public (double X, double Y, double Z) Gravity => (gx, gy, gz);

    public void Reset()
    {
        primed = false;
        gx = 0; gy = 1.0; gz = 0;
    }

    public void Update(double ax, double ay, double az,
        double wx, double wy, double wz, double dt,
        out double yaw, out double pitch)
    {
        double norm = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (norm < 0.3)
        {
            ax = gx; ay = gy; az = gz; norm = 1.0;
        }
        ax /= norm; ay /= norm; az /= norm;

        if (!primed)
        {
            gx = ax; gy = ay; gz = az;
            primed = true;
        }
        else
        {
            double a = 1.0 - Math.Exp(-dt / tau);
            gx += a * (ax - gx);
            gy += a * (ay - gy);
            gz += a * (az - gz);
            double gn = Math.Sqrt(gx * gx + gy * gy + gz * gz);
            if (gn < 1e-9) gn = 1.0;
            gx /= gn; gy /= gn; gz /= gn;
        }

        yaw = wx * gx + wy * gy + wz * gz;

        double dotX = gx;
        double px = 1.0 - gx * dotX;
        double py = -gy * dotX;
        double pz = -gz * dotX;
        double pn = Math.Sqrt(px * px + py * py + pz * pz);
        if (pn < 1e-4)
        {
            pitch = wx;
        }
        else
        {
            pitch = (wx * px + wy * py + wz * pz) / pn;
        }
    }
}

/// 完整的「陀螺 → 右摇杆」处理器
public sealed class GyroStick
{
    private const double GyroRawPerDps = 16.384;
    private const double AccelRawPerG = 4096.0;

    // GamepadState 轴是 12 位无符号
    private const double AxisCenter = GamepadState.AxisCenter;   // 2048
    private const double AxisSpanPos = GamepadState.AxisMax - AxisCenter; // 2047
    private const double AxisSpanNeg = AxisCenter;               // 2048

    /// 俯仰（上下）方向的链路符号。见 Apply 中的说明。
    private const double PitchAxisLinkSign = -1.0;

    private readonly GyroConfig config;
    private readonly GyroFilter filter;
    private readonly PlayerSpace space;

    private double biasYaw;
    private double biasPitch;
    private int learnCount;
    private double learnSec;
    private double sumYaw;
    private double sumPitch;
    private double sumYaw2;
    private double sumPitch2;
    private bool zeroReady;

    /// <summary>
    /// 累计学习时长：只增不减。用户一上手就开始转，静止判据会反复重来，
    /// 攒够这个时长就直接用当前均值当零点，宁可略差也不能让陀螺一直出 0。
    /// </summary>
    private double learnTotalSec;

    // 静止检测：用最近若干帧角速度的「极差」判断有没有在动。
    // 这比「输出接近 0」可靠得多——零偏本身就偏 10dps 时，输出永远接近不了 0，
    // 旧逻辑会一直不进修正区，于是摇杆被顶死一个方向（实测向右/向下漂移）。
    private const int StillFrames = 24;
    private const double StillSpreadDps = 2.0;
    private const double BiasTrackTauSec = 0.6;
    private readonly double[] stillYaw = new double[StillFrames];
    private readonly double[] stillPitch = new double[StillFrames];
    private int stillIndex;
    private int stillCount;

    /// <summary>零点是用「兜底估算」得到的（学习期手柄在动）。</summary>
    public bool RoughZero { get; private set; }

    /// <summary>当前零偏来源：saved / auto_still / auto_fallback / learning。</summary>
    public string BiasSource { get; private set; } = "learning";

    /// <summary>零点来自上次保存的档案，本次连接无需重新静置学习。</summary>
    public bool BiasFromSaved { get; private set; }

    private long lastTicks;
    private uint lastUpdate;
    private bool haveLastUpdate;

    public bool GyroOn { get; private set; }
    public GyroConfig Config => config;
    public bool ZeroReady => zeroReady;
    public double BiasYawDeg => biasYaw;
    public double BiasPitchDeg => biasPitch;
    public double LastYaw { get; private set; }
    public double LastPitch { get; private set; }
    public double LastYawOut { get; private set; }
    public double LastPitchOut { get; private set; }
    public double LastRx { get; private set; }
    public double LastRy { get; private set; }
    public double LearnRemaining => zeroReady ? 0.0 : Math.Max(config.Gyro.ZeroLearnSec - learnSec, 0.0);
    public GyroFilter Filter => filter;

    public GyroStick(GyroConfig cfg)
    {
        config = cfg;
        filter = new GyroFilter(cfg.Tune);
        space = new PlayerSpace(cfg.Gyro.GravityTauSec);
        GyroOn = cfg.Gyro.StartEnabled;
        ReloadSavedBias();
    }

    /// <summary>
    /// 连接新手柄时调用。设置页里已存过零偏就直接就绪，
    /// 不再要求用户每次插线/开机都把手柄放平等学习窗口。
    /// </summary>
    public void ReloadSavedBias()
    {
        Pro2GyroBiasProfile saved = Pro2GyroBiasStore.Load();
        if (saved.YawPitchCalibrated)
        {
            biasYaw = saved.YawBiasDps;
            biasPitch = saved.PitchBiasDps;
            zeroReady = true;
            RoughZero = false;
            BiasSource = "saved";
            BiasFromSaved = true;
        }
        else
        {
            zeroReady = false;
            BiasFromSaved = false;
            BiasSource = "learning";
        }
        learnCount = 0;
        learnSec = 0;
        learnTotalSec = 0;
        sumYaw = sumPitch = sumYaw2 = sumPitch2 = 0;
        stillCount = 0;
        stillIndex = 0;
        Array.Clear(stillYaw);
        Array.Clear(stillPitch);
    }

    /// <summary>设置页点「重新校准」：清掉旧档案，本次连接重新学习。</summary>
    public void ClearSavedBias()
    {
        Pro2GyroBiasStore.Clear();
        biasYaw = 0;
        biasPitch = 0;
        zeroReady = false;
        RoughZero = false;
        BiasFromSaved = false;
        BiasSource = "learning";
        learnCount = 0;
        learnSec = 0;
        learnTotalSec = 0;
        sumYaw = sumPitch = sumYaw2 = sumPitch2 = 0;
        stillCount = 0;
        stillIndex = 0;
        Array.Clear(stillYaw);
        Array.Clear(stillPitch);
    }

    /// <summary>断开/退出时把当前零偏立即写回设置页。</summary>
    public void FlushBias()
    {
        if (!zeroReady) return;
        Pro2GyroBiasStore.SaveYawPitch(biasYaw, biasPitch, BiasSource, force: true);
    }

    /// <summary>
    /// 最近一帧的「纯陀螺」输出（-1..1，已包含方向符号与压枪增益），不含物理右摇杆。
    /// 键鼠模式用它单独驱动鼠标，从而可以给摇杆和陀螺设置不同的灵敏度。
    /// </summary>
    public double LastGyroUnitX { get; private set; }
    public double LastGyroUnitY { get; private set; }

    /// <summary>重力方向估计（项目坐标），诊断用。</summary>
    public (double X, double Y, double Z) GravityVector => space.Gravity;

    public void Toggle()
    {
        GyroOn = !GyroOn;
        PersistEnabled();
        // 切换时清滤波状态：关闭期间若一直在甩，抑制会累积到 1.0，
        // 切回来第一下会被吃掉（表现为「切回来拉不动」）。
        filter.Reset();
    }

    public void SetEnabled(bool on)
    {
        GyroOn = on;
        PersistEnabled();
        filter.Reset();
    }

    /// <summary>
    /// 把当前开关同步进配置并落盘：下次启动、换连接方式、重建会话都沿用最后一次切换。
    /// </summary>
    private void PersistEnabled()
    {
        config.Gyro.StartEnabled = GyroOn;
        GyroConfig.PersistStartEnabled(GyroOn);
    }

    /// 处理一帧手柄状态。
    /// <paramref name="togglePressed"/> 为 true 时切换陀螺开关（上升沿由调用方保证）。
    public GamepadState Apply(GamepadState state, long nowTicks, bool togglePressed)
    {
        if (togglePressed) Toggle();

        if (!config.Gyro.Enabled)
        {
            return state;
        }

        SwitchImuRawSample[] samples = state.SwitchRawImuSamples;
        if (samples == null || samples.Length == 0)
        {
            return state;
        }

        // 同一个源状态被重复推送时不要重复积分
        if (haveLastUpdate && state.Updates == lastUpdate)
        {
            return WriteSticks(state, LastRx, LastRy);
        }
        lastUpdate = state.Updates;
        haveLastUpdate = true;

        double dtPack = lastTicks == 0
            ? 1.0 / 133.0
            : (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
        lastTicks = nowTicks;
        dtPack = GyroFilter.Clamp(dtPack, 0.001, 0.05);
        double dt = dtPack / samples.Length;

        // 物理右摇杆（-1..1），项目已在解析层做过中心校准
        double physRx = AxisToUnit(state.Rx);
        double physRy = AxisToUnit(state.Ry);

        if (!GyroOn)
        {
            // C 键只关「陀螺转右摇杆」这一路：右摇杆回到物理值。
            // 绝不碰 state.GyroX/Y/Z —— 那是 Pro1/PS5 原生陀螺仪链路，
            // 由 Pro2RuntimeCalibration 单独处理，必须始终在线。
            LastRx = physRx;
            LastRy = physRy;
            LastGyroUnitX = 0.0;
            LastGyroUnitY = 0.0;
            return state;
        }

        double outX = 0.0, outY = 0.0;
        foreach (SwitchImuRawSample raw in samples)
        {
            // 项目坐标：accel (X, Z, -Y)，gyro (X, Z, -Y)
            double ax = raw.AccelX / AccelRawPerG;
            double ay = raw.AccelZ / AccelRawPerG;
            double az = -raw.AccelY / AccelRawPerG;
            double wx = raw.GyroX / GyroRawPerDps;
            double wy = raw.GyroZ / GyroRawPerDps;
            double wz = -raw.GyroY / GyroRawPerDps;

            space.Update(ax, ay, az, wx, wy, wz, dt, out double yaw, out double pitch);
            ZeroStep(yaw, pitch, dt, out yaw, out pitch);
            LastYaw = yaw;
            LastPitch = pitch;
            if (!zeroReady)
            {
                outX = 0.0;
                outY = 0.0;
                continue;
            }

            filter.Apply(yaw, pitch, dt, out outX, out outY);
            LastYawOut = outX;
            LastPitchOut = outY;
        }

        if (!zeroReady)
        {
            LastRx = physRx;
            LastRy = physRy;
            return state;
        }

        // 陀螺归一化的上限：1.0 意味着「转到 full_speed_dps 就削平」，
        // 快速甩枪（一两百 dps）会被压成和慢转一样，手感发糊。
        // 留 1.6 倍余量：慢速瞄准不变，快甩还能继续加速。
        // 输出给游戏手柄时后面还会 Clamp 到 ±1，所以不影响原有手柄手感。
        const double GyroOutCeiling = 1.6;
        double full = Math.Max(config.Gyro.EffectiveFullSpeedDps, 1.0);
        double gx = GyroFilter.Clamp(outX / full, -GyroOutCeiling, GyroOutCeiling) * config.Gyro.ScaleYaw;
        double gy = GyroFilter.Clamp(outY / full, -GyroOutCeiling, GyroOutCeiling) * config.Gyro.ScalePitch;

        if (config.Gyro.InvertYaw) gx = -gx;
        if (config.Gyro.InvertPitch) gy = -gy;

        // 链路符号：本项目的 Ry 是 12 位轴值，增大 = 摇杆向下；
        // 而 Python 版（pyvigen）的 y 正 = 向上。为了让 gyro.json
        // 的 invert_pitch 语义与 config.json 完全一致（可直接互拷），
        // 这里把俯仰速度再乘一次链路符号。物理摇杆 passthrough 不经过此符号，
        // 走的是原样读入 → 原样写回，方向不受影响。
        gy *= PitchAxisLinkSign;

        // 压枪侧单独增益：经过上面的符号链后，gy > 0 就是「准星向下」的方向。
        // 只放大这一侧，抬头手感不变。
        double pitchDownGain = config.Gyro.ScalePitchDown;
        if (gy > 0.0 && double.IsFinite(pitchDownGain) && pitchDownGain > 0.0 && pitchDownGain != 1.0)
            gy *= pitchDownGain;

        double rx = gx;
        double ry = gy;
        // 键鼠模式需要「纯陀螺」分量（不含物理右摇杆），单独暴露出来。
        LastGyroUnitX = gx;
        LastGyroUnitY = gy;
        if (config.Gyro.StickPassthrough)
        {
            rx = GyroFilter.Clamp(physRx + gx, -1.0, 1.0);
            ry = GyroFilter.Clamp(physRy + gy, -1.0, 1.0);
        }

        double dz = config.Gyro.RightStickDeadzone;
        double anti = config.Gyro.AntiDeadzone;
        rx = ApplyDeadzone(rx, dz, anti);
        ry = ApplyDeadzone(ry, dz, anti);

        LastRx = rx;
        LastRy = ry;
        return WriteSticks(state, rx, ry);
    }

    private GamepadState WriteSticks(GamepadState state, double rx, double ry)
    {
        GamepadState result = state.Clone();
        result.Rx = UnitToAxis(rx);
        result.Ry = UnitToAxis(ry);
        return result;
    }

    private void ZeroStep(double yaw, double pitch, double dt,
        out double outYaw, out double outPitch)
    {
        if (!zeroReady)
        {
            learnCount++;
            learnSec += dt;
            learnTotalSec += dt;
            sumYaw += yaw;
            sumPitch += pitch;
            sumYaw2 += yaw * yaw;
            sumPitch2 += pitch * pitch;

            if (learnSec >= Math.Max(config.Gyro.ZeroLearnSec, 0.2) && learnCount >= 20)
            {
                double n = learnCount;
                double my = sumYaw / n;
                double mp = sumPitch / n;
                double sdy = Math.Sqrt(Math.Max(sumYaw2 / n - my * my, 0.0));
                double sdp = Math.Sqrt(Math.Max(sumPitch2 / n - mp * mp, 0.0));
                if (sdy > 2.0 || sdp > 2.0)
                {
                    // 采样期手柄在动，这组数据不能当零点，重采
                    learnCount = 0;
                    learnSec = 0;
                    sumYaw = sumPitch = sumYaw2 = sumPitch2 = 0;
                }
                else
                {
                    biasYaw = my;
                    biasPitch = mp;
                    zeroReady = true;
                    BiasSource = "auto_still";
                    Pro2GyroBiasStore.SaveYawPitch(biasYaw, biasPitch, BiasSource, force: true);
                }
            }

            // 兜底：一直没等到「完全静止」也要能用。手柄从开箱就在动是常态，
            // 卡在「学习中」会让陀螺输出恒为 0，表现就是「陀螺仪没反应」。
            if (!zeroReady && learnCount >= 20 &&
                learnTotalSec >= Math.Max(config.Gyro.ZeroLearnSec * 2.0, 2.5))
            {
                biasYaw = sumYaw / learnCount;
                biasPitch = sumPitch / learnCount;
                zeroReady = true;
                RoughZero = true;
                BiasSource = "auto_fallback";
                Pro2GyroBiasStore.SaveYawPitch(biasYaw, biasPitch, BiasSource, force: true);
            }

            outYaw = 0.0;
            outPitch = 0.0;
            return;
        }

        TrackBiasWhileStill(yaw, pitch, dt);

        outYaw = yaw - biasYaw;
        outPitch = pitch - biasPitch;
    }

    /// <summary>
    /// 只要「最近 24 帧的角速度几乎不变」就认为手柄没在动，用 0.6 秒的
    /// 时间常数把零偏慢慢拉向当前值。这样：
    ///   * 开机时零偏学偏了（转动中被当成静止），静止 1~2 秒就能自己纠回来；
    ///   * 真正在瞄准时角速度一直在变，极差远超阈值，零偏完全冻结，不吃输入。
    /// </summary>
    private void TrackBiasWhileStill(double yaw, double pitch, double dt)
    {
        if (!zeroReady || dt <= 0) return;

        stillYaw[stillIndex] = yaw;
        stillPitch[stillIndex] = pitch;
        stillIndex = (stillIndex + 1) % StillFrames;
        if (stillCount < StillFrames) stillCount++;
        if (stillCount < StillFrames) return;

        double minY = double.MaxValue, maxY = double.MinValue;
        double minP = double.MaxValue, maxP = double.MinValue;
        for (int i = 0; i < StillFrames; i++)
        {
            double v = stillYaw[i];
            if (v < minY) minY = v;
            if (v > maxY) maxY = v;
            double q = stillPitch[i];
            if (q < minP) minP = q;
            if (q > maxP) maxP = q;
        }

        if (maxY - minY > StillSpreadDps || maxP - minP > StillSpreadDps) return;

        double a = 1.0 - Math.Exp(-dt / BiasTrackTauSec);
        biasYaw += (yaw - biasYaw) * a;
        biasPitch += (pitch - biasPitch) * a;
        // 继续缓慢纠偏的结果也要落盘，下次连接直接用最新版。
        // 写入带 3 秒节流，避免每帧都碰配置文件。
        Pro2GyroBiasStore.SaveYawPitch(biasYaw, biasPitch, BiasSource);
    }

    private static double ApplyDeadzone(double v, double dz, double anti)
    {
        double a = Math.Abs(v);
        if (a < 1e-6) return 0.0;
        if (a < dz) return 0.0;
        double output = (a - dz) / Math.Max(1.0 - dz, 1e-6);
        if (anti > 0.0) output = anti + output * (1.0 - anti);
        return Math.CopySign(GyroFilter.Clamp(output, 0.0, 1.0), v);
    }

    private static double AxisToUnit(ushort value)
    {
        double centered = value - AxisCenter;
        return centered >= 0 ? centered / AxisSpanPos : centered / AxisSpanNeg;
    }

    private static ushort UnitToAxis(double unit)
    {
        unit = GyroFilter.Clamp(unit, -1.0, 1.0);
        double raw = unit >= 0 ? AxisCenter + unit * AxisSpanPos : AxisCenter + unit * AxisSpanNeg;
        int rounded = (int)Math.Round(raw);
        if (rounded < 0) rounded = 0;
        if (rounded > GamepadState.AxisMax) rounded = GamepadState.AxisMax;
        return (ushort)rounded;
    }

    /// 按配置的按键名判断这一帧是不是「陀螺开关」键
    public bool IsTogglePressed(GamepadState state)
    {
        string key = config.Gyro.GyroToggleButton ?? "none";
        return key.ToLowerInvariant() switch
        {
            "c" or "aux" => state.IsPressed(GamepadButtons.Aux),
            "gr" or "paddle_right" => state.IsPressed(GamepadButtons.PaddleRight),
            "gl" or "paddle_left" => state.IsPressed(GamepadButtons.PaddleLeft),
            "l3" => state.IsPressed(GamepadButtons.LeftStick),
            "r3" => state.IsPressed(GamepadButtons.RightStick),
            "minus" or "back" => state.IsPressed(GamepadButtons.Back),
            "plus" or "start" => state.IsPressed(GamepadButtons.Start),
            _ => false
        };
    }
}
