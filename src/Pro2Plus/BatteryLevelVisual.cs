using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Pro2Plus;

public sealed record BatterySegment(bool Filled, Brush Fill);

/// <summary>
/// 电量条展示：只有「已连接 + 有档位 + 档位合法」才画填充格，其余一律显示未知。
/// 九格直接按索引构造，避免为每帧生成 LINQ 迭代器与中间数组。
/// </summary>
public sealed record BatteryLevelVisual(string Level, string Caption, string Charge, BatterySegment[] Segments)
{
    private const int SegmentCount = 9;
    private static readonly Brush Green = Frozen("#40866A"), Amber = Frozen("#AD751D"), Empty = Frozen("#E5EBE7");

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    public static BatteryLevelVisual Create(GamepadState state, bool connected, bool hasLevel, bool freshPower,
        string levelCaption = "连接时档位")
    {
        bool known = connected && hasLevel && state.BatteryLevel <= 9;
        int level = known ? state.BatteryLevel : 0;
        string caption = !connected ? "连接后读取" :
            (known ? levelCaption : "档位未知") +
            (state.BatteryVoltageMillivolts > 0
                ? " · " + (state.BatteryVoltageMillivolts / 1000.0).ToString("F2") + " V" : "");
        string charge = connected && freshPower && state.BatteryPowerKnown
            ? state.BatteryCharging ? "充电中" : state.BatteryExternalPower ? "外接电源" : "" : "";

        var segments = new BatterySegment[SegmentCount];
        for (int i = 0; i < SegmentCount; i++)
        {
            bool filled = known && i < level;
            segments[i] = new BatterySegment(filled, filled ? (level <= 2 ? Amber : Green) : Empty);
        }

        return new(known ? level + " / 9" : "未知", caption, charge, segments);
    }
}
