using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using Visibility = System.Windows.Visibility;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace Pro2Plus.UI;

/// <summary>把后端的状态串折叠成一句中文：含 NotCalibrated 视为未校准。</summary>
public sealed class CalibrationStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = value as string ?? string.Empty;
        if (text.Length == 0) return "未校准";
        return text.Contains("NotCalibrated", StringComparison.OrdinalIgnoreCase) ? "未校准" : "已校准";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>校准状态 → 圆点颜色。</summary>
public sealed class CalibrationDotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = value as string ?? string.Empty;
        bool ok = text.Length > 0 && !text.Contains("NotCalibrated", StringComparison.OrdinalIgnoreCase);
        return ok
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5F, 0xD3, 0x9B))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8C, 0x95, 0x9E));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>true → Visible，false → Collapsed；参数 "invert" 取反。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
