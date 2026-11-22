using System;

namespace Pro2Plus.Connection;

/// <summary>手柄走哪条链路。Auto 表示还没决定，由服务按可用性挑。</summary>
public enum Pro2LinkKind
{
    None = 0,
    Wired = 1,
    Bluetooth = 2
}

/// <summary>
/// 连接阶段。刻意保持线性，UI 只需要根据它决定文案与按钮可用性。
/// </summary>
public enum Pro2LinkPhase
{
    /// <summary>没有链路，也没在尝试。</summary>
    Idle = 0,
    /// <summary>正在找设备（枚举 USB / 扫 BLE）。</summary>
    Detecting = 1,
    /// <summary>找到了设备，正在打开句柄 / 建立 GATT。</summary>
    Opening = 2,
    /// <summary>已打开，正在跑初始化命令。</summary>
    Initializing = 3,
    /// <summary>可以收输入了。</summary>
    Ready = 4,
    /// <summary>失败，LastError 里有原因。</summary>
    Failed = 5
}

/// <summary>一次连接尝试的结果，用于在界面上解释「为什么连不上」。</summary>
public readonly record struct Pro2LinkProbe(bool Found, int Count, string Detail)
{
    public static Pro2LinkProbe None(string detail) => new(false, 0, detail);
}

/// <summary>服务对外暴露的只读状态快照（值类型，避免跨线程共享可变对象）。</summary>
public readonly record struct Pro2LinkSnapshot(
    Pro2LinkKind Kind,
    Pro2LinkPhase Phase,
    string Title,
    string Detail,
    double RateHz,
    int BatterySteps,
    bool Charging,
    string LastError)
{
    public bool IsReady => Phase == Pro2LinkPhase.Ready;

    public bool IsBusy => Phase is Pro2LinkPhase.Detecting or Pro2LinkPhase.Opening or Pro2LinkPhase.Initializing;

    public static Pro2LinkSnapshot Idle => new(
        Pro2LinkKind.None,
        Pro2LinkPhase.Idle,
        "未连接",
        "插上 USB 线，或让手柄进入蓝牙配对",
        0,
        -1,
        false,
        "");
}
