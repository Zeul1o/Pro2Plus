using System;

namespace Pro2Plus;

/// <summary>
/// 连接层（Pro2InputSession）统一应用的校准。
///
/// 摇杆：把保存的零位平移到轴中心（2048），再把静止残余吸附掉。
///       没有这一步时，零位档案只是个摆设——旧实现的重映射是恒等运算，
///       2047 进来还是 2047 出去，漂移原样送到游戏和鼠标。
/// 陀螺：减去保存的 IMU 原始三轴零偏，再乘当前输出模式的原生陀螺仪灵敏度。
///       PS5 / Pro1 原生体感直接读 GamepadState.GyroX/Y/Z，走这里才生效。
/// </summary>
public static class Pro2RuntimeCalibration
{
    private const int CenterSpreadLimit = 48;      // 零位采样允许的抖动
    private const int MinimumRangeSpan = 700;      // 行程采样至少要有这么大的行程

    public static void Apply(GamepadState state, double gyroSensitivity)
    {
        Pro2GyroBiasProfile gyro = Pro2GyroBiasStore.Load();
        if (state.GyroValid)
        {
            // 有线解析层已经扣过零偏（GyroBiasApplied），这里再扣就是双倍。
            if (gyro.RawCalibrated && !state.GyroBiasApplied)
            {
                state.GyroX = ClampShort(state.GyroX - gyro.RawX);
                state.GyroY = ClampShort(state.GyroY - gyro.RawY);
                state.GyroZ = ClampShort(state.GyroZ - gyro.RawZ);
            }
            // 原生陀螺仪映射灵敏度：Pro1 / PS5 出包直接读这三个轴。
            if (double.IsFinite(gyroSensitivity) && Math.Abs(gyroSensitivity - 1.0) > 1e-6)
            {
                state.GyroX = ClampShort(state.GyroX * gyroSensitivity);
                state.GyroY = ClampShort(state.GyroY * gyroSensitivity);
                state.GyroZ = ClampShort(state.GyroZ * gyroSensitivity);
            }
        }

        // 摇杆归零不在这里做：归一化（含 ±64 中心死区）已经下沉到两个解析器，
        // 有线和蓝牙共用 StickAxisNormalizer，输出端再改一次就会二次纠正、反而把中心推歪。
    }

