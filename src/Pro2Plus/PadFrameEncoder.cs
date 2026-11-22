using System;
using System.Buffers.Binary;

namespace Pro2Plus;

/// <summary>
/// 虚拟手柄输入帧编码器。把内部 <see cref="GamepadState"/> 按目标设备的 HID 布局写进调用方给的
/// <see cref="Span{T}"/>：控制器热路径每 4 ms 调一次，所以整条路径不产生托管分配。
///
/// 布局与协议对应关系：
///   DualSense（PS5） 33 字节：摇杆 0-3、按钮 4-7、十字键 8、扳机 9-10、触摸 11-15、IMU 21-31
///   Switch（Pro2/Pro1）28 字节：按钮 0-3、摇杆 4-11、IMU 12-23、时间戳 24-27
///   Xbox 20 字节：按钮 0-3、扳机 4-5、摇杆 6-13
/// </summary>
public static class PadFrameEncoder
{
    public const int DualSenseSize = 33;
    public const int SwitchSize = 28;
    public const int XboxSize = 20;
    public const int MaxFrameSize = DualSenseSize;

    private const ushort AxisCenter = GamepadState.AxisCenter;
    private const ushort AxisMax = GamepadState.AxisMax;
    private const ushort TriggerMax = GamepadState.TriggerMax;

    // PS5 静止平放时的重力分量：SDL 坐标系 +Y 向上，平放读到 +Y 1g。
    private static readonly short[] NeutralGravity = [0, 8192, 0];

    private readonly record struct KeyBit(GamepadButtons Flag, uint Code);

    /// <summary>PS5：b[4..7] 是 32 位按钮字，方块/叉/圆/三角占 0x10-0x80。</summary>
    private static readonly KeyBit[] DualSenseKeys =
    [
        new(GamepadButtons.West, 0x0000_0010),
        new(GamepadButtons.South, 0x0000_0020),
        new(GamepadButtons.East, 0x0000_0040),
        new(GamepadButtons.North, 0x0000_0080),
        new(GamepadButtons.L1, 0x0000_0100),
        new(GamepadButtons.R1, 0x0000_0200),
        new(GamepadButtons.L2, 0x0000_0400),
        new(GamepadButtons.R2, 0x0000_0800),
        new(GamepadButtons.Back, 0x0000_1000),
        new(GamepadButtons.Start, 0x0000_2000),
        new(GamepadButtons.LeftStick, 0x0000_4000),
        new(GamepadButtons.RightStick, 0x0000_8000),
        new(GamepadButtons.Home, 0x0001_0000),
        new(GamepadButtons.Capture, 0x0002_0000)
    ];

    /// <summary>Switch：b[0..3] 32 位按钮字，B/A/Y/X 依次占 bit0-3。</summary>
    private static readonly KeyBit[] SwitchKeys =
    [
        new(GamepadButtons.South, 0x0000_0001),
        new(GamepadButtons.East, 0x0000_0002),
        new(GamepadButtons.West, 0x0000_0004),
        new(GamepadButtons.North, 0x0000_0008),
        new(GamepadButtons.R1, 0x0000_0010),
        new(GamepadButtons.R2, 0x0000_0020),
        new(GamepadButtons.Start, 0x0000_0040),
        new(GamepadButtons.RightStick, 0x0000_0080),
        new(GamepadButtons.DPadDown, 0x0000_0100),
        new(GamepadButtons.DPadRight, 0x0000_0200),
        new(GamepadButtons.DPadLeft, 0x0000_0400),
        new(GamepadButtons.DPadUp, 0x0000_0800),
        new(GamepadButtons.L1, 0x0000_1000),
        new(GamepadButtons.L2, 0x0000_2000),
        new(GamepadButtons.Back, 0x0000_4000),
        new(GamepadButtons.LeftStick, 0x0000_8000),
        new(GamepadButtons.Home, 0x0001_0000),
        new(GamepadButtons.Capture, 0x0002_0000),
        new(GamepadButtons.PaddleRight, 0x0004_0000),
        new(GamepadButtons.PaddleLeft, 0x0008_0000),
        new(GamepadButtons.Aux, 0x0010_0000)
    ];

    /// <summary>Pro1（Switch 1）没有背键和 C 键，报文字段也不存在，只取前 18 项。</summary>
    private const int Pro1KeyCount = 18;

