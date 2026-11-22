using System;

namespace Pro2Plus;

/// <summary>
/// 虚拟手柄的按键位。位序对应输出报告的按键字，不能随意调整。
/// </summary>
[Flags]
public enum GamepadButtons : ulong
{
    None = 0,
    South = 1UL << 0,
    East = 1UL << 1,
    West = 1UL << 2,
    North = 1UL << 3,
    L1 = 1UL << 4,
    R1 = 1UL << 5,
    L2 = 1UL << 6,
    R2 = 1UL << 7,
    Back = 1UL << 8,
    Start = 1UL << 9,
    LeftStick = 1UL << 10,
    RightStick = 1UL << 11,
    DPadDown = 1UL << 12,
    DPadRight = 1UL << 13,
    DPadLeft = 1UL << 14,
    DPadUp = 1UL << 15,
    Home = 1UL << 16,
    Capture = 1UL << 17,
    PaddleRight = 1UL << 18,
    PaddleLeft = 1UL << 19,
    Aux = 1UL << 20
}

/// <summary>
/// 一帧输入：按键、摇杆、扳机、体感、电量与时间戳。
/// 字段是协议映射的结果，生产者每帧填一份；消费者只读快照，不直接改这份对象。
/// </summary>
public sealed class GamepadState
{
    public const ushort AxisCenter = 2048;
    public const ushort AxisMax = 4095;
    public const ushort TriggerMax = 4095;
    public const byte BatteryUnknown = 255;

    public GamepadButtons Buttons { get; set; }

    // 诊断用：原始按键字/字节的拼装值，随报告格式不同含义不同。只用于定位解析位，不参与出包。
    public uint RawButtons { get; set; }

    public ushort Lx { get; set; } = AxisCenter;
    public ushort Ly { get; set; } = AxisCenter;
    public ushort Rx { get; set; } = AxisCenter;
    public ushort Ry { get; set; } = AxisCenter;
    public ushort L2 { get; set; }
    public ushort R2 { get; set; }

    // 合成出来的 PS5 触点状态；物理 FD2 与它的遥测不受影响。
    public Ps5TouchPoint Ps5Touch { get; set; }

    public bool AccelValid { get; set; }
    public bool GyroValid { get; set; }

    /// <summary>
    /// 解析层是否已经扣过原始零偏（有线协议层自己扣）。扣过的不要再扣一次，
    /// 否则同一份零偏被减两遍，原生体感会恒偏 0.2–0.3 dps。
    /// </summary>
    public bool GyroBiasApplied { get; set; }

    public short AccelX { get; set; }
    public short AccelY { get; set; }
    public short AccelZ { get; set; }
    public short GyroX { get; set; }
    public short GyroY { get; set; }
    public short GyroZ { get; set; }

    public SwitchImuRawSample[] SwitchRawImuSamples { get; set; } = [];
    public int SwitchRawImuOffset { get; set; } = -1;
    public string SwitchRawImuBytesHex { get; set; } = "";

    // Native Switch 2 motion clock from FD2, in microseconds. This is kept
    // separate from SourceTimestampTicks, which measures Windows delivery.
    public uint MotionTimestampUs { get; set; }
    public long SourceTimestampTicks { get; set; }
    public ulong RawNotificationSequence { get; set; }

    public byte BatteryPercent { get; set; } = BatteryUnknown;
    public byte BatteryLevel { get; set; } = BatteryUnknown;
    public bool BatteryPercentApproximate { get; set; }
    public bool BatteryPowerKnown { get; set; }
    public bool BatteryExternalPower { get; set; }
    public ushort BatteryVoltageMillivolts { get; set; }
    public bool BatteryCharging { get; set; }

    // 物理耳机口状态，不是虚拟按键，也不代表音频已经工作。
    public bool HeadsetPresenceKnown { get; set; }
    public bool HeadsetPresent { get; set; }
    public uint Updates { get; set; }

    public bool IsPressed(GamepadButtons button) => (Buttons & button) != 0;

    /// <summary>
    /// 消费者侧快照。IMU 样本数组按值复制：解析层会对同一帧的样本做原地时间戳回填，
    /// 共享数组会让已经发布出去的快照在之后被改写。数组只有几个元素，代价可忽略。
    /// </summary>
    public GamepadState Clone()
    {
        var copy = (GamepadState)MemberwiseClone();
        if (SwitchRawImuSamples.Length > 0)
            copy.SwitchRawImuSamples = (SwitchImuRawSample[])SwitchRawImuSamples.Clone();
        return copy;
    }

    public static GamepadState Neutral() => new();
}
