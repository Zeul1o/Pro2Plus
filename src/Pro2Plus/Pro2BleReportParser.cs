using System;
using System.Buffers.Binary;
using System.Diagnostics;

namespace Pro2Plus;

/// <summary>
/// 蓝牙 FD2 载荷与 Switch 系 HID 报告的解析器（本项目自研实现）。
///
/// 协议事实来自公开记录与实机抓包（SDL3 的 <c>SDL_hidapi_switch2.c</c> 说明了
/// 0x05/FD2 的字段位置，速率与电量档位则是本机实测校准的），实现上不沿用任何
/// 第三方解析代码：
///   * 按键用「掩码 → 语义键」表驱动，一份报告类型一张表；
///   * 摇杆固定 12 位解包，交给有线和蓝牙共用的 <see cref="StickAxisNormalizer"/> 归一化；
///   * IMU 只做零偏估计，交给 <see cref="GyroBiasLearner"/> 或本地估计器。
///
/// 线程模型：所有公开入口内部加锁，调用方可以在采集线程和 UI 线程同时访问。
/// </summary>
public sealed class Pro2BleReportParser
{
    /// <summary>FD2 按键字第 3 字节 bit4：耳机在位，不是保留位。</summary>
    public const uint Fd2HeadsetPresentMask = 0x10000000u;

    /// <summary>FD2 完整载荷的最小长度（短于此值只当前半段解析）。</summary>
    private const int Fd2MinimumPayload = 60;

    private const ushort FactoryCenter = GamepadState.AxisCenter;

    private readonly object sync = new();
    private readonly StickAxes primaryAxes = new();
    private readonly StickAxes legacyAxes = new();
    private readonly GyroBiasEstimator fallbackGyro = new();
    private readonly GyroBiasLearner? qualityGyro;

    public Pro2BleReportParser() : this(QualityFeatureFlags.GyroEnabled) { }

    public Pro2BleReportParser(bool gyroQualityMode)
    {
        qualityGyro = gyroQualityMode ? new GyroBiasLearner() : null;
    }

    // ── 诊断 / 校准对外接口 ──────────────────────────────────────────────

    public string StickTelemetry
    {
        get { lock (sync) { return primaryAxes.Telemetry; } }
    }

    public string StickCalibrationSummary
    {
        get { lock (sync) { return primaryAxes.Summary; } }
    }

    public StickCalibrationProfile StickCalibrationProfile
    {
        get { lock (sync) { return primaryAxes.Profile; } }
    }

    public PhysicalStickAxes LastPhysicalStickAxes
    {
        get { lock (sync) { return primaryAxes.LastRaw; } }
    }

    public bool IsStickCalibrationCaptureActive
    {
        get { lock (sync) { return primaryAxes.CaptureActive; } }
    }

    public string StartManualGyroCalibration()
    {
        lock (sync)
        {
            return qualityGyro?.StartManualCalibration() ?? fallbackGyro.StartManualCalibration();
        }
    }

    public string GyroCalibrationSummary
    {
        get { lock (sync) { return qualityGyro?.Summary ?? fallbackGyro.Summary; } }
    }

    public string SetStickCalibration(StickCalibrationProfile? profile)
    {
        lock (sync)
        {
            primaryAxes.ApplyProfile(profile);
            legacyAxes.ApplyProfile(profile);
            return primaryAxes.Summary;
        }
    }

    public string StartManualStickCenterCalibration()
    {
        lock (sync) { return primaryAxes.BeginCenterCapture(); }
    }

    public StickCalibrationResult CompleteManualStickCenterCalibration()
    {
        lock (sync)
        {
            StickCalibrationResult result = primaryAxes.FinishCenterCapture();
            if (result.Success && result.Profile != null)
            {
                legacyAxes.ApplyProfile(result.Profile);
            }

            return result;
        }
    }

    public string StartManualStickRangeCalibration()
    {
        lock (sync) { return primaryAxes.BeginRangeCapture(); }
    }

    public StickCalibrationResult CompleteManualStickRangeCalibration()
    {
        lock (sync)
        {
            StickCalibrationResult result = primaryAxes.FinishRangeCapture();
            if (result.Success && result.Profile != null)
            {
                legacyAxes.ApplyProfile(result.Profile);
            }

            return result;
        }
    }

    // ── 解析入口 ────────────────────────────────────────────────────────

    /// <summary>通用入口：HID 输入报告 → FD2 载荷 → 旧版载荷，依次尝试。</summary>
    public bool TryParse(ReadOnlySpan<byte> report, out GamepadState state, out string source)
    {
        lock (sync)
        {
            if (TryParseHidInputReportCore(report, out state, out source))
            {
                return true;
            }

            if (TryParseFd2Core(report, out state, out source))
            {
                return true;
            }

            if (TryParseLegacyCore(report, out state, out source))
            {
                return true;
            }

            state = GamepadState.Neutral();
            source = string.Empty;
            return false;
        }
    }