    /// <summary>Xbox 360：b[0..3] 16 位按钮字。</summary>
    private static readonly KeyBit[] XboxKeys =
    [
        new(GamepadButtons.DPadUp, 0x0001),
        new(GamepadButtons.DPadDown, 0x0002),
        new(GamepadButtons.DPadLeft, 0x0004),
        new(GamepadButtons.DPadRight, 0x0008),
        new(GamepadButtons.Start, 0x0010),
        new(GamepadButtons.Back, 0x0020),
        new(GamepadButtons.LeftStick, 0x0040),
        new(GamepadButtons.RightStick, 0x0080),
        new(GamepadButtons.L1, 0x0100),
        new(GamepadButtons.R1, 0x0200),
        new(GamepadButtons.Home, 0x0400),
        new(GamepadButtons.South, 0x1000),
        new(GamepadButtons.East, 0x2000),
        new(GamepadButtons.West, 0x4000),
        new(GamepadButtons.North, 0x8000)
    ];

    /// <summary>目标报文长度；键鼠模式不走本编码器，返回 0。</summary>
    public static int SizeOf(ViiperVirtualMode mode) => mode switch
    {
        ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.DualSenseProfessionalImuTest => DualSenseSize,
        ViiperVirtualMode.Pro1 or ViiperVirtualMode.Pro2 => SwitchSize,
        ViiperVirtualMode.Xbox or ViiperVirtualMode.XboxProfessionalImuTest => XboxSize,
        _ => 0
    };

    /// <summary>写入中立帧（未接手柄、切模式保护、断连兜底都用它）。</summary>
    public static void WriteNeutral(Span<byte> frame, ViiperVirtualMode mode)
    {
        switch (mode)
        {
            case ViiperVirtualMode.DualSenseLike:
            case ViiperVirtualMode.DualSenseProfessionalImuTest:
                frame[..DualSenseSize].Clear();
                WriteI16(frame, 27, NeutralGravity[0]);
                WriteI16(frame, 29, NeutralGravity[1]);
                WriteI16(frame, 31, NeutralGravity[2]);
                break;
            case ViiperVirtualMode.Pro1:
            case ViiperVirtualMode.Pro2:
                frame[..SwitchSize].Clear();
                WriteU16(frame, 4, AxisCenter);
                WriteU16(frame, 6, AxisCenter);
                WriteU16(frame, 8, AxisCenter);
                WriteU16(frame, 10, AxisCenter);
                break;
            case ViiperVirtualMode.Xbox:
            case ViiperVirtualMode.XboxProfessionalImuTest:
                frame[..XboxSize].Clear();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>按设备类型编码一帧。</summary>
    public static void Write(
        Span<byte> frame,
        ViiperDeviceProfile profile,
        GamepadState state,
        GyroAxisInversion gyroAxisInversion,
        Ps5OutputImuTuning ps5OutputImuTuning,
        DualSenseImuRawSample? professionalDualSenseImu,
        GamepadState? professionalXboxState)
    {
        switch (profile.Mode)
        {
            case ViiperVirtualMode.DualSenseLike:
                WriteDualSense(frame, state, gyroAxisInversion, ps5OutputImuTuning, professionalImu: null);
                break;
            case ViiperVirtualMode.DualSenseProfessionalImuTest:
                WriteDualSense(frame, state, gyroAxisInversion, ps5OutputImuTuning, professionalDualSenseImu);
                break;
            case ViiperVirtualMode.Pro2:
                WriteSwitch(frame, state, gyroAxisInversion, pro1Axes: false);
                break;
            case ViiperVirtualMode.Pro1:
                WriteSwitch(frame, state, gyroAxisInversion, pro1Axes: true);
                break;
            case ViiperVirtualMode.Xbox:
                WriteXbox(frame, state);
                break;
            case ViiperVirtualMode.XboxProfessionalImuTest:
                WriteXbox(frame, professionalXboxState ?? state);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile));
        }
    }

    // ---------------------------------------------------------------- PS5

