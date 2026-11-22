using System;

namespace Pro2Plus;

/// <summary>
/// 用源当前的累计帧数播种，而不是从零开始：消费者可能在蓝牙输入启动几分钟后才接入，
/// 接入之前的帧不属于它的第一个统计窗口。源会话重建导致计数回绕时按新会话重新计数。
/// </summary>
public sealed class UpdateRateWindow(uint initialUpdates)
{
    private uint previous = initialUpdates;

    public double Measure(uint current, double seconds)
    {
        uint delta = current >= previous ? current - previous : current;
        previous = current;
        return double.IsFinite(seconds) && seconds > 0 ? delta / seconds : 0;
    }
}