    public bool TryParseFd2Payload(ReadOnlySpan<byte> report, out GamepadState state, out string source)
    {
        lock (sync) { return TryParseFd2Core(report, out state, out source); }
    }

    public bool TryParseLegacyPayload(ReadOnlySpan<byte> report, out GamepadState state, out string source)
    {
        lock (sync) { return TryParseLegacyCore(report, out state, out source); }
    }

    public bool TryParseHidInputReport(ReadOnlySpan<byte> report, out GamepadState state, out string source)
    {
        lock (sync) { return TryParseHidInputReportCore(report, out state, out source); }
    }

    /// <summary>Switch Pro 主报告（11 字节起）：按键在 2..4，摇杆在 5 与 8。</summary>
    public bool TryParsePrimaryProPayload(ReadOnlySpan<byte> report, out GamepadState state, out string source)
    {
        lock (sync)
        {
            state = GamepadState.Neutral();
            source = string.Empty;
            if (report.Length < 11)
            {
                return false;
            }

            ApplyButtonTable(state, PrimaryProButtons, report[2], report[3], report[4]);
            if (BatteryLevelReading.TryDecodePrimary(report, out var battery))
            {
                battery.ApplyTo(state);
            }

            ApplySticks(primaryAxes, state, report, 5, 8);
            source = "primary_pro_payload";
            return true;
        }
    }

    private bool TryParseFd2Core(ReadOnlySpan<byte> report, out GamepadState state, out string source)
    {
        state = GamepadState.Neutral();
        source = string.Empty;
        if (report.Length < Fd2MinimumPayload || !LooksLikeFd2(report))
        {
            return false;
        }

        // FD2 的 0..3 是计数器，不是主报告里的电源信息；电压只作遥测，单看电压推不出电量百分比。
        state.BatteryVoltageMillivolts =
            BinaryPrimitives.ReadUInt16LittleEndian(report.Slice(31, 2));
        if (ChargeStatusReading.TryDecodeFd2(report, out var charging))
        {
            charging.ApplyTo(state);
        }

        uint buttons = BinaryPrimitives.ReadUInt32LittleEndian(report.Slice(4, 4));
        state.HeadsetPresenceKnown = true;
        state.HeadsetPresent = (buttons & Fd2HeadsetPresentMask) != 0;
        ApplyFd2Buttons(state, buttons);
        ApplySticks(primaryAxes, state, report, 10, 13);

        state.MotionTimestampUs = BinaryPrimitives.ReadUInt32LittleEndian(report.Slice(42, 4));
        AttachRawImu(state, report, 48, 3);
        ApplyMotion(state, report.Slice(48, 12));
        source = "fd2_payload";
        return true;
    }

    private bool TryParseLegacyCore(ReadOnlySpan<byte> report, out GamepadState state, out string source)
    {
        state = GamepadState.Neutral();
        source = string.Empty;
        if (report.Length < 11 || IsStandardReportId(report[0]))
        {
            return false;
        }

        ApplyButtonTable(state, StandardButtons, report[2], report[3], report[4]);
        ApplySticks(legacyAxes, state, report, 5, 8);
        source = "legacy_payload";
        return true;
    }

    private bool TryParseHidInputReportCore(
        ReadOnlySpan<byte> report,
        out GamepadState state,
        out string source)
    {
        state = GamepadState.Neutral();
        source = string.Empty;
        if (report.Length < 13 || !IsStandardReportId(report[0]))
        {
            return false;
        }

        ApplyButtonTable(state, StandardButtons, report[3], report[4], report[5]);
        ApplySticks(primaryAxes, state, report, 6, 9);

        if (report.Length >= 49)
        {
            AttachRawImu(state, report, 13, 3);
            ApplyMotion(state, report.Slice(37, 12));
        }
        else if (report.Length >= 25)
        {
            AttachRawImu(state, report, 13, 1);
            ApplyMotion(state, report.Slice(13, 12));
        }

        source = "switch_pro_standard";
        return true;
    }

    // ── 按键 ────────────────────────────────────────────────────────────

    /// <summary>标准（主报告 / 旧版）三字节按键表：右、中、左。</summary>
    private static readonly (byte Mask, GamepadButtons Button)[] StandardRightBits =
    {
        (0x01, GamepadButtons.West), (0x02, GamepadButtons.North),
        (0x04, GamepadButtons.South), (0x08, GamepadButtons.East),
        (0x40, GamepadButtons.R1), (0x80, GamepadButtons.R2)
    };

    private static readonly (byte Mask, GamepadButtons Button)[] StandardMiddleBits =
    {
        (0x01, GamepadButtons.Back), (0x02, GamepadButtons.Start),
        (0x04, GamepadButtons.RightStick), (0x08, GamepadButtons.LeftStick),
        (0x10, GamepadButtons.Home), (0x20, GamepadButtons.Capture)
    };

