using System;
using System.Windows;

namespace Pro2Plus;

public static class StartupWindowLayout
{
    // WPF DIPs, not physical pixels: retain taskbar space even with display scaling.
    private const double PreferredWidth = 1060;
    private const double PreferredHeight = 840;
    private const double MaximumInset = 32;
    private const double InsetRatio = .04;

    public static Rect Fit(Rect workArea)
    {
        if (workArea.IsEmpty || !double.IsFinite(workArea.Width) || !double.IsFinite(workArea.Height) ||
            workArea.Width <= 0 || workArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea));

        double width = Math.Min(PreferredWidth, Math.Max(1, workArea.Width - Inset(workArea.Width)));
        double height = Math.Min(PreferredHeight, Math.Max(1, workArea.Height - Inset(workArea.Height)));
        return new(workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2, width, height);
    }

    // 小屏按比例让出任务栏，大屏最多让 32 DIP：再大只是浪费可用面积。
    private static double Inset(double extent) => Math.Min(MaximumInset, extent * InsetRatio);
}
