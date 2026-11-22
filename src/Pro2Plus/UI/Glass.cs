using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;

namespace Pro2Plus.UI;

/// <summary>
/// 玻璃卡片的模板参数（对齐 Figma 审计结果）：
/// Radius 圆角、Sheen 顶部 1px 高光、Shadow 独立阴影层（不污染卡内文字）、Smoothing 圆角平滑度。
/// </summary>
public static class Glass
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(Glass),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static void SetRadius(DependencyObject obj, double value) => obj.SetValue(RadiusProperty, value);
    public static double GetRadius(DependencyObject obj) => (double)obj.GetValue(RadiusProperty);

    public static readonly DependencyProperty SmoothingProperty = DependencyProperty.RegisterAttached(
        "Smoothing", typeof(double), typeof(Glass),
        new FrameworkPropertyMetadata(0.62, FrameworkPropertyMetadataOptions.AffectsRender));

    public static void SetSmoothing(DependencyObject obj, double value) => obj.SetValue(SmoothingProperty, value);
    public static double GetSmoothing(DependencyObject obj) => (double)obj.GetValue(SmoothingProperty);

    public static readonly DependencyProperty SheenProperty = DependencyProperty.RegisterAttached(
        "Sheen", typeof(Brush), typeof(Glass),
        new FrameworkPropertyMetadata(null));

    public static void SetSheen(DependencyObject obj, Brush value) => obj.SetValue(SheenProperty, value);
    public static Brush GetSheen(DependencyObject obj) => (Brush)obj.GetValue(SheenProperty);

    /// <summary>"" 无阴影 / "Card" 黑色卡片阴影 / "Accent" 蓝色高亮阴影。</summary>
    public static readonly DependencyProperty ShadowProperty = DependencyProperty.RegisterAttached(
        "Shadow", typeof(string), typeof(Glass), new FrameworkPropertyMetadata(""));

    public static void SetShadow(DependencyObject obj, string value) => obj.SetValue(ShadowProperty, value);
    public static string GetShadow(DependencyObject obj) => (string)obj.GetValue(ShadowProperty);
}

/// <summary>把圆角半径换算成顶部高光的内缩，避免 1px 高光画到圆角外面变成廉价描边。</summary>
public sealed class SheenMarginConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double r = value is double d ? d : 24.0;
        double inset = Math.Max(2.0, r * 0.72);
        return new Thickness(inset, 0.5, inset, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Figma cornerSmoothing 风格的连续曲率圆角（squircle）。
/// WPF 的 Border.CornerRadius 是标准圆弧，在 22–26px 圆角上明显「塑料感」；
/// 这里用超椭圆 |u|^n + |v|^n = 1 采样每个角，n 由 smoothing 推导。
/// 描边按 INSIDE 对齐（几何内缩半个笔宽），与 Figma strokeAlign = INSIDE 一致。
/// </summary>
public sealed class Squircle : System.Windows.Shapes.Shape
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(Squircle),
        new FrameworkPropertyMetadata(22.0,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public static readonly DependencyProperty SmoothingProperty = DependencyProperty.Register(
        nameof(Smoothing), typeof(double), typeof(Squircle),
        new FrameworkPropertyMetadata(0.62,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Smoothing
    {
        get => (double)GetValue(SmoothingProperty);
        set => SetValue(SmoothingProperty, value);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override Geometry DefiningGeometry
    {
        get
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 1 || h <= 1) return Geometry.Empty;

            double inset = Math.Max(0, StrokeThickness / 2.0);
            double x0 = inset, y0 = inset, x1 = w - inset, y1 = h - inset;
            double span = Math.Min(x1 - x0, y1 - y0);
            double r = Math.Max(0, Math.Min(Radius - inset, span / 2.0));
            if (r <= 0.5) r = 0.5;

            double n = 2.0 + 3.5 * Math.Clamp(Smoothing, 0.0, 1.0);
            const int N = 14;
            var pts = new List<Point>(4 * (N + 1) + 2)
            {
                new Point(x0 + r, y0),
                new Point(x1 - r, y0)
            };

            AddCorner(pts, x1 - r, y0 + r, +1, -1, true, r, n, N);   // 右上：(x1-r,y0) → (x1,y0+r)
            AddCorner(pts, x1 - r, y1 - r, +1, +1, false, r, n, N);  // 右下：(x1,y1-r) → (x1-r,y1)
            AddCorner(pts, x0 + r, y1 - r, -1, +1, true, r, n, N);   // 左下：(x0+r,y1) → (x0,y1-r)
            AddCorner(pts, x0 + r, y0 + r, -1, -1, false, r, n, N);  // 左上：(x0,y0+r) → (x0+r,y0)

            var figure = new PathFigure { StartPoint = pts[0], IsClosed = true };
            figure.Segments.Add(new PolyLineSegment(pts.GetRange(1, pts.Count - 1), true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }
    }

    private static void AddCorner(List<Point> pts, double cx, double cy,
        int sx, int sy, bool reverse, double r, double n, int N)
    {
        for (int i = 0; i <= N; i++)
        {
            int step = reverse ? N - i : i;
            double t = step * (Math.PI / 2.0) / N;
            double u = Math.Pow(Math.Max(0.0, Math.Cos(t)), 2.0 / n);
            double v = Math.Pow(Math.Max(0.0, Math.Sin(t)), 2.0 / n);
            pts.Add(new Point(cx + sx * r * u, cy + sy * r * v));
        }
    }
}