    private static readonly (byte Mask, GamepadButtons Button)[] StandardLeftBits =
    {
        (0x01, GamepadButtons.DPadDown), (0x02, GamepadButtons.DPadUp),
        (0x04, GamepadButtons.DPadRight), (0x08, GamepadButtons.DPadLeft),
        (0x40, GamepadButtons.L1), (0x80, GamepadButtons.L2)
    };

    private static readonly ButtonTable StandardButtons = new(
        StandardRightBits, StandardMiddleBits, StandardLeftBits);

    /// <summary>Switch Pro 主报告三字节：右（2）、左（3）、公共（4）。</summary>
    private static readonly ButtonTable PrimaryProButtons = new(
        new[]
        {
            ((byte)0x01, GamepadButtons.South), ((byte)0x02, GamepadButtons.East),
            ((byte)0x04, GamepadButtons.West), ((byte)0x08, GamepadButtons.North),
            ((byte)0x10, GamepadButtons.R1), ((byte)0x20, GamepadButtons.R2),
            ((byte)0x40, GamepadButtons.Start), ((byte)0x80, GamepadButtons.RightStick)
        },
        new[]
        {
            ((byte)0x01, GamepadButtons.DPadDown), ((byte)0x02, GamepadButtons.DPadRight),
            ((byte)0x04, GamepadButtons.DPadLeft), ((byte)0x08, GamepadButtons.DPadUp),
            ((byte)0x10, GamepadButtons.L1), ((byte)0x20, GamepadButtons.L2),
            ((byte)0x40, GamepadButtons.Back), ((byte)0x80, GamepadButtons.LeftStick)
        },
        new[]
        {
            ((byte)0x01, GamepadButtons.Home), ((byte)0x02, GamepadButtons.Capture),
            ((byte)0x04, GamepadButtons.PaddleRight), ((byte)0x08, GamepadButtons.PaddleLeft),
            ((byte)0x10, GamepadButtons.Aux)
        });

    /// <summary>FD2 按键字：32 位，低位在右，方向键与 L1/L2 在高位。</summary>
    private static readonly (uint Mask, GamepadButtons Button)[] Fd2ButtonBits =
    {
        (0x00000001u, GamepadButtons.West), (0x00000002u, GamepadButtons.North),
        (0x00000004u, GamepadButtons.South), (0x00000008u, GamepadButtons.East),
        (0x00000040u, GamepadButtons.R1), (0x00000080u, GamepadButtons.R2),
        (0x00000100u, GamepadButtons.Back), (0x00000200u, GamepadButtons.Start),
        (0x00000400u, GamepadButtons.RightStick), (0x00000800u, GamepadButtons.LeftStick),
        (0x00001000u, GamepadButtons.Home), (0x00002000u, GamepadButtons.Capture),
        (0x00004000u, GamepadButtons.Aux),
        (0x00010000u, GamepadButtons.DPadDown), (0x00020000u, GamepadButtons.DPadUp),
        (0x00040000u, GamepadButtons.DPadRight), (0x00080000u, GamepadButtons.DPadLeft),
        (0x00400000u, GamepadButtons.L1), (0x00800000u, GamepadButtons.L2),
        (0x01000000u, GamepadButtons.PaddleRight), (0x02000000u, GamepadButtons.PaddleLeft)
    };

    private static void ApplyFd2Buttons(GamepadState state, uint buttons)
    {
        state.Buttons = GamepadButtons.None;
        state.RawButtons = buttons;
        foreach ((uint mask, GamepadButtons button) in Fd2ButtonBits)
        {
            if ((buttons & mask) != 0)
            {
                state.Buttons |= button;
            }
        }

        state.L2 = state.IsPressed(GamepadButtons.L2) ? GamepadState.TriggerMax : (ushort)0;
        state.R2 = state.IsPressed(GamepadButtons.R2) ? GamepadState.TriggerMax : (ushort)0;
    }

    /// <summary>
    /// 三字节按键统一解码。RawButtons 的拼法跟着报告类型走：
    /// 标准/旧版是「左 中 右」，主报告是「公共 左 右」。
    /// </summary>
    private static void ApplyButtonTable(
        GamepadState state,
        ButtonTable table,
        byte right,
        byte middle,
        byte left)
    {
        state.Buttons = GamepadButtons.None;
        state.RawButtons = table.BuildRaw(right, middle, left);

        ApplyBits(state, table.First, right);
        ApplyBits(state, table.Second, middle);
        ApplyBits(state, table.Third, left);

        state.L2 = state.IsPressed(GamepadButtons.L2) ? GamepadState.TriggerMax : (ushort)0;
        state.R2 = state.IsPressed(GamepadButtons.R2) ? GamepadState.TriggerMax : (ushort)0;
    }

    private static void ApplyBits(
        GamepadState state,
        (byte Mask, GamepadButtons Button)[] bits,
        byte value)
    {
        foreach ((byte mask, GamepadButtons button) in bits)
        {
            if ((value & mask) != 0)
            {
                state.Buttons |= button;
            }
        }
    }

