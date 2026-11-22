namespace Pro2Plus;

/// <summary>
/// 推送给虚拟手柄的最终输出快照（不可变），供映射页模型图实时试验用：
/// 按键点亮 + 摇杆位置点都读这份，保证同一帧的数据一致。
/// </summary>
public readonly record struct OutputPadSnapshot(
    ulong Buttons,
    ushort Lx, ushort Ly, ushort Rx, ushort Ry,
    // 扳机行程：模型上看不到 L2/R2，但「按下一个键」的大标识要能显示它们。
    ushort Lt = 0, ushort Rt = 0);
