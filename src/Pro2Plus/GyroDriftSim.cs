using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Pro2Plus;

/// <summary>
/// 陀螺→右摇杆的离线仿真：用合成 IMU 输入复现「小角度左右抖动偏一侧 / 停下不回中」。
/// 只跑算法，不碰设备。跑法：PRO2_GYRO_SIM=1 Pro2Plus.exe
/// </summary>
internal static class GyroDriftSim
{
    private const double Dt = 1.0 / 133.0;
    private const double GyroRawPerDps = 16.384;
    private const double AccelRawPerG = 4096.0;

    private sealed record Scenario(
        string Name,
        double Seconds,
        Func<double, double> YawDps,
        double NoiseSd,
        int Seed,
        double? StopAtSec = null,
        int PhysRxLsb = 0);

    public static string Run()
    {
        var log = new StringBuilder();
        GyroConfig cfg = GyroConfig.LoadOrDefault(out string note);
        log.AppendLine("[GYRO_SIM] " + note);
        log.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"[GYRO_SIM] params full_speed={cfg.Gyro.FullSpeedDps} scale_yaw={cfg.Gyro.ScaleYaw} " +
            $"dz={cfg.Gyro.RightStickDeadzone} anti={cfg.Gyro.AntiDeadzone} " +
            $"invert_yaw={cfg.Gyro.InvertYaw} passthrough={cfg.Gyro.StickPassthrough} " +
            $"driftThr={cfg.Tune.DriftThreshold} driftHold={cfg.Tune.DriftHoldTimeSec} " +
            $"signChange={cfg.Tune.SignChangeThr} smoothThr={cfg.Tune.SmoothThreshold}"));

        var scenarios = new List<Scenario>
        {
            new("still_sd0.5", 5.0, _ => 0.0, 0.5, 1),
            new("still_sd2.0", 5.0, _ => 0.0, 2.0, 2),
            new("sym_4dps", 5.0, t => 4.0 * Math.Sin(2 * Math.PI * 1.5 * t), 0.5, 3),
            new("sym_8dps", 5.0, t => 8.0 * Math.Sin(2 * Math.PI * 1.5 * t), 0.5, 4),
            new("asym_left8_right4", 5.0, t => -2.0 + 6.0 * Math.Sin(2 * Math.PI * 1.5 * t), 0.5, 5),
            new("bias_1.5dps", 5.0, _ => 1.5, 0.5, 6),
            new("stop_after_shake", 6.0, t => t < 2.0 ? 8.0 * Math.Sin(2 * Math.PI * 1.5 * t) : 0.0, 0.5, 7, 2.0),
            new("drift_then_rest", 6.0, t => t < 2.0 ? 3.0 : 0.0, 0.5, 8, 2.0),
            new("hold_-3dps", 6.0, _ => -3.0, 0.3, 11),
            new("hold_-1.2dps", 6.0, _ => -1.2, 0.3, 12),
            new("hold_then_reverse", 6.0, t => t < 3.0 ? -3.0 : 3.0, 0.3, 13),
            new("sym8_phys+40lsb", 5.0, t => 8.0 * Math.Sin(2 * Math.PI * 1.5 * t), 0.5, 9, null, 40),
            new("sym8_phys-40lsb", 5.0, t => 8.0 * Math.Sin(2 * Math.PI * 1.5 * t), 0.5, 10, null, -40),
        };

        foreach (Scenario s in scenarios)
        {
            log.AppendLine(RunScenario(cfg, s));
        }

        // 诊断工具：输出的是现场数据，不参与任何门槛判定。
        log.AppendLine("summary=DIAG");
        return log.ToString();
    }

    private static string RunScenario(GyroConfig cfg, Scenario s)
    {
        var gyro = new GyroStick(cfg);
        var random = new Random(s.Seed);
        long step = (long)Math.Round(Dt * Stopwatch.Frequency);
        long ticks = Stopwatch.GetTimestamp();

        int frames = (int)Math.Round(s.Seconds / Dt);
        var sx = new List<double>(frames);
        var rawYaw = new List<double>(frames);

        for (int i = 0; i < frames; i++)
        {
            double t = i * Dt;
            double clean = s.YawDps(t);
            double noise = Gaussian(random) * s.NoiseSd;
            double yaw = clean + noise;
            rawYaw.Add(yaw);

            GamepadState state = GamepadState.Neutral();
            state.Updates = (uint)(i + 1);
            state.Rx = (ushort)(GamepadState.AxisCenter + s.PhysRxLsb);
            state.SwitchRawImuSamples =
            [
                new SwitchImuRawSample(
                    0, 0, (short)AccelRawPerG,
                    0, 0, (short)Math.Round(yaw * GyroRawPerDps),
                    0, 0, ticks, (ulong)i)
            ];

            ticks += step;
            GamepadState result = gyro.Apply(state, ticks, false);
            double unit = (result.Rx - GamepadState.AxisCenter) /
                (double)(GamepadState.AxisMax - GamepadState.AxisCenter);
            sx.Add(unit);
        }

        // 学习结束后的窗口才算有效数据
        int warm = (int)Math.Round((cfg.Gyro.ZeroLearnSec * 2.0 + 0.3) / Dt);
        warm = Math.Min(warm, sx.Count / 2);
        var tail = sx.GetRange(warm, sx.Count - warm);

        int nonZero = 0, pos = 0, neg = 0;
        double sum = 0, maxAbs = 0, maxJump = 0;
        double prev = tail.Count > 0 ? tail[0] : 0;
        bool firstSample = true;
        foreach (double v in tail)
        {
            sum += v;
            if (Math.Abs(v) > 1e-9) nonZero++;
            if (v > 0) pos++;
            if (v < 0) neg++;
            maxAbs = Math.Max(maxAbs, Math.Abs(v));
            if (!firstSample) maxJump = Math.Max(maxJump, Math.Abs(v - prev));
            firstSample = false;
            prev = v;
        }

        double mean = tail.Count > 0 ? sum / tail.Count : 0;
        double posMid = pos + neg > 0 ? (double)pos / (pos + neg) : 0;

        // 停车后的回中帧数（输出落到 ±1 LSB 内）
        int settleFrames = -1;
        if (s.StopAtSec is double stop)
        {
            int stopIndex = (int)Math.Round(stop / Dt);
            bool settled = true;
            for (int i = sx.Count - 1; i >= stopIndex; i--)
            {
                if (Math.Abs(sx[i]) > 1.0 / 2047.0) { settleFrames = i - stopIndex + 1; settled = false; break; }
            }
            if (settled) settleFrames = 0;
        }

        string bias = string.Create(CultureInfo.InvariantCulture,
            $"bias={gyro.BiasYawDeg:F2}dps src={gyro.BiasSource} rough={gyro.RoughZero} ready={gyro.ZeroReady}");
        int half = tail.Count / 2;
        double h1 = 0, h2 = 0;
        for (int i = 0; i < half; i++) h1 += tail[i];
        for (int i = half; i < tail.Count; i++) h2 += tail[i];
        h1 = half > 0 ? h1 / half : 0;
        h2 = tail.Count - half > 0 ? h2 / (tail.Count - half) : 0;
        double nonZeroPct = 100.0 * nonZero / Math.Max(tail.Count, 1);
        double posPct = 100.0 * posMid;
        string meanText = mean.ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture);
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{s.Name,-20} n={tail.Count,4} mean={meanText} h1={h1:+0.000;-0.000} h2={h2:+0.000;-0.000} " +
            $"nonzero={nonZeroPct,5:F1}% " +
            $"pos={posPct,5:F1}% maxAbs={maxAbs:F3} maxJump={maxJump:F3} " +
            $"settle={settleFrames,4} {bias}");
        return line;
    }

    private static double Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
