using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Threading;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using Visibility = System.Windows.Visibility;

namespace Pro2Plus.UI.Pages;

public partial class GyroPage : UserControl
{
    /// <summary>高级工具按钮，由外壳转发到旧逻辑。</summary>
    private bool _advancedOpen;
    private DispatcherTimer? _swapTimer;

    // 下方两块的位移层：折叠条被「顶」上去、展开卡从下方推上来。
    private readonly TranslateTransform _advSlide = new();
    private readonly TranslateTransform _barSlide = new();

    // 时序：先压缩/展开卡片，等主卡让出空间后再让另一块出现，避免上下两块在动画中途重叠。
    private const int MoveMs = 280;
    private const int FadeMs = 220;
    private const int BarFadeMs = 140;
    private const int ChevronMs = 180;
    private const int HandoverMs = 180;

    public GyroPage()
    {
        InitializeComponent();

        AdvancedCard.RenderTransform = _advSlide;
        AdvancedBar.RenderTransform = _barSlide;

        // 点击输入框以外的空白处让 TextBox 失焦，提交当前输入（即「取消输入」）。
        // Canvas 本身不接收键盘焦点，需显式清除，否则编辑态会一直停留在输入框。
        AdvancedCard.AddHandler(MouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnAdvancedCardMouseDown), true);
    }

    private void OnAdvancedCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点在输入框内（含其模板里的 ScrollViewer / Border）时不动焦点，保证可正常输入。
        for (DependencyObject? node = e.OriginalSource as DependencyObject; node != null;
             node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.TextBox) return;
        }
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) Keyboard.ClearFocus();
    }

    /// <summary>清掉动画时钟并写回本地值，避免上一次 HoldEnd 把属性锁死。</summary>
    private static void Reset(DependencyObject target, DependencyProperty prop, double value)
    {
        if (target is IAnimatable a) a.BeginAnimation(prop, null);
        target.SetValue(prop, value);
    }

    private static void Anim(DependencyObject target, DependencyProperty prop, double to,
        int ms, int delay = 0, EasingMode mode = EasingMode.EaseOut, Action? done = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
        // 只有真正需要错峰时才写 BeginTime；显式赋 null 会让时钟不启动（动画完全不动）
        if (delay > 0) anim.BeginTime = TimeSpan.FromMilliseconds(delay);
        anim.EasingFunction = new CubicEase { EasingMode = mode };
        if (done is not null) anim.Completed += (_, _) => done();
        if (target is IAnimatable animatable) animatable.BeginAnimation(prop, anim);
    }

    private void ScheduleSwitch(Action action)
    {
        _swapTimer?.Stop();
        _swapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HandoverMs) };
        _swapTimer.Tick += (_, _) =>
        {
            _swapTimer?.Stop();
            action();
        };
        _swapTimer.Start();
    }

    /// <summary>卡片高度与内部控件位置一起过渡，避免出现「卡先矮、内容后落」的错位。</summary>
    private void MoveCards(bool open, int delay)
    {
        double cardH = open ? 320 : 440;
        double sliderTop = open ? 180 : 291;
        double tickTop = open ? 200 : 320;
        double btnTop = open ? 256 : 376;
        double markLeft = open ? 232 : 172;
        double markSize = open ? 130 : 170;
        var mode = EasingMode.EaseInOut;

        Anim(CardGyro, FrameworkElement.HeightProperty, cardH, MoveMs, delay, mode);
        Anim(CardVibe, FrameworkElement.HeightProperty, cardH, MoveMs, delay, mode);

        Anim(SliderGyro, Canvas.TopProperty, sliderTop, MoveMs, delay, mode);
        Anim(TickGyro1, Canvas.TopProperty, tickTop, MoveMs, delay, mode);
        Anim(TickGyro2, Canvas.TopProperty, tickTop, MoveMs, delay, mode);
        Anim(TickGyro3, Canvas.TopProperty, tickTop, MoveMs, delay, mode);
        Anim(BtnGyroReset, Canvas.TopProperty, btnTop, MoveMs, delay, mode);

        Anim(SliderVibe, Canvas.TopProperty, sliderTop, MoveMs, delay, mode);
        Anim(TickVibe1, Canvas.TopProperty, tickTop, MoveMs, delay, mode);
        Anim(TickVibe2, Canvas.TopProperty, tickTop, MoveMs, delay, mode);
        Anim(TickVibe3, Canvas.TopProperty, tickTop, MoveMs, delay, mode);
        Anim(TickVibe4, Canvas.TopProperty, tickTop, MoveMs, delay, mode);
        Anim(TickVibe5, Canvas.TopProperty, tickTop, MoveMs, delay, mode);
        Anim(BtnVibeTry, Canvas.TopProperty, btnTop, MoveMs, delay, mode);

        Anim(MarkGyro, Canvas.LeftProperty, markLeft, MoveMs, delay, mode);
        Anim(MarkGyro, FrameworkElement.WidthProperty, markSize, MoveMs, delay, mode);
        Anim(MarkGyro, FrameworkElement.HeightProperty, markSize, MoveMs, delay, mode);
        Anim(MarkVibe, Canvas.LeftProperty, markLeft, MoveMs, delay, mode);
        Anim(MarkVibe, FrameworkElement.WidthProperty, markSize, MoveMs, delay, mode);
        Anim(MarkVibe, FrameworkElement.HeightProperty, markSize, MoveMs, delay, mode);
    }

    /// <summary>
    /// 展开 / 收起高级设置：内联替换而不是浮层。
    /// 方向上是「向上顶出」，所以折叠态箭头朝上、展开态箭头朝下，切换时做 180° 翻转。
    /// </summary>
    private void ApplyAdvanced(bool open)
    {
        if (_advancedOpen == open) return;
        _advancedOpen = open;

        if (open)
        {
            MoveCards(true, 0);
            Anim(BarChevronRotate, RotateTransform.AngleProperty, 180, ChevronMs, 0);

            // 折叠条被上面的卡「顶」走：淡出 + 微微上移，不做硬切
            Reset(AdvancedBar, UIElement.OpacityProperty, 1);
            Anim(AdvancedBar, UIElement.OpacityProperty, 0, BarFadeMs);
            Reset(_barSlide, TranslateTransform.YProperty, 0);
            Anim(_barSlide, TranslateTransform.YProperty, -12, BarFadeMs);

            // 等折叠条让位，再把展开卡从下方推上来（淡入 + 轻微上滑）
            ScheduleSwitch(() =>
            {
                AdvancedBar.BeginAnimation(UIElement.OpacityProperty, null);
                AdvancedBar.Visibility = Visibility.Collapsed;
                Reset(AdvancedBar, UIElement.OpacityProperty, 1);
                Reset(_barSlide, TranslateTransform.YProperty, 0);

                Reset(AdvancedCard, UIElement.OpacityProperty, 0);
                Reset(_advSlide, TranslateTransform.YProperty, 14);
                AdvancedCard.Visibility = Visibility.Visible;

                Anim(AdvancedCard, UIElement.OpacityProperty, 1, FadeMs, 0, EasingMode.EaseOut);
                Anim(_advSlide, TranslateTransform.YProperty, 0, FadeMs + 40, 0, EasingMode.EaseOut);
                Anim(CardChevronRotate, RotateTransform.AngleProperty, 0, ChevronMs, 0);
            });
        }
        else
        {
            // 展开卡先淡出 + 下沉让位，随后卡片才长回来
            Anim(AdvancedCard, UIElement.OpacityProperty, 0, BarFadeMs, 0, EasingMode.EaseOut);
            Anim(_advSlide, TranslateTransform.YProperty, 10, BarFadeMs, 0, EasingMode.EaseOut);
            Anim(CardChevronRotate, RotateTransform.AngleProperty, 180, ChevronMs, 0);
            MoveCards(false, HandoverMs - 20);

            ScheduleSwitch(() =>
            {
                AdvancedCard.BeginAnimation(UIElement.OpacityProperty, null);
                AdvancedCard.Visibility = Visibility.Collapsed;
                Reset(AdvancedCard, UIElement.OpacityProperty, 1);
                Reset(_advSlide, TranslateTransform.YProperty, 0);

                Reset(AdvancedBar, UIElement.OpacityProperty, 0);
                Reset(_barSlide, TranslateTransform.YProperty, 12);
                AdvancedBar.Visibility = Visibility.Visible;

                Anim(AdvancedBar, UIElement.OpacityProperty, 1, FadeMs, 0, EasingMode.EaseOut);
                Anim(_barSlide, TranslateTransform.YProperty, 0, FadeMs + 40, 0, EasingMode.EaseOut);
                Anim(BarChevronRotate, RotateTransform.AngleProperty, 0, ChevronMs, 0);
            });
        }
    }

    private void Advanced_Click(object sender, RoutedEventArgs e) => ApplyAdvanced(true);

    /// <summary>仅供冒烟截图校验展开态布局。</summary>
    public void OpenAdvancedForSmoke() => ApplyAdvanced(true);

    private void AdvancedClose_Click(object sender, RoutedEventArgs e) => ApplyAdvanced(false);

}