    private static void WriteDualSense(
        Span<byte> frame,
        GamepadState state,
        GyroAxisInversion inversion,
        Ps5OutputImuTuning tuning,
        DualSenseImuRawSample? professionalImu)
    {
        frame[0] = unchecked((byte)AxisToI8(state.Lx, invert: false));
        frame[1] = unchecked((byte)AxisToI8(state.Ly, invert: true));
        frame[2] = unchecked((byte)AxisToI8(state.Rx, invert: false));
        frame[3] = unchecked((byte)AxisToI8(state.Ry, invert: true));
        WriteU32(frame, 4, PackKeys(DualSenseKeys, state.Buttons, int.MaxValue));
        frame[8] = PackDpad(state.Buttons);
        frame[9] = TriggerToU8(state.L2, (state.Buttons & GamepadButtons.L2) != 0);
        frame[10] = TriggerToU8(state.R2, (state.Buttons & GamepadButtons.R2) != 0);
        WriteU16(frame, 11, Math.Min(state.Ps5Touch.X, Ps5TouchGestures.MaxX));
        WriteU16(frame, 13, Math.Min(state.Ps5Touch.Y, Ps5TouchGestures.MaxY));
        // 第二触点保持未激活：同时触发的宏不会被拼成一根手指或重复的 tracking id。
        frame[15] = state.Ps5Touch.Active ? (byte)1 : (byte)0;

        if (professionalImu is { Valid: true } imu)
        {
            WriteI16(frame, 21, imu.GyroX);
            WriteI16(frame, 23, imu.GyroY);
            WriteI16(frame, 25, imu.GyroZ);
            WriteI16(frame, 27, imu.AccelX);
            WriteI16(frame, 29, imu.AccelY);
            WriteI16(frame, 31, imu.AccelZ);
            return;
        }

        // Pro2 → DualSense 的轴与单位换算：
        //   加速度 4096/g → 8192/g，正好 ×2；(+X, +Z, -Y)
        //   角速度 两侧都是 16.384 counts/dps，比例 1:1，只剩轴重排和可选倍率
        short aX = (short)Math.Clamp(state.AccelX * 2, short.MinValue, short.MaxValue);
        short aY = (short)Math.Clamp(state.AccelZ * 2, short.MinValue, short.MaxValue);
        short aZ = (short)Math.Clamp(-state.AccelY * 2, short.MinValue, short.MaxValue);

        short gx = Invert(state.GyroX, inversion.InvertX);
        short gy = Invert(state.GyroY, inversion.InvertY);
        short gz = Invert(state.GyroZ, inversion.InvertZ);

        WriteI16(frame, 21, state.GyroValid ? Scale(gx, tuning.GyroScalePitch) : (short)0);
        WriteI16(frame, 23, state.GyroValid ? Scale(gz, tuning.GyroScaleYaw) : (short)0);
        WriteI16(frame, 25, state.GyroValid ? Scale(Negate(gy), tuning.GyroScaleRoll) : (short)0);
        WriteI16(frame, 27, state.AccelValid ? aX : NeutralGravity[0]);
        WriteI16(frame, 29, state.AccelValid ? aY : NeutralGravity[1]);
        WriteI16(frame, 31, state.AccelValid ? aZ : NeutralGravity[2]);
    }

    private static byte PackDpad(GamepadButtons buttons)
    {
        byte dpad = 0;
        if ((buttons & GamepadButtons.DPadUp) != 0) dpad |= 0x01;
        if ((buttons & GamepadButtons.DPadDown) != 0) dpad |= 0x02;
        if ((buttons & GamepadButtons.DPadLeft) != 0) dpad |= 0x04;
        if ((buttons & GamepadButtons.DPadRight) != 0) dpad |= 0x08;
        return dpad;
    }

    // ------------------------------------------------------------- Switch

    private static void WriteSwitch(
        Span<byte> frame,
        GamepadState state,
        GyroAxisInversion inversion,
        bool pro1Axes)
    {
        int limit = pro1Axes ? Pro1KeyCount : SwitchKeys.Length;
        WriteU32(frame, 0, PackKeys(SwitchKeys, state.Buttons, limit));
        WriteU16(frame, 4, ClampAxis(state.Lx));
        WriteU16(frame, 6, ClampAxis(state.Ly));
        WriteU16(frame, 8, ClampAxis(state.Rx));
        WriteU16(frame, 10, ClampAxis(state.Ry));

        short aX = state.AccelX, aY = state.AccelY, aZ = state.AccelZ;
        short gx = Invert(state.GyroX, inversion.InvertX);
        short gy = Invert(state.GyroY, inversion.InvertY);
        short gz = Invert(state.GyroZ, inversion.InvertZ);

        if (pro1Axes)
        {
            // Switch 1 消费端按 (-Y, +Z, -X) 归一化，Pro2 物理轴是 (+X, +Z, -Y)。
            // 在同一协议边界做前置旋转，源轴反转已经先应用过。
            (aX, aY) = (aY, Negate(aX));
            (gx, gy) = (gy, Negate(gx));
        }

        WriteI16(frame, 12, state.AccelValid ? aX : (short)0);
        WriteI16(frame, 14, state.AccelValid ? aY : (short)0);
        WriteI16(frame, 16, state.AccelValid ? aZ : (short)0);
        WriteI16(frame, 18, state.GyroValid ? gx : (short)0);
        WriteI16(frame, 20, state.GyroValid ? gy : (short)0);
        WriteI16(frame, 22, state.GyroValid ? gz : (short)0);
        WriteU32(frame, 24, state.MotionTimestampUs);
    }

