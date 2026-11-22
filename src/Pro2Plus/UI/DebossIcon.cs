using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using UserControl = System.Windows.Controls.UserControl;
using Grid = System.Windows.Controls.Grid;
using Path = System.Windows.Shapes.Path;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Rect = System.Windows.Rect;

namespace Pro2Plus.UI;

/// <summary>
/// 设计稿的「凹陷 / deboss」水印：冷白油墨面 + 上缘暗内阴影 + 下缘亮内阴影，
/// 图形自身不发光，靠卡片内的柔光显形。用于模式卡、陀螺仪/震动卡的大剪影。
/// </summary>
public sealed class DebossIcon : UserControl
{
    public static readonly DependencyProperty GeometryProperty = DependencyProperty.Register(
        nameof(Geometry), typeof(Geometry), typeof(DebossIcon),
        new FrameworkPropertyMetadata(null, OnShapeChanged));

    public static readonly DependencyProperty MarkOpacityProperty = DependencyProperty.Register(
        nameof(MarkOpacity), typeof(double), typeof(DebossIcon),
        new FrameworkPropertyMetadata(0.15, OnShapeChanged));

    /// <summary>> 0 时把线稿几何加粗成填充几何（用于陀螺仪环形箭头、震动波浪）。</summary>
    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(DebossIcon),
        new FrameworkPropertyMetadata(0.0, OnShapeChanged));

    public Geometry Geometry
    {
        get => (Geometry)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    public double MarkOpacity
    {
        get => (double)GetValue(MarkOpacityProperty);
        set => SetValue(MarkOpacityProperty, value);
    }

    public double StrokeWidth
    {
        get => (double)GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    private readonly Grid _root;

    public DebossIcon()
    {
        IsHitTestVisible = false;
        _root = new Grid();
        Content = _root;
        Loaded += (_, _) => Rebuild();
        SizeChanged += (_, _) => Rebuild();
    }

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DebossIcon)d).Rebuild();

    private void Rebuild()
    {
        if (_root == null || Geometry == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        _root.Children.Clear();

        // 线稿 → 填充几何
        Geometry src = Geometry;
        if (StrokeWidth > 0)
        {
            var p = new Pen(Brushes.Black, StrokeWidth)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            src = src.GetWidenedPathGeometry(p);
        }

        var b = src.Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        var s = Math.Min(ActualWidth / b.Width, ActualHeight / b.Height);
        var ox = (ActualWidth - b.Width * s) / 2 - b.X * s;
        var oy = (ActualHeight - b.Height * s) / 2 - b.Y * s;

        Geometry At(double dx, double dy)
        {
            var g = src.Clone();
            g.Transform = new MatrixTransform(s, 0, 0, s, ox + dx, oy + dy);
            return g;
        }

        // 1) 冷白油墨面
        var alpha = (byte)Math.Round(Math.Max(0, Math.Min(1, MarkOpacity)) * 255);
        _root.Children.Add(new Path
        {
            Data = At(0, 0),
            Stretch = Stretch.None,
            Fill = new SolidColorBrush(Color.FromArgb(alpha, 0xA6, 0xBE, 0xDA))
        });

        // 2) 内阴影：真正按 Figma INNER_SHADOW 算，不用「描边错位」近似。
        //    做法 = 外框减去偏移后的形状得到环带，模糊后落在形状内侧即为内阴影；
        //    环带被形状自身 OpacityMask 裁掉外侧，因此任意复杂轮廓（PS5 的 P 形、键鼠细节）都不会糊死。
        var mask = new DrawingBrush(new GeometryDrawing(Brushes.White, null, At(0, 0)))
        {
            Stretch = Stretch.None,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, ActualWidth, ActualHeight),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, ActualWidth, ActualHeight)
        };

        var layer = new Grid { OpacityMask = mask };
        var frame = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));

        Geometry Ring(double dx, double dy)
        {
            var g = new CombinedGeometry(GeometryCombineMode.Exclude, frame, At(dx, dy));
            g.Freeze();
            return g;
        }

        // 上缘暗：offset (2.2,2.2) / radius 5 / #000 58%
        layer.Children.Add(new Path
        {
            Data = Ring(2.2, 2.2),
            Stretch = Stretch.None,
            Fill = new SolidColorBrush(Color.FromArgb(148, 0, 0, 0)),
            Effect = new BlurEffect { Radius = 4.0, RenderingBias = RenderingBias.Quality }
        });
        // 下缘亮：offset (-2,-2) / radius 3.2 / #FFF 26%
        layer.Children.Add(new Path
        {
            Data = Ring(-2.0, -2.0),
            Stretch = Stretch.None,
            Fill = new SolidColorBrush(Color.FromArgb(66, 255, 255, 255)),
            Effect = new BlurEffect { Radius = 2.6, RenderingBias = RenderingBias.Quality }
        });

        _root.Children.Add(layer);

        // 3) 边缘极轻柔化，对应设计稿 artFrame 的 LAYER_BLUR 1.4（过大会吃掉细节）
        _root.Effect = new BlurEffect { Radius = 0.8, RenderingBias = RenderingBias.Quality };
    }
}