    private readonly struct ButtonTable
    {
        public ButtonTable(
            (byte Mask, GamepadButtons Button)[] right,
            (byte Mask, GamepadButtons Button)[] middle,
            (byte Mask, GamepadButtons Button)[] left)
        {
            First = right;
            Second = middle;
            Third = left;
        }

        public (byte Mask, GamepadButtons Button)[] First { get; }
        public (byte Mask, GamepadButtons Button)[] Second { get; }
        public (byte Mask, GamepadButtons Button)[] Third { get; }

        /// <summary>三字节原始值：右在低 8 位、公共在中、左在高，和主报告字段顺序一致。</summary>
        public uint BuildRaw(byte right, byte middle, byte left) =>
            ((uint)left << 16) | ((uint)middle << 8) | right;
    }

    // ── 摇杆 ────────────────────────────────────────────────────────────

    /// <summary>两组 12 位摇杆打包：第一个在 <paramref name="firstOffset"/>，第二个紧随其后。</summary>
    private static void ApplySticks(
        StickAxes calibration,
        GamepadState state,
        ReadOnlySpan<byte> data,
        int firstOffset,
        int secondOffset)
    {
        ushort lx = UnpackX(data, firstOffset);
        ushort ly = UnpackY(data, firstOffset);
        ushort rx = UnpackX(data, secondOffset);
        ushort ry = UnpackY(data, secondOffset);

        calibration.Observe(lx, ly, rx, ry);
        calibration.LearnCenterWhileIdle(state.Buttons, lx, ly, rx, ry);
        state.Lx = calibration.ToDevice(lx, StickAxes.Axis.LeftX);
        state.Ly = calibration.ToDevice(ly, StickAxes.Axis.LeftY);
        state.Rx = calibration.ToDevice(rx, StickAxes.Axis.RightX);
        state.Ry = calibration.ToDevice(ry, StickAxes.Axis.RightY);
    }

    private static ushort UnpackX(ReadOnlySpan<byte> data, int offset)
    {
        int value = data[offset] | ((data[offset + 1] & 0x0F) << 8);
        return (ushort)Math.Clamp(value, 0, GamepadState.AxisMax);
    }

    private static ushort UnpackY(ReadOnlySpan<byte> data, int offset)
    {
        int value = ((data[offset + 1] >> 4) & 0x0F) | (data[offset + 2] << 4);
        return (ushort)Math.Clamp(value, 0, GamepadState.AxisMax);
    }

    // ── 体感 ────────────────────────────────────────────────────────────

