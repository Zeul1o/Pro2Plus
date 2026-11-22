using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaPoint = System.Windows.Point;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using UserControl = System.Windows.Controls.UserControl;
using Visibility = System.Windows.Visibility;

namespace Pro2Plus.UI.Pages;

public partial class SettingsPage : UserControl
{
    /// <summary>高级工具按钮：参数是按钮 Tag，由外壳转发到主窗口旧逻辑。</summary>
    private MediaBrush? _maskBottom;   // 顶部不淡，底部渐隐
    private MediaBrush? _maskTop;      // 顶部渐隐，底部不淡
    private MediaBrush? _maskBoth;     // 上下都渐隐
    private string? _maskState;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateThumb();
            // 冒烟截图用：PRO2_UI_SMOKE_SCROLL=<偏移> 直接滚到指定位置，便于核对顶部渐隐
            if (int.TryParse(Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_SCROLL"), out int offset) && offset > 0)
            {
                ContentScroll.ScrollToVerticalOffset(offset);
                ContentScroll.UpdateLayout();
                UpdateThumb();
                RefreshMask();
                return;
            }
            RefreshMask();
        };
        Unloaded += (_, _) => SaveTuning();
    }

    /// <summary>
    /// 滚动时把内容上沿做成渐隐，避免在页头下沿出现硬切；未滚动时只保留底部渐隐。
    /// 掩码加在 ScrollContentPresenter 上（不随内容滚动，也不影响滚动条）。
    /// </summary>
    private void ContentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateThumb();
        RefreshMask();
    }

    /// <summary>把自绘滑块放到 6 → 710 的行程上；内容不足以滚动时隐藏。</summary>
    private void UpdateThumb()
    {
        double max = ContentScroll.ScrollableHeight;
        if (max <= 0.5 || ContentScroll.ViewportHeight <= 0)
        {
            ScrollThumb.Visibility = Visibility.Collapsed;
            return;
        }
        ScrollThumb.Visibility = Visibility.Visible;
        double travel = ThumbTravel;
        double ratio = Math.Min(1, Math.Max(0, ContentScroll.VerticalOffset / max));
        Canvas.SetTop(ScrollThumb, ThumbTop + ratio * travel);
        Canvas.SetLeft(ScrollThumb, Math.Max(0, BarLayer.Width - 27));
    }

    /// <summary>滑块行程：上下各留 6px，顶端就是顶端、底端就是底端。</summary>
    private const double ThumbTop = 6;
    private double ThumbTravel => Math.Max(1, ContentScroll.ViewportHeight - ScrollThumb.Height - ThumbTop * 2);

    private bool _thumbDragging;
    private double _dragStartY;
    private double _dragStartOffset;

    private void ScrollThumb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _thumbDragging = true;
        _dragStartY = e.GetPosition(ContentScroll).Y;
        _dragStartOffset = ContentScroll.VerticalOffset;
        ScrollThumb.CaptureMouse();
        e.Handled = true;
    }

    private void ScrollThumb_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_thumbDragging) return;
        double travel = ThumbTravel;
        double delta = e.GetPosition(ContentScroll).Y - _dragStartY;
        double target = _dragStartOffset + delta / travel * ContentScroll.ScrollableHeight;
        ContentScroll.ScrollToVerticalOffset(Math.Max(0, Math.Min(ContentScroll.ScrollableHeight, target)));
    }

    private void ScrollThumb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _thumbDragging = false;
        ScrollThumb.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ScrollThumb_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ContentScroll.ScrollToVerticalOffset(ContentScroll.VerticalOffset - Math.Sign(e.Delta) * 64);
        e.Handled = true;
    }

    /// <summary>
    /// 按位置决定哪一头渐隐：刚打开不淡顶端，滚到底不淡底端（底端要干净），
    /// 中间两头都淡，避免页头与页脚出现硬切。
    /// </summary>
    private void RefreshMask()
    {
        double max = ContentScroll.ScrollableHeight;
        bool atTop = ContentScroll.VerticalOffset <= 0.5;
        bool atBottom = max <= 0.5 || ContentScroll.VerticalOffset >= max - 0.5;

        string state = atTop && atBottom ? "none"
            : atTop ? "bottom"
            : atBottom ? "top"
            : "both";
        if (_maskState == state) return;
        _maskState = state;

        ContentScroll.OpacityMask = state switch
        {
            "bottom" => _maskBottom ??= BuildFadeMask(withTop: false, withBottom: true),
            "top" => _maskTop ??= BuildFadeMask(withTop: true, withBottom: false),
            "both" => _maskBoth ??= BuildFadeMask(withTop: true, withBottom: true),
            _ => null
        };
    }

    /// <summary>
    /// 视口高 722：顶部 48→168 渐显（120px，多段缓动），底部 734→770 渐隐。
    /// 两头是否参与按滚动位置决定，到底时底端不再淡。
    /// </summary>
    private static MediaBrush BuildFadeMask(bool withTop, bool withBottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new MediaPoint(0, 0),
            EndPoint = new MediaPoint(0, 1),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        if (withTop)
        {
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.000));
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.028));
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0x5C, 0xFF, 0xFF, 0xFF), 0.048));
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0xD2, 0xFF, 0xFF, 0xFF), 0.075));
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.090));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.000));
        }
        if (withBottom)
        {
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.950));
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.000));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(MediaColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 1.000));
        }
        brush.Freeze();
        return brush;
    }

    private void Step_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key } || DataContext is not MainViewModel vm) return;
        switch (key)
        {
            case "double-": vm.DoubleTapIntervalMs -= 10; break;
            case "double+": vm.DoubleTapIntervalMs += 10; break;
            case "double-reset": vm.DoubleTapIntervalMs = 100; break;
            case "hold-": vm.HoldThresholdMs -= 25; break;
            case "hold+": vm.HoldThresholdMs += 25; break;
            case "hold-reset": vm.HoldThresholdMs = 250; break;
            case "combo-": vm.ComboWindowMs -= 10; break;
            case "combo+": vm.ComboWindowMs += 10; break;
            case "combo-reset": vm.ComboWindowMs = 150; break;
            // 死区按 0.2% 一档（4 个原始单位），微调不会一跳一大截。
            case "wired-dz-": vm.WiredStickDeadzoneRaw -= 4; break;
            case "wired-dz+": vm.WiredStickDeadzoneRaw += 4; break;
            case "wired-dz-reset": vm.WiredStickDeadzoneRaw = AppSettings.DefaultWiredStickDeadzone; break;
            case "ble-dz-": vm.BleStickDeadzoneRaw -= 4; break;
            case "ble-dz+": vm.BleStickDeadzoneRaw += 4; break;
            case "ble-dz-reset": vm.BleStickDeadzoneRaw = AppSettings.DefaultBleStickDeadzone; break;
            case "ps5-": vm.Ps5GyroSensitivity -= 0.05; break;
            case "ps5+": vm.Ps5GyroSensitivity += 0.05; break;
            case "ps5-reset": vm.Ps5GyroSensitivity = 1.0; break;
            case "pro1-": vm.Pro1GyroSensitivity -= 0.05; break;
            case "pro1+": vm.Pro1GyroSensitivity += 0.05; break;
            case "pro1-reset": vm.Pro1GyroSensitivity = 1.0; break;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveTuning();

    private void SaveTuning()
    {
        if (DataContext is MainViewModel vm && vm.SaveTuningCommand.CanExecute(null))
            vm.SaveTuningCommand.Execute(null);
    }
}
