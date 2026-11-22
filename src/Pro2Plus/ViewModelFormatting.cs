using System;

namespace Pro2Plus;

/// <summary>视图模型里的纯格式化与参数收敛，无 UI 状态依赖。</summary>
internal static class ViewModelFormatting
{
    internal static string RangeHint(double left, double right)
    {
        const double done = 0.98;
        bool l = left >= done, r = right >= done;
        return (l, r) switch
        {
            (true, true) => "已覆盖，保持 2 秒",
            (true, false) => "还差右摇杆",
            (false, true) => "还差左摇杆",
            _ => ""
        };
    }

    /// <summary>非有限值退回默认值，否则按指定精度四舍五入后收敛到区间。</summary>
    private static double ClampFinite(double value, int decimals, double min, double max, double fallback) =>
        !double.IsFinite(value) ? fallback : Math.Clamp(Math.Round(value, decimals), min, max);

    internal static double ClampRange(double value, double min, double max, double fallback) =>
        ClampFinite(value, 3, min, max, fallback);

    internal static byte[] BuildTestRumbleReport()
    {
        byte[] report = new byte[64];
        report[0] = 0x02;
        // packed 40bit：freq(10) | amp(10) | freq(10) | amp(10)，振幅字段在 bit10 / bit30。
        ulong packed = (0x87UL) | ((ulong)400 << 10) | (0x87UL << 20) | ((ulong)400 << 30);
        for (int side = 1; side <= 17; side += 16)
        {
            report[side] = 0x50; // known format + sequence
            for (int frame = 0; frame < 3; frame++)
            {
                int off = side + 1 + frame * 5;
                for (int i = 0; i < 5; i++) report[off + i] = (byte)(packed >> (i * 8));
            }
        }
        return report;
    }

    internal static string LowRateNumber(double value) => double.IsFinite(value) && value >= 0
        ? value.ToString("F1") : "未获取";
    internal static string PercentText(int raw) =>
        (raw / (GamepadState.AxisCenter / 100.0)).ToString("0.0") + "%";

    internal static double ClampScale(double value) =>
        ClampFinite(value, 2, 0.25, 4.0, 1.0);

    internal static string DescribeUsbipSource(string source) => source switch
    {
        "saved_manual_path" => "手动指定",
        "near_application" => "程序附近目录",
        "common_directory" => "常用安装目录",
        _ when source.StartsWith("path_", StringComparison.Ordinal) => "环境变量",
        _ when source.StartsWith("uninstall_", StringComparison.Ordinal) || source.StartsWith("app_paths_", StringComparison.Ordinal) => "安装记录",
        _ => "自动查找"
    };
}