    private void ApplyMotion(GamepadState state, ReadOnlySpan<byte> sample)
    {
        if (sample.Length < 12)
        {
            return;
        }

        state.AccelValid = true;
        state.GyroValid = true;
        state.AccelX = BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(0, 2));
        state.AccelY = BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(2, 2));
        state.AccelZ = BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(4, 2));
        state.GyroX = BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(6, 2));
        state.GyroY = BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(8, 2));
        state.GyroZ = BinaryPrimitives.ReadInt16LittleEndian(sample.Slice(10, 2));

        if (qualityGyro != null)
        {
            qualityGyro.Apply(state, Stopwatch.GetTimestamp());
        }
        else
        {
            fallbackGyro.Apply(state);
        }
    }

    private static void AttachRawImu(
        GamepadState state,
        ReadOnlySpan<byte> payload,
        int offset,
        int maxSamples)
    {
        SwitchImuRawBlock block = ProfessionalImuConverter.DecodeSwitchImuSamples(payload, offset, maxSamples);
        state.SwitchRawImuSamples = block.Samples;
        state.SwitchRawImuOffset = block.Offset;
        state.SwitchRawImuBytesHex = block.RawBytesHex;
    }

    // ── 载荷识别 ────────────────────────────────────────────────────────

    private static bool IsStandardReportId(byte id) =>
        id == 0x30 || id == 0x31 || id == 0x21 || id == 0x23;

    /// <summary>
    /// FD2 的形状检查：按键字里只有已知位可以为 1。
    /// 0xEC300020 覆盖了未使用的保留位与厂商位，出现即说明这不是 FD2 载荷。
    /// </summary>
    private static bool LooksLikeFd2(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            return false;
        }

        uint buttons = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        return (buttons & 0xEC300020u) == 0;
    }

    /// <summary>
    /// 摇杆零位跟踪 + 手动校准。四轴用数组统一处理：中心、行程、小数进位、
    /// 静止跟踪各一条数组，避免四份重复代码。
    /// </summary>
    private sealed class StickAxes
    {
        public enum Axis
        {
            LeftX = 0,
            LeftY = 1,
            RightX = 2,
            RightY = 3
        }

        private const int AxisCount = 4;
        private const ushort CenterLearnTolerance = 256;
        private const int DefaultDirectionalRange = 1600;
        private const int CenterSamplesNeeded = 20;
        private const int CaptureCenterSamples = 16;
        private const int CaptureCenterMaxSpread = 96;
        private const int CaptureRangeSamples = 60;
        private const int CaptureMinimumTravel = 900;
        private const double EndpointReserve = 0.97;
        private const int ManualCenterWindow = 512;

        // 零位跟踪：只有「离当前中心 12 个单位以内」才算静止，时间常数 1.2 秒。
        // 用真实时间而不是帧数，蓝牙 19~133Hz 的抖动才不会让收敛时间差十几倍。
        private const double QuietWindow = 12;
        private const double TrackTauSeconds = 1.2;

        private readonly long[] rawSum = new long[AxisCount];
        private readonly ushort[] centers = { FactoryCenter, FactoryCenter, FactoryCenter, FactoryCenter };
        private readonly double[] trackedCenter = new double[AxisCount];
        private readonly double[] carry = new double[AxisCount];
        private readonly long[] lastTick = new long[AxisCount];
        private readonly long[] outSum = new long[AxisCount];
        private readonly int[] outCount = new int[AxisCount];
        private readonly ushort[] outMin = new ushort[AxisCount];
        private readonly ushort[] outMax = new ushort[AxisCount];
        private readonly ushort[] lastRaw = new ushort[AxisCount];
        private readonly ushort[] lastOut = new ushort[AxisCount];

        private StickCalibrationProfile profile = new();
        private AxisCapture? capture;
        private int learnSamples;
        private bool tracked;

        public PhysicalStickAxes LastRaw { get; private set; } =
            new(FactoryCenter, FactoryCenter, FactoryCenter, FactoryCenter);

        public StickCalibrationProfile Profile => profile;

        public bool CaptureActive => capture != null;

        public string Summary =>
            profile.Summary +
            (capture == null
                ? string.Empty
                : " capture=" + capture.ModeName + " samples=" + capture.Samples);

        public string Telemetry =>
            "raw=(" + lastRaw[0] + "," + lastRaw[1] + "," + lastRaw[2] + "," + lastRaw[3] + ")" +
            " center=(" + F(trackedCenter[0]) + "," + F(trackedCenter[1]) + "," +
            F(trackedCenter[2]) + "," + F(trackedCenter[3]) + ")" +
            " out=(" + lastOut[0] + "," + lastOut[1] + "," + lastOut[2] + "," + lastOut[3] + ")" +
            " mean=(" + Mean(0) + "," + Mean(1) + "," + Mean(2) + "," + Mean(3) + ")" +
            " range=(" + Span(0) + "," + Span(1) + "," + Span(2) + "," + Span(3) + ")" +
            " frames=" + outCount[0];

        public void ApplyProfile(StickCalibrationProfile? saved)
        {
            StickCalibrationProfile normalized = StickCalibrationProfile.Normalize(saved);
            if (profile == normalized)
            {
                return;
            }

            profile = normalized;
            centers[0] = profile.CenterLx;
            centers[1] = profile.CenterLy;
            centers[2] = profile.CenterRx;
            centers[3] = profile.CenterRy;
            capture = null;
            ResetLearning();
            SyncTracked();
        }

        public void Observe(ushort lx, ushort ly, ushort rx, ushort ry)
        {
            LastRaw = new PhysicalStickAxes(lx, ly, rx, ry);
            capture?.Add(lx, ly, rx, ry);
        }

        /// <summary>未做过中心校准时，静止报文的均值就是这台手柄的真实零位。</summary>
        public void LearnCenterWhileIdle(
            GamepadButtons buttons,
            ushort lx,
            ushort ly,
            ushort rx,
            ushort ry)
        {
            if (profile.CenterCalibrated || capture != null)
            {
                return;
            }

            if (buttons != GamepadButtons.None ||
                !NearFactory(lx) || !NearFactory(ly) || !NearFactory(rx) || !NearFactory(ry))
            {
                ResetLearning();
                return;
            }

            rawSum[0] += lx;
            rawSum[1] += ly;
            rawSum[2] += rx;
            rawSum[3] += ry;
            if (++learnSamples < CenterSamplesNeeded)
            {
                return;
            }

            for (int i = 0; i < AxisCount; i++)
            {
                centers[i] = (ushort)(rawSum[i] / learnSamples);
            }

            ResetLearning();
            SyncTracked();
        }

        /// <summary>
        /// 12 位原始值 → 设备轴值。死区与行程都由设置页给，蓝牙和有线共用同一个死区。
        /// </summary>
        public ushort ToDevice(ushort raw, Axis axis)
        {
            int i = (int)axis;
            if (!tracked)
            {
                SyncTracked();
            }

            if (capture == null)
            {
                TrackCenter(i, raw);
            }

            int range = DefaultDirectionalRange;
            if (profile.RangeCalibrated)
            {
                int measured = raw < centers[i]
                    ? centers[i] - BoundsFor(axis).Minimum
                    : BoundsFor(axis).Maximum - centers[i];
                range = Math.Max(1, (int)Math.Round(measured * EndpointReserve));
            }

            ushort output = StickAxisNormalizer.Normalize(
                raw,
                trackedCenter[i],
                range,
                Pro2GyroBiasStore.ActiveStickDeadzone,
                ref carry[i]);

            lastRaw[i] = raw;
            lastOut[i] = output;
            outSum[i] += output;
            if (outCount[i] == 0 || output < outMin[i]) outMin[i] = output;
            if (outCount[i] == 0 || output > outMax[i]) outMax[i] = output;
            outCount[i]++;
            return output;
        }

        public string BeginCenterCapture()
        {
            capture = new AxisCapture(AxisCapture.Mode.Center);
            return "status=started mode=center duration_seconds=2 keep_sticks_released=1";
        }

        public StickCalibrationResult FinishCenterCapture()
        {
            AxisCapture? done = Take(AxisCapture.Mode.Center);
            if (done == null)
            {
                return Rejected("status=rejected mode=center reason=not_started");
            }

            if (done.Samples < CaptureCenterSamples)
            {
                return Rejected(
                    "status=rejected mode=center reason=insufficient_samples samples=" + done.Samples);
            }

            if (done.Spread > CaptureCenterMaxSpread)
            {
                return Rejected(
                    "status=rejected mode=center reason=sticks_moved max_spread=" + done.Spread);
            }

            PhysicalStickAxes average = done.Average;
            if (!WithinManualCenter(average.Lx) || !WithinManualCenter(average.Ly) ||
                !WithinManualCenter(average.Rx) || !WithinManualCenter(average.Ry))
            {
                return Rejected(
                    "status=rejected mode=center reason=outside_safe_center_window " +
                    average.TelemetryValue);
            }

            ApplyProfile(StickCalibrationProfile.Normalize(profile with
            {
                CenterCalibrated = true,
                CenterLx = average.Lx,
                CenterLy = average.Ly,
                CenterRx = average.Rx,
                CenterRy = average.Ry,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }));

            return new StickCalibrationResult(
                true,
                "status=calibrated mode=center samples=" + done.Samples +
                " max_spread=" + done.Spread + " " + profile.Summary,
                profile);
        }

        public string BeginRangeCapture()
        {
            if (!profile.CenterCalibrated)
            {
                return "status=rejected mode=range reason=center_calibration_required";
            }

            capture = new AxisCapture(AxisCapture.Mode.Range);
            return "status=started mode=range duration_seconds=8 rotate_both_sticks_full_circle=1";
        }

        public StickCalibrationResult FinishRangeCapture()
        {
            AxisCapture? done = Take(AxisCapture.Mode.Range);
            if (done == null)
            {
                return Rejected("status=rejected mode=range reason=not_started");
            }

            if (done.Samples < CaptureRangeSamples)
            {
                return Rejected(
                    "status=rejected mode=range reason=insufficient_samples samples=" + done.Samples);
            }

            PhysicalStickAxes low = done.Minimum;
            PhysicalStickAxes high = done.Maximum;
            string spans = SpanText(low, high);
            if (!TravelEnough(low.Lx, centers[0], high.Lx) ||
                !TravelEnough(low.Ly, centers[1], high.Ly) ||
                !TravelEnough(low.Rx, centers[2], high.Rx) ||
                !TravelEnough(low.Ry, centers[3], high.Ry))
            {
                return Rejected(
                    "status=rejected mode=range reason=incomplete_directional_travel " + spans);
            }

            ApplyProfile(StickCalibrationProfile.Normalize(profile with
            {
                RangeCalibrated = true,
                MinLx = low.Lx,
                MaxLx = high.Lx,
                MinLy = low.Ly,
                MaxLy = high.Ly,
                MinRx = low.Rx,
                MaxRx = high.Rx,
                MinRy = low.Ry,
                MaxRy = high.Ry,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }));

            return new StickCalibrationResult(
                true,
                "status=calibrated mode=range samples=" + done.Samples +
                " endpoint_reserve_percent=3 " + spans + " " + profile.Summary,
                profile);
        }

        private (ushort Minimum, ushort Maximum) BoundsFor(Axis axis) => axis switch
        {
            Axis.LeftX => (profile.MinLx, profile.MaxLx),
            Axis.LeftY => (profile.MinLy, profile.MaxLy),
            Axis.RightX => (profile.MinRx, profile.MaxRx),
            Axis.RightY => (profile.MinRy, profile.MaxRy),
            _ => (0, GamepadState.AxisMax)
        };

        private void SyncTracked()
        {
            for (int i = 0; i < AxisCount; i++)
            {
                trackedCenter[i] = centers[i];
            }

            tracked = true;
        }

        private void TrackCenter(int axis, ushort raw)
        {
            long now = Stopwatch.GetTimestamp();
            long previous = lastTick[axis];
            lastTick[axis] = now;

            if (Math.Abs(raw - trackedCenter[axis]) > QuietWindow || previous == 0)
            {
                return;
            }

            double seconds = (now - previous) / (double)Stopwatch.Frequency;
            if (seconds <= 0 || seconds > 0.5)
            {
                return;   // 断流或卡顿时不跟，免得把跳变当成零位
            }

            double alpha = 1.0 - Math.Exp(-seconds / TrackTauSeconds);
            trackedCenter[axis] += (raw - trackedCenter[axis]) * alpha;
        }

        private AxisCapture? Take(AxisCapture.Mode expected)
        {
            AxisCapture? done = capture;
            capture = null;
            return done?.Kind == expected ? done : null;
        }

        private StickCalibrationResult Rejected(string message) =>
            new(false, message, profile);

        private void ResetLearning()
        {
            learnSamples = 0;
            for (int i = 0; i < AxisCount; i++)
            {
                rawSum[i] = 0;
            }
        }

        private string SpanText(PhysicalStickAxes low, PhysicalStickAxes high) =>
            "span_lx=" + (centers[0] - low.Lx) + "/" + (high.Lx - centers[0]) +
            " span_ly=" + (centers[1] - low.Ly) + "/" + (high.Ly - centers[1]) +
            " span_rx=" + (centers[2] - low.Rx) + "/" + (high.Rx - centers[2]) +
            " span_ry=" + (centers[3] - low.Ry) + "/" + (high.Ry - centers[3]);

        private static bool TravelEnough(ushort low, ushort center, ushort high) =>
            low < center &&
            high > center &&
            center - low >= CaptureMinimumTravel &&
            high - center >= CaptureMinimumTravel;

        private static bool NearFactory(ushort value) =>
            Math.Abs(value - FactoryCenter) <= CenterLearnTolerance;

        private static bool WithinManualCenter(ushort value) =>
            value >= FactoryCenter - ManualCenterWindow && value <= FactoryCenter + ManualCenterWindow;

        private static string F(double value) => value.ToString("F2");

        private string Mean(int axis) =>
            outCount[axis] == 0 ? "-" : (outSum[axis] / (double)outCount[axis]).ToString("F3");

        private string Span(int axis) =>
            outCount[axis] == 0 ? "-" : (outMax[axis] - outMin[axis]).ToString();

        /// <summary>一次手动采集：中心（要稳）或行程（要推满）。</summary>
        private sealed class AxisCapture
        {
            public enum Mode
            {
                Center,
                Range
            }

            private readonly long[] sum = new long[AxisCount];
            private readonly ushort[] low = { ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue };
            private readonly ushort[] high = new ushort[AxisCount];

            public AxisCapture(Mode kind)
            {
                Kind = kind;
            }

            public Mode Kind { get; }

            public int Samples { get; private set; }

            public string ModeName => Kind == Mode.Center ? "center" : "range";

            public PhysicalStickAxes Minimum => new(low[0], low[1], low[2], low[3]);

            public PhysicalStickAxes Maximum => new(high[0], high[1], high[2], high[3]);

            public int Spread
            {
                get
                {
                    int spread = 0;
                    for (int i = 0; i < AxisCount; i++)
                    {
                        spread = Math.Max(spread, high[i] - low[i]);
                    }

                    return spread;
                }
            }

            public PhysicalStickAxes Average
            {
                get
                {
                    if (Samples == 0)
                    {
                        return new PhysicalStickAxes(
                            FactoryCenter, FactoryCenter, FactoryCenter, FactoryCenter);
                    }

                    return new PhysicalStickAxes(
                        RoundedAverage(sum[0], Samples),
                        RoundedAverage(sum[1], Samples),
                        RoundedAverage(sum[2], Samples),
                        RoundedAverage(sum[3], Samples));
                }
            }

            public void Add(ushort lx, ushort ly, ushort rx, ushort ry)
            {
                ushort[] values = { lx, ly, rx, ry };
                for (int i = 0; i < AxisCount; i++)
                {
                    low[i] = Math.Min(low[i], values[i]);
                    high[i] = Math.Max(high[i], values[i]);
                    sum[i] += values[i];
                }

                Samples++;
            }

            private static ushort RoundedAverage(long total, int count) =>
                (ushort)((total + count / 2) / count);
        }
    }

    /// <summary>
    /// 没有质量管线时的陀螺零偏估计：静止窗口内取均值，标准差太大就当手在动。
    /// 自动模式 64 帧，手动模式 200 帧（三秒左右，提示语让用户把手柄放平）。
    /// </summary>
    private sealed class GyroBiasEstimator
    {
        private const int AutoSamples = 64;
        private const int ManualSamples = 200;
        private const int StationaryGyroLimit = 256;
        private const int GravityMinimum = 3000;
        private const int GravityMaximum = 5400;
        private const double MaximumGyroStd = 3.5;
        private const double MaximumAccelNormStd = 12.0;

        private long sumX, sumY, sumZ;
        private long squareX, squareY, squareZ;
        private double accelNormSum, accelNormSquareSum;
        private int samples;
        private bool ready;
        private bool manual;
        private double biasX, biasY, biasZ;
        private string result = "auto_waiting_stationary";

        public string Summary =>
            "status=" + (manual ? "manual_collecting" : ready ? "calibrated" : "not_calibrated") +
            " samples=" + samples +
            " bias_raw=" + biasX.ToString("0.###") + "," +
            biasY.ToString("0.###") + "," + biasZ.ToString("0.###") +
            " result=" + result;

        public string StartManualCalibration()
        {
            manual = true;
            result = "manual_collecting_keep_controller_still";
            Reset();
            return "陀螺仪三秒校准已开始；请将手柄静置在稳定平面。";
        }

        public void Apply(GamepadState state)
        {
            bool idle = state.Buttons == GamepadButtons.None &&
                        Math.Abs(state.Lx - FactoryCenter) <= 128 &&
                        Math.Abs(state.Ly - FactoryCenter) <= 128 &&
                        Math.Abs(state.Rx - FactoryCenter) <= 128 &&
                        Math.Abs(state.Ry - FactoryCenter) <= 128;

            bool still = IsStill(state.AccelX, state.AccelY, state.AccelZ,
                state.GyroX, state.GyroY, state.GyroZ);

            if (manual)
            {
                if (idle && still)
                {
                    Accumulate(state, ManualSamples, true);
                }
                else
                {
                    result = "manual_motion_detected_window_restarted";
                    Reset();
                }
            }
            else if (!ready)
            {
                if (idle && still)
                {
                    Accumulate(state, AutoSamples, false);
                }
                else
                {
                    Reset();
                }
            }

            if (!ready)
            {
                return;
            }

            state.GyroX = Clamp((int)Math.Round(state.GyroX - biasX));
            state.GyroY = Clamp((int)Math.Round(state.GyroY - biasY));
            state.GyroZ = Clamp((int)Math.Round(state.GyroZ - biasZ));
        }

        private static bool IsStill(int ax, int ay, int az, int gx, int gy, int gz)
        {
            long gravity = (long)ax * ax + (long)ay * ay + (long)az * az;
            bool plausible =
                gravity >= (long)GravityMinimum * GravityMinimum &&
                gravity <= (long)GravityMaximum * GravityMaximum;
            return plausible &&
                   Math.Abs(gx) <= StationaryGyroLimit &&
                   Math.Abs(gy) <= StationaryGyroLimit &&
                   Math.Abs(gz) <= StationaryGyroLimit;
        }

        private void Accumulate(GamepadState state, int needed, bool isManual)
        {
            sumX += state.GyroX;
            sumY += state.GyroY;
            sumZ += state.GyroZ;
            squareX += (long)state.GyroX * state.GyroX;
            squareY += (long)state.GyroY * state.GyroY;
            squareZ += (long)state.GyroZ * state.GyroZ;

            double norm = Math.Sqrt(
                (double)state.AccelX * state.AccelX +
                (double)state.AccelY * state.AccelY +
                (double)state.AccelZ * state.AccelZ);
            accelNormSum += norm;
            accelNormSquareSum += norm * norm;
            samples++;

            if (samples < needed)
            {
                return;
            }

            double meanX = (double)sumX / samples;
            double meanY = (double)sumY / samples;
            double meanZ = (double)sumZ / samples;
            double stdX = Std(squareX, meanX, samples);
            double stdY = Std(squareY, meanY, samples);
            double stdZ = Std(squareZ, meanZ, samples);
            double accelMean = accelNormSum / samples;
            double accelStd = Math.Sqrt(Math.Max(0, accelNormSquareSum / samples - accelMean * accelMean));

            string prefix = isManual ? "manual" : "auto";
            if (stdX > MaximumGyroStd || stdY > MaximumGyroStd || stdZ > MaximumGyroStd ||
                accelStd > MaximumAccelNormStd)
            {
                result = prefix + "_rejected_unstable std_gyro=" +
                         stdX.ToString("0.###") + "," + stdY.ToString("0.###") + "," +
                         stdZ.ToString("0.###") +
                         " accel_norm_std=" + accelStd.ToString("0.###");
                Reset();
                return;
            }

            biasX = meanX;
            biasY = meanY;
            biasZ = meanZ;
            ready = true;
            manual = false;
            result = prefix + "_committed std_gyro=" +
                     stdX.ToString("0.###") + "," + stdY.ToString("0.###") + "," +
                     stdZ.ToString("0.###") +
                     " accel_norm_std=" + accelStd.ToString("0.###");
            Reset();
        }

        private static double Std(long squareSum, double mean, int count) =>
            Math.Sqrt(Math.Max(0, (double)squareSum / count - mean * mean));

        private static short Clamp(int value) =>
            (short)Math.Clamp(value, short.MinValue, short.MaxValue);

        private void Reset()
        {
            sumX = sumY = sumZ = 0;
            squareX = squareY = squareZ = 0;
            accelNormSum = accelNormSquareSum = 0;
            samples = 0;
        }
    }
}