    // --------------------------------------------------------------- Xbox

    private static void WriteXbox(Span<byte> frame, GamepadState state)
    {
        WriteU32(frame, 0, PackKeys(XboxKeys, state.Buttons, int.MaxValue));
        frame[4] = TriggerToU8(state.L2, (state.Buttons & GamepadButtons.L2) != 0);
        frame[5] = TriggerToU8(state.R2, (state.Buttons & GamepadButtons.R2) != 0);
        WriteI16(frame, 6, AxisToI16(state.Lx, invert: false));
        WriteI16(frame, 8, AxisToI16(state.Ly, invert: false));
        WriteI16(frame, 10, AxisToI16(state.Rx, invert: false));
        WriteI16(frame, 12, AxisToI16(state.Ry, invert: false));
    }

    // -------------------------------------------------------------- 共用

    /// <summary>按键表驱动打包：一次遍历把命中的位码 OR 起来。</summary>
    private static uint PackKeys(KeyBit[] table, GamepadButtons buttons, int limit)
    {
        uint packed = 0;
        int count = Math.Min(limit, table.Length);
        for (int i = 0; i < count; i++)
        {
            if ((buttons & table[i].Flag) != 0)
            {
                packed |= table[i].Code;
            }
        }
        return packed;
    }

    private static sbyte AxisToI8(ushort value, bool invert)
    {
        short wide = AxisToI16(value, invert);
        int scaled = wide >= 0 ? (wide * 127 + 16383) / 32767 : (wide * 128 - 16384) / 32768;
        if (scaled < -128) scaled = -128;
        if (scaled > 127) scaled = 127;
        return (sbyte)scaled;
    }

    private static short AxisToI16(ushort value, bool invert)
    {
        // 校准后的零位与死区由采集层负责；这里不再叠一层 ±64 截断，
        // 否则会把已经校准过的微小位移吞掉（Raw Direct 下尤其明显）。
        value = ClampAxis(value);
        int centered = value - AxisCenter;
        int scaled = centered >= 0
            ? centered * 32767 / (AxisMax - AxisCenter)
            : centered * 32768 / AxisCenter;
        if (invert) scaled = -scaled;
        if (scaled < short.MinValue) return short.MinValue;
        if (scaled > short.MaxValue) return short.MaxValue;
        return (short)scaled;
    }

    private static byte TriggerToU8(ushort value, bool pressed)
    {
        if (value == 0 && pressed)
        {
            return 255; // 只有数字扳机没有模拟量时按满量程输出
        }
        uint scaled = ((uint)value * 255u + TriggerMax / 2u) / TriggerMax;
        return (byte)Math.Min(255u, scaled);
    }

    private static ushort ClampAxis(ushort value) => value > AxisMax ? AxisMax : value;

    private static short Invert(short value, bool invert) => invert ? Negate(value) : value;

    private static short Negate(short value) => value == short.MinValue ? short.MaxValue : (short)-value;

    /// <summary>倍率为 1 时直接返回原值，避开浮点往返（默认配置走这条快路径）。</summary>
    private static short Scale(short value, double factor)
    {
        if (factor == 1.0)
        {
            return value;
        }
        double scaled = Math.Round(value * factor, MidpointRounding.AwayFromZero);
        if (scaled < short.MinValue) return short.MinValue;
        if (scaled > short.MaxValue) return short.MaxValue;
        return (short)scaled;
    }

    private static void WriteU16(Span<byte> b, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(offset, 2), value);

    private static void WriteI16(Span<byte> b, int offset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(b.Slice(offset, 2), value);

    private static void WriteU32(Span<byte> b, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(offset, 4), value);
}