    private static short ClampShort(double value) =>
        (short)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);

    /// <summary>
    /// 蓝牙链路的 IMU 原始零偏学习：有线由 Pro2WiredProtocol 自己学，
    /// 蓝牙没有这一步，原生体感会一直漂。静止时按原始三轴均值落盘，
    /// 之后 Apply 会把同一个档案减回去。
    /// </summary>
    public sealed class GyroRawBiasLearner
    {
        private const int Window = 48;
        private const double SpreadLimit = 90.0;
        private const int RequiredStillFrames = 200;

        private readonly short[] ringX = new short[Window];
        private readonly short[] ringY = new short[Window];
        private readonly short[] ringZ = new short[Window];
        private int index, count, stillFrames, summed;
        private double sumX, sumY, sumZ;

        public bool Completed { get; private set; }

        public void Add(GamepadState state)
        {
            if (Completed || !state.GyroValid) return;
            if (state.Buttons != GamepadButtons.None)
            {
                ResetStill();
                return;
            }

            ringX[index] = state.GyroX;
            ringY[index] = state.GyroY;
            ringZ[index] = state.GyroZ;
            index = (index + 1) % Window;
            if (count < Window) count++;
            if (count < Window) return;

            if (Spread(ringX) > SpreadLimit || Spread(ringY) > SpreadLimit || Spread(ringZ) > SpreadLimit)
            {
                ResetStill();
                return;
            }

            stillFrames++;
            summed++;
            sumX += state.GyroX;
            sumY += state.GyroY;
            sumZ += state.GyroZ;
            if (stillFrames < RequiredStillFrames || summed < RequiredStillFrames) return;

            Pro2GyroBiasStore.SaveRaw(sumX / summed, sumY / summed, sumZ / summed,
                "auto_still_ble", force: true);
            Completed = true;
        }

        private static double Spread(short[] values)
        {
            short min = short.MaxValue, max = short.MinValue;
            foreach (short value in values)
            {
                if (value < min) min = value;
                if (value > max) max = value;
            }
            return max - (double)min;
        }

        private void ResetStill()
        {
            stillFrames = 0;
            summed = 0;
            sumX = sumY = sumZ = 0;
        }
    }

    /// <summary>从实时帧采集摇杆零位或完整行程。</summary>
    public sealed class StickSampleAccumulator
    {
        private readonly bool rangeMode;
        private readonly object gate = new();
        private long count;
        private long sumLx, sumLy, sumRx, sumRy;
        private ushort maxSeenLx, maxSeenLy, maxSeenRx, maxSeenRy;
        private ushort minSeenLx = GamepadState.AxisMax, minSeenLy = GamepadState.AxisMax;
        private ushort minSeenRx = GamepadState.AxisMax, minSeenRy = GamepadState.AxisMax;

        public StickSampleAccumulator(bool rangeMode) => this.rangeMode = rangeMode;

        public long Count => count;
        public bool RangeMode => rangeMode;

        public void Add(GamepadState state)
        {
            lock (gate)
            {
                count++;
                sumLx += state.Lx; sumLy += state.Ly;
                sumRx += state.Rx; sumRy += state.Ry;
                minSeenLx = Math.Min(minSeenLx, state.Lx); maxSeenLx = Math.Max(maxSeenLx, state.Lx);
                minSeenLy = Math.Min(minSeenLy, state.Ly); maxSeenLy = Math.Max(maxSeenLy, state.Ly);
                minSeenRx = Math.Min(minSeenRx, state.Rx); maxSeenRx = Math.Max(maxSeenRx, state.Rx);
                minSeenRy = Math.Min(minSeenRy, state.Ry); maxSeenRy = Math.Max(maxSeenRy, state.Ry);
            }
        }

        /// <summary>单轴覆盖度：行程跨度相对 MinimumRangeSpan 的比例，封顶 1。</summary>
        private static double Axis(int span) =>
            span <= 0 ? 0 : Math.Min(1.0, span / (double)MinimumRangeSpan);

        /// <summary>
        /// 左摇杆的到位程度（0–1）：取 X/Y 两轴的**较小值**。
        /// 必须转圈把两根轴都走满才算完成——只左右推不算，和最终判定「四轴行程都要够」一致。
        /// </summary>
        public double LeftCoverage
        {
            get
            {
                if (!rangeMode) return 0;
                lock (gate) return Math.Min(Axis(maxSeenLx - minSeenLx), Axis(maxSeenLy - minSeenLy));
            }
        }

        /// <summary>右摇杆的到位程度（0–1），规则同左摇杆。</summary>
        public double RightCoverage
        {
            get
            {
                if (!rangeMode) return 0;
                lock (gate) return Math.Min(Axis(maxSeenRx - minSeenRx), Axis(maxSeenRy - minSeenRy));
            }
        }

        /// <summary>
        /// 行程采样完成度（0–1）= 两根摇杆到位程度的平均。
        /// 一根摇杆转一圈只该算一半的活，另半在右摇杆。
        /// </summary>
        public double RangeCoverage => rangeMode ? (LeftCoverage + RightCoverage) / 2.0 : 0;

        public StickCalibrationResult Complete(
            StickCalibrationProfile? existing,
            string deviceKey)
        {
            if (count < 20)
            {
                return new StickCalibrationResult(false,
                    "采样帧数不足（" + count + "），请在手柄持续输出时重试。", null);
            }

            StickCalibrationProfile baseProfile = existing ?? new StickCalibrationProfile();
            if (!rangeMode)
            {
                ushort cx = Mean(sumLx), cy = Mean(sumLy), crx = Mean(sumRx), cry = Mean(sumRy);
                // 采样期间手柄在动就不写档案，避免把偏移当成零位。
                if (maxSeenLx - minSeenLx > CenterSpreadLimit ||
                    maxSeenLy - minSeenLy > CenterSpreadLimit ||
                    maxSeenRx - minSeenRx > CenterSpreadLimit ||
                    maxSeenRy - minSeenRy > CenterSpreadLimit)
                {
                    return new StickCalibrationResult(false,
                        "采样期间摇杆有移动，零位未保存；请完全松开后重试。", null);
                }

                return new StickCalibrationResult(true,
                    "零位采样完成，中心=(" + cx + "," + cy + "," + crx + "," + cry + ")。",
                    baseProfile with
                    {
                        CenterCalibrated = true,
                        CenterLx = cx, CenterLy = cy, CenterRx = crx, CenterRy = cry
                    });
            }

            if (!baseProfile.CenterCalibrated)
            {
                return new StickCalibrationResult(false, "请先完成摇杆零位校准。", null);
            }

            int spanLx = maxSeenLx - minSeenLx, spanLy = maxSeenLy - minSeenLy;
            int spanRx = maxSeenRx - minSeenRx, spanRy = maxSeenRy - minSeenRy;
            if (spanLx < MinimumRangeSpan || spanLy < MinimumRangeSpan ||
                spanRx < MinimumRangeSpan || spanRy < MinimumRangeSpan)
            {
                return new StickCalibrationResult(false,
                    "行程采样不足（" + spanLx + "," + spanLy + "," + spanRx + "," + spanRy +
                    "），请把每个摇杆沿外圈推到底。", null);
            }

            return new StickCalibrationResult(true,
                "完整行程采样完成，范围=(" + minSeenLx + ".." + maxSeenLx + " 等)。",
                baseProfile with
                {
                    RangeCalibrated = true,
                    MinLx = minSeenLx, MaxLx = maxSeenLx,
                    MinLy = minSeenLy, MaxLy = maxSeenLy,
                    MinRx = minSeenRx, MaxRx = maxSeenRx,
                    MinRy = minSeenRy, MaxRy = maxSeenRy
                });
        }

        private ushort Mean(long sum) =>
            (ushort)Math.Clamp(Math.Round(sum / (double)Math.Max(1, count), MidpointRounding.AwayFromZero),
                0, GamepadState.AxisMax);
    }
}
