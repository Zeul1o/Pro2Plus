using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pro2Plus.Mapping;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using Border = System.Windows.Controls.Border;
using Canvas = System.Windows.Controls.Canvas;
using Ellipse = System.Windows.Shapes.Ellipse;
using WpfImage = System.Windows.Controls.Image;
using UserControl = System.Windows.Controls.UserControl;
using RoutedEventArgs = System.Windows.RoutedEventArgs;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using BlurEffect = System.Windows.Media.Effects.BlurEffect;
using Visibility = System.Windows.Visibility;
using Thickness = System.Windows.Thickness;

namespace Pro2Plus.UI.Pages;

/// <summary>
/// 按键映射页。左半是手柄热点（按下响应）、右半是当前源键的映射详情；
/// 键鼠模式下换成屏幕键盘 + 23 个源键。
///
/// 数据来自 <see cref="MappingProfile"/>：点热点/胶囊/源键选中源键，
/// 点「修改」在同一张卡内切到目标列表（不弹层），选完立即写进内存表，
/// 底部「保存并应用」落盘到 我的文档\Pro2+\bindings.json（见 AppPaths）。
/// </summary>
public partial class BindingsPage : UserControl
{
    private const double IW = 448, IH = 315, IX = 24, IY = 90;
    private static readonly Color Accent = Color.FromRgb(0x5C, 0x9C, 0xFF);

    // 手柄图原始像素尺寸：Stretch=Uniform 时按它换算真实渲染区域，避免 PS5 宽高比不同导致错位。
    private static (double w, double h) PadPixels(string layout) => layout switch
    {
        "xbox" => (1001, 703),
        "ps5" => (1001, 678),
        _ => (1000, 702)   // switch_pro
    };

    // name / nx / ny / diameter(相对线稿宽) —— 全部由 Figma「手柄线稿」里的热点节点直接抓取，
    // 归一化到线稿矩形。线稿矩形与本地 PNG 是同一张图，逐点校验偏移 < 5px。
    // n 必须与模型图上的丝印字母一致：Switch Pro 是 X 上 / Y 左 / A 右 / B 下。
    // 亮键高亮按这里的位置画，所以「输出 B」就会亮图上标着 B 的那颗，不会跑到 A 的圈上。
    private static readonly (string n, double x, double y, double d)[] SwitchSpots =
    {
        ("L3", .2220, .2990, .1150), ("R3", .6300, .4960, .1150), ("十字键", .3480, .4950, .1420),
        ("X", .7630, .2020, .0700), ("Y", .6850, .3010, .0700), ("A", .8410, .3000, .0700), ("B", .7620, .3900, .0700),
        ("－", .3770, .1920, .0460), ("＋", .6220, .1920, .0460)
    };
    private static readonly (string n, double x, double y, double d)[] XboxSpots =
    {
        ("L3", .2440, .2750, .1080), ("R3", .6320, .4910, .1080), ("十字键", .3710, .5010, .1440),
        ("Y", .7600, .1770, .0680), ("X", .6900, .2780, .0680), ("B", .8270, .2780, .0680), ("A", .7600, .3760, .0680),
        ("视图", .4300, .2700, .0460), ("菜单", .5710, .2700, .0460)
    };
    private static readonly (string n, double x, double y, double d)[] Ps5Spots =
    {
        ("L3", .3380, .4660, .1080), ("R3", .6640, .4690, .1080), ("十字键", .1870, .2590, .1680),
        ("△", .8170, .1650, .0680), ("□", .7490, .2650, .0680), ("○", .8840, .2650, .0680), ("×", .8170, .3650, .0680),
        ("PS 键", .5020, .4630, .0600), ("触摸板", .5000, .1480, .0600)
    };

    private static readonly (string k, double sp)[][] KeyRows =
    {
        new[] { ("Esc",1.0), ("`",1.0), ("1",1.0), ("2",1.0), ("3",1.0), ("4",1.0), ("5",1.0), ("6",1.0),
                ("7",1.0), ("8",1.0), ("9",1.0), ("0",1.0), ("-",1.0), ("=",1.0), ("⌫",1.0) },
        new[] { ("Tab",1.5), ("Q",1.0), ("W",1.0), ("E",1.0), ("R",1.0), ("T",1.0), ("Y",1.0), ("U",1.0),
                ("I",1.0), ("O",1.0), ("P",1.0), ("[",1.0), ("]",1.0), ("\\",1.5) },
        new[] { ("Caps",1.75), ("A",1.0), ("S",1.0), ("D",1.0), ("F",1.0), ("G",1.0), ("H",1.0),
                ("J",1.0), ("K",1.0), ("L",1.0), (";",1.0), ("'",1.0), ("Enter",2.25) },
        new[] { ("Shift",2.25), ("Z",1.0), ("X",1.0), ("C",1.0), ("V",1.0), ("B",1.0), ("N",1.0),
                ("M",1.0), (",",1.0), (".",1.0), ("/",1.0), ("Shift",2.75) },
        new[] { ("Ctrl",1.25), ("Win",1.0), ("Alt",1.25), ("",6.0), ("Alt",1.25), ("Ctrl",1.25),
                ("←",1.0), ("↓",1.0), ("→",1.0) }
    };

    private static readonly string[][] SourceRows =
    {
        new[] { "A", "B", "X", "Y", "↑", "↓", "←", "→" },
        new[] { "L", "R", "ZL", "ZR", "L3", "R3", "＋", "－" },
        new[] { "截图", "Home", "GL", "GR", SourceKeys.LeftStick, SourceKeys.RightStick }
    };


    private INotifyPropertyChanged? _vm;
    private MappingProfile _profile = MappingProfile.CreateDefault();
    private MappingLibrary _library = MappingLibrary.CreateDefault();
    private ProfileSet _set = null!;
    private string _boundMode = "";
    private string _layout = "pro2";        // 输出平台布局（决定文案/图标：xbox / ps5 / pro2）
    private string _artLayout = "pro2";     // 线稿布局（决定亮图与热点，等于「物理布局」开关）
    private string _appliedModeLayout = "";
    private string _appliedOutputMode = "";
    private string _builtHighlightsFor = "";

    private string _padSource = "GL";
    private string _kbmSource = "左摇杆";
    private BindingTrigger _kbmTrigger = BindingTrigger.Tap;
    private string _comboPartner = "";
    private bool _pickingPartner;
    // 右卡当前停在「更多动作」页
    private bool _moreOpen;

    private bool _ready;
    private bool _capturing;
    // 手柄模型图：按虚拟手柄最终输出实时点亮按键（50ms 轮询）。
    private readonly DispatcherTimer _padHighlightTimer = new(DispatcherPriority.Background)
    { Interval = TimeSpan.FromMilliseconds(50) };
    private ulong _debounceCandidate, _stableButtons;
    // 瞬时按下 / 延迟松开：按下立刻采纳，松开要持续 100ms，滤掉长按时的单帧丢失。
    private long _debounceSince;

    // 摇杆位置点常驻元素：只在布局切换时重建，之后只改坐标/可见性。
    // 之前每次轮询都 Clear 整个高亮层，轴值抖动会反复重建带 BlurEffect 的元素，
    // 视觉上就是「长按周期闪烁」。按钮光环与摇杆点分层后互不干扰。
    private readonly Ellipse?[] _stickGlow = new Ellipse?[2];
    private readonly Ellipse?[] _stickDot = new Ellipse?[2];
    private (double cx, double cy, double d)[] _stickGeom = new (double, double, double)[2];

    public BindingsPage()
    {
        InitializeComponent();
        Focusable = true;
        Loaded += (_, _) =>
        {
            _ready = true;
            _library = MappingLibrary.Load();
            BindActiveProfile();
            RefreshProfileBar();
            (DataContext as MainViewModel)?.RefreshMappingRuntime();
            Hook();
            BuildKeyboard();
            BuildSources();
            ApplyMode();
            RefreshPadCard();
            RefreshKbmCard();

            // 截图核对用：强制打开目标列表。放到模式切换之后再执行，
            // 否则刚打开就被 ApplyMode 关掉。
            if (Environment.GetEnvironmentVariable("PRO2_MAP_PICK") == "1")
                Dispatcher.BeginInvoke(new Action(ShowPickView), DispatcherPriority.Background);
            if (Environment.GetEnvironmentVariable("PRO2_MAP_RENAME") == "1")
                Dispatcher.BeginInvoke(new Action(() => BeginProfileRename(IsKbm ? KbmProfileName : ProfileName)),
                    DispatcherPriority.Background);
            if (Environment.GetEnvironmentVariable("PRO2_MAP_PROFILE") == "1")
                ProfileList_Click(this, new RoutedEventArgs());
            // 截图核对用：临时塞若干套配置，看列表滚动条（只改内存，不落盘）
            if (int.TryParse(Environment.GetEnvironmentVariable("PRO2_MAP_PROF_SEED"), out int seed) && seed > 0)
            {
                for (int i = 1; i <= seed; i++) _set.Add("测试配置 " + i, _profile.Clone());
                _profile = _set.Current.Bindings;
                RefreshProfileBar();
                RefreshPadCard();
                RefreshKbmCard();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ProfileList_Click(this, new RoutedEventArgs());
                }), DispatcherPriority.Background);
            }
            // 截图核对用：键鼠页指定源键 / 直接打开目标选择
            string? smokeKbmSource = Environment.GetEnvironmentVariable("PRO2_MAP_KBM");
            if (!string.IsNullOrWhiteSpace(smokeKbmSource))
            {
                _kbmSource = smokeKbmSource;
                BuildSources();
                UpdateKbmOutput();
            }
            if (Environment.GetEnvironmentVariable("PRO2_MAP_KBM_PICK") == "1")
                Dispatcher.BeginInvoke(new Action(ShowKbmPick), DispatcherPriority.Background);
            if (Environment.GetEnvironmentVariable("PRO2_MAP_KBM_PARTNER") == "1")
                Dispatcher.BeginInvoke(new Action(ShowKbmPartnerPick), DispatcherPriority.Background);
            if (Environment.GetEnvironmentVariable("PRO2_MAP_KBM_COMBO") == "1")
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _moreOpen = true;
                    _kbmTrigger = BindingTrigger.Combo;
                    _comboPartner = ResolvePartner();
                    RefreshKbmSlots();
                    ShowKbmPick();
                }), DispatcherPriority.Background);
            if (Environment.GetEnvironmentVariable("PRO2_MAP_KBM_MORE") == "1")
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _moreOpen = true;
                    _kbmTrigger = BindingTrigger.Tap;
                    RefreshKbmSlots();
                }), DispatcherPriority.Background);
            _padHighlightTimer.Tick += (_, _) => RefreshPadHighlight();
            _padHighlightTimer.Start();

            // 配置列表滚动时同步自绘滑块
            KbmProfileScroll.ScrollChanged += (_, _) => UpdateKbmProfileThumb();
            PadProfileScroll.ScrollChanged += (_, _) => UpdatePadProfileThumb();
        };
        Unloaded += (_, _) => _padHighlightTimer.Stop();
        DataContextChanged += (_, _) => { Hook(); if (_ready) ApplyMode(); };
        PreviewKeyDown += BindingsPage_PreviewKeyDown;
    }

    private void Hook()
    {
        if (_vm != null) _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = DataContext as INotifyPropertyChanged;
        if (_vm != null) _vm.PropertyChanged += Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "IsPro2Selected" or "IsXboxSelected" or "CompactPs5Selected" or "IsKeyMouseSelected")
            ApplyMode();
    }

    private bool IsKbm => (DataContext as MainViewModel)?.IsKeyMouseSelected ?? false;

    private void ApplyMode()
    {
        if (!_ready) return;
        var vm = DataContext as MainViewModel;
        bool kbm = vm?.IsKeyMouseSelected ?? false;
        bool xbox = vm?.IsXboxSelected ?? false;
        bool ps5 = vm?.CompactPs5Selected ?? false;

        KbmView.Visibility = kbm ? Visibility.Visible : Visibility.Collapsed;
        PadView.Visibility = kbm ? Visibility.Collapsed : Visibility.Visible;
        SyncProfileForMode();

        // 只有真的换了输出模式才重置视图，避免属性通知重复到达时把用户正在看的界面关掉。
        bool modeChanged = OutputMode != _appliedOutputMode;
        _appliedOutputMode = OutputMode;

        if (modeChanged && !kbm)
        {
            PadPickView.Visibility = Visibility.Collapsed;
            PadProfileView.Visibility = Visibility.Collapsed;
            PadEditView.Visibility = Visibility.Visible;
            SetProfileBarVisible(true);
        }
        else if (modeChanged)
        {
            KbmProfileView.Visibility = Visibility.Collapsed;
            HideKbmPick();
        }

        if (kbm)
        {
            UpdateKbmOutput();
            return;
        }

        // 线稿永远跟着输出平台走；ABXY 开关只改「按下哪颗发哪个字母」，不换图。
        string face = ps5 ? "xbox" : CurrentFaceLayout();
        string nextLayout = xbox ? "xbox" : (ps5 ? "ps5" : "pro2");
        _layout = nextLayout;
        PadMappingEngine.SetFaceLayout(face);
        PadMappingEngine.SetPlatform(_layout);

        // 模式属性通知可能重复到达；布局没变就不要重设图片/重建高亮，
        // 否则每次都会 Clear 并重设 BitmapImage，表现为整张手柄图和高亮一起闪。
        if (nextLayout == _appliedModeLayout && PadImage.Source != null)
        {
            RefreshPadCard();
            return;
        }
        _appliedModeLayout = nextLayout;
        _artLayout = nextLayout;
        string pad = nextLayout == "xbox" ? "xbox" : nextLayout == "ps5" ? "ps5" : "switch_pro";
        PadImage.Source = new BitmapImage(new Uri(
            "pack://application:,,,/Assets/Handhelds/" + pad + ".png", UriKind.Absolute));
        BuildHotspots(nextLayout == "xbox" ? XboxSpots : (nextLayout == "ps5" ? Ps5Spots : SwitchSpots), _artLayout);

        LayoutSeg.Visibility = ps5 ? Visibility.Collapsed : Visibility.Visible;
        ApplyLayout();
        RefreshPadCard();
    }

    // ── 配置库 ────────────────────────────────────────────────────

    /// <summary>当前输出模式对应的配置键；手柄页的物理布局开关不改变它。</summary>
    private string OutputMode
    {
        get
        {
            var vm = DataContext as MainViewModel;
            if (vm is null) return "xbox";
            if (vm.IsKeyMouseSelected) return "kbm";
            return vm.CompactPs5Selected ? "ps5" : "xbox";
        }
    }

    private void BindActiveProfile()
    {
        _boundMode = OutputMode;
        _set = _library.For(_boundMode);
        _profile = _set.Current.Bindings;
    }

    /// <summary>模式切换后重新绑定（每个模式各自记住自己的活动配置）。</summary>
    private void SyncProfileForMode()
    {
        if (!_ready) return;
        string mode = OutputMode;
        if (mode == _boundMode) return;
        _library.For(_boundMode).Current.Bindings = _profile;   // 先把草稿写回原模式
        BindActiveProfile();
        RefreshProfileBar();
        RefreshPadCard();
        RefreshKbmCard();
    }

    /// <summary>只吃点击、不画任何东西的按钮模板（配置行整行可点）。</summary>
    private static ControlTemplate TransparentTemplate()
    {
        const string xaml =
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
            "<Border Background='Transparent'/></ControlTemplate>";
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private void RefreshProfileBar()
    {
        ProfileName.Text = _set.Active;
        ProfileName.ToolTip = _set.Profiles.Count + " 套配置";
        KbmProfileName.Text = _set.Active;
        KbmProfileName.ToolTip = _set.Profiles.Count + " 套配置";
        string modeLabel = (DataContext as MainViewModel)?.SelectedModeLabel ?? _boundMode;
        ProfileSubtitle.Text = modeLabel + " · " + _set.Profiles.Count + " 套";
        KbmProfileSubtitle.Text = modeLabel + " · " + _set.Profiles.Count + " 套";
        if (PadProfileView.Visibility == Visibility.Visible) BuildProfileList(ProfileListHost, 228);
        if (KbmProfileView.Visibility == Visibility.Visible) BuildProfileList(KbmProfileListHost, 360);
    }

    private void ProfilePrev_Click(object sender, RoutedEventArgs e) => StepProfile(-1);
    private void ProfileNext_Click(object sender, RoutedEventArgs e) => StepProfile(1);

    private void StepProfile(int delta)
    {
        if (_set.Profiles.Count < 2) return;
        int index = _set.Profiles.FindIndex(p => p.Name == _set.Active);
        int next = (index + delta + _set.Profiles.Count) % _set.Profiles.Count;
        _set.Active = _set.Profiles[next].Name;
        _profile = _set.Current.Bindings;
        RefreshProfileBar();
        RefreshPadCard();
        RefreshKbmCard();
        MarkDirty();
    }

    private void ProfileList_Click(object sender, RoutedEventArgs e)
    {
        // 键鼠模式的配置管理铺在键盘卡上，空间比手柄模式的窄卡宽裕。
        if (IsKbm)
        {
            if (KbmProfileView.Visibility == Visibility.Visible) { KbmProfileBack_Click(sender, e); return; }
            HideKbmPick();
            BuildProfileList(KbmProfileListHost, 360);
            RefreshProfileBar();
            SetManageActive(true);
            KbmProfileView.Visibility = Visibility.Visible;
            KeyboardHost.Visibility = Visibility.Collapsed;
            MouseOutView.Visibility = Visibility.Collapsed;
            KbmTitle.Visibility = Visibility.Collapsed;
            KbmOutputLabel.Visibility = Visibility.Collapsed;
            KbmProfileScroll.ScrollToTop();
            Dispatcher.BeginInvoke(new Action(UpdateKbmProfileThumb), DispatcherPriority.Background);
            return;
        }

        if (PadProfileView.Visibility == Visibility.Visible) { ProfileBack_Click(sender, e); return; }
        BuildProfileList(ProfileListHost, 228);
        SetManageActive(true);
        PadEditView.Visibility = Visibility.Collapsed;
        PadPickView.Visibility = Visibility.Collapsed;
        PadProfileView.Visibility = Visibility.Visible;
        SetProfileBarVisible(false);
        RefreshProfileBar();
    }

    private void KbmProfileBack_Click(object sender, RoutedEventArgs e)
    {
        KbmProfileView.Visibility = Visibility.Collapsed;
        SetManageActive(false);
        UpdateKbmOutput();
    }

    /// <summary>管理按钮的按下态：开了管理页就常亮且不可再按，返回后复位。</summary>
    private void SetManageActive(bool on)
    {
        Style style = (Style)FindResource(on ? "StepButtonOn" : "StepButton");
        foreach (Button b in new[] { ProfileMore, KbmProfileMore })
        {
            b.Style = style;
            b.IsEnabled = !on;
        }
    }

    private void ProfileBack_Click(object sender, RoutedEventArgs e)
    {
        PadProfileView.Visibility = Visibility.Collapsed;
        PadEditView.Visibility = Visibility.Visible;
        SetProfileBarVisible(true);
        SetManageActive(false);
    }

    private void BuildProfileList(Canvas host, double width)
    {
        host.Children.Clear();
        const double rh = 40, gap = 6;
        int row = 0;
        foreach (ProfileEntry entry in _set.Profiles)
        {
            bool on = entry.Name == _set.Active;
            var card = new ContentControl
            {
                Width = width, Height = rh,
                Style = (Style)FindResource(on ? "CardSelected" : "CardSoft")
            };
            var canvas = new Canvas();
            canvas.Children.Add(new TextBlock
            {
                Text = entry.Name, FontSize = 12,
                Foreground = (SolidColorBrush)FindResource(on ? "T1" : "T3"),
                FontWeight = on ? FontWeights.Bold : FontWeights.Normal,
                Width = width - 60, LineHeight = 18
            });
            Canvas.SetLeft(canvas.Children[0], 14);
            Canvas.SetTop(canvas.Children[0], 11);

            var pick = new Button { Width = width - 38, Height = rh, Tag = entry.Name, Cursor = Cursors.Hand };
            pick.Template = TransparentTemplate();
            pick.Click += ProfilePick_Click;
            // 双击名字就地改名（配置页上没有上方那颗配置芯片，只能在这里改）
            string rowName = entry.Name;
            pick.MouseDoubleClick += (_, args) =>
            {
                args.Handled = true;
                BeginRowRename(canvas, rowName, width, host);
            };
            canvas.Children.Add(pick);

            if (_set.Profiles.Count > 1)
            {
                var del = new Button
                {
                    Content = "×", Width = 24, Height = 24, FontSize = 12,
                    Style = (Style)FindResource("StepButton"), Tag = entry.Name, Cursor = Cursors.Hand
                };
                del.Click += ProfileDelete_Click;
                Canvas.SetLeft(del, width - 32);
                Canvas.SetTop(del, 8);
                canvas.Children.Add(del);
            }

            card.Content = canvas;
            Canvas.SetTop(card, row * (rh + gap));
            host.Children.Add(card);
            row++;
        }

        // 列表比可视区高时才有得滚，高度写回 Canvas 让 ScrollViewer 拿到真实 extent。
        host.Height = Math.Max(host == KbmProfileListHost ? 196 : 292, row * (rh + gap) - gap);
        if (host == KbmProfileListHost)
        {
            KbmProfileScroll.ScrollToTop();
            UpdateKbmProfileThumb();
        }
        else
        {
            PadProfileScroll.ScrollToTop();
            Dispatcher.BeginInvoke(new Action(UpdatePadProfileThumb), DispatcherPriority.Background);
        }
    }

    // ── 配置列表滚动条（与设置页同一套自绘玻璃滑块）──────────────────
    private bool _thumbDragging;
    private double _thumbDragY;
    private double _thumbDragOffset;

    /// <summary>滑块事件是共用的：按 sender 找回它所属的滚动区。</summary>
    private (ScrollViewer Scroll, FrameworkElement Thumb) ThumbPair(object sender) =>
        ReferenceEquals(sender, KbmProfileThumb)
            ? (KbmProfileScroll, KbmProfileThumb)
            : (PadProfileScroll, PadProfileThumb);

    private void UpdateProfileThumb(ScrollViewer scroll, FrameworkElement thumb)
    {
        double viewport = scroll.ViewportHeight > 0 ? scroll.ViewportHeight : scroll.Height;
        double extent = scroll.ExtentHeight;
        if (extent <= viewport + 0.5)
        {
            thumb.Visibility = Visibility.Collapsed;
            return;
        }

        thumb.Visibility = Visibility.Visible;
        double h = Math.Max(48, viewport * viewport / extent);
        thumb.Height = h;
        double travel = Math.Max(1, viewport - h);
        double scrollable = Math.Max(1, extent - viewport);
        Canvas.SetTop(thumb, scroll.VerticalOffset / scrollable * travel);
    }

    private void UpdateKbmProfileThumb() => UpdateProfileThumb(KbmProfileScroll, KbmProfileThumb);
    private void UpdatePadProfileThumb() => UpdateProfileThumb(PadProfileScroll, PadProfileThumb);

    private void ProfileThumb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        (ScrollViewer scroll, FrameworkElement thumb) = ThumbPair(sender);
        _thumbDragging = true;
        _thumbDragY = e.GetPosition(scroll).Y;
        _thumbDragOffset = scroll.VerticalOffset;
        thumb.CaptureMouse();
        e.Handled = true;
    }

    private void ProfileThumb_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_thumbDragging) return;
        (ScrollViewer scroll, FrameworkElement thumb) = ThumbPair(sender);
        double viewport = scroll.ViewportHeight > 0 ? scroll.ViewportHeight : scroll.Height;
        double travel = Math.Max(1, viewport - thumb.Height);
        double delta = e.GetPosition(scroll).Y - _thumbDragY;
        double target = _thumbDragOffset + delta / travel * scroll.ScrollableHeight;
        scroll.ScrollToVerticalOffset(Math.Max(0, Math.Min(scroll.ScrollableHeight, target)));
    }

    private void ProfileThumb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        (_, FrameworkElement thumb) = ThumbPair(sender);
        _thumbDragging = false;
        thumb.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ProfileThumb_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        (ScrollViewer scroll, _) = ThumbPair(sender);
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset - Math.Sign(e.Delta) * 64);
        e.Handled = true;
    }

    /// <summary>配置列表行内改名：双击名字进编辑，Enter 提交，Esc 取消，失焦按提交处理。</summary>
    private void BeginRowRename(Canvas canvas, string current, double width, Canvas host)
    {
        if (canvas.Children.OfType<System.Windows.Controls.TextBox>().Any()) return;
        if (canvas.Children.Count == 0 || canvas.Children[0] is not TextBlock label) return;

        var box = new System.Windows.Controls.TextBox
        {
            Text = current, Width = width - 60, Height = 22, FontSize = 12,
            Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = (SolidColorBrush)FindResource("T1"),
            CaretBrush = (SolidColorBrush)FindResource("Accent"),
            Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center
        };
        Canvas.SetLeft(box, 14);
        Canvas.SetTop(box, 8);
        canvas.Children.Remove(label);
        canvas.Children.Add(box);   // 加到末尾：盖在那颗透明按钮上面，否则点不到

        bool done = false;
        void Finish(bool save)
        {
            if (done) return;
            done = true;
            string wanted = box.Text.Trim();
            if (save && wanted.Length > 0 && wanted != current)
            {
                _set.Rename(current, wanted);
                RefreshProfileBar();
                MarkDirty();
            }
            BuildProfileList(host, width);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostFocus += (_, _) => Finish(true);
        box.Focus();
        box.SelectAll();
    }

    private void ProfilePick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name) return;
        _set.Active = name;
        _profile = _set.Current.Bindings;
        RefreshProfileBar();
        RefreshPadCard();
        RefreshKbmCard();
        MarkDirty();
    }

    private void ProfileDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name) return;
        _set.Remove(name);
        _profile = _set.Current.Bindings;
        RefreshProfileBar();
        BindActiveProfile();
        RefreshPadCard();
        RefreshKbmCard();
        MarkDirty();
    }

    private void ProfileCreate_Click(object sender, RoutedEventArgs e) => CreateProfile();
    private void KbmProfileCreate_Click(object sender, RoutedEventArgs e) => CreateProfile();

    /// <summary>新建：复制当前这套，名字自动编号，之后双击配置名就能改。</summary>
    private void CreateProfile()
    {
        _set.Add("配置", _profile.Clone());
        _profile = _set.Current.Bindings;
        RefreshProfileBar();
        RefreshPadCard();
        RefreshKbmCard();
        MarkDirty();
    }

    // ── 配置名内联重命名：双击配置芯片进入编辑，Enter / 失焦提交，Esc 取消 ──
    private System.Windows.Controls.TextBox? _renamingBox;

    private void ProfileChip_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;          // 单击仍交给下面的列表按钮
        e.Handled = true;
        BeginProfileRename(ReferenceEquals(sender, KbmProfileChip) ? KbmProfileName : ProfileName);
    }

    private void BeginProfileRename(System.Windows.Controls.TextBox box)
    {
        _renamingBox = box;
        box.IsHitTestVisible = true;
        box.IsReadOnly = false;
        box.Focus();
        box.SelectAll();
    }

    private void ProfileName_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitProfileRename(box);
            Keyboard.ClearFocus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            box.Text = _set.Active;
            EndProfileRename(box);
        }
    }

    private void ProfileName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box) CommitProfileRename(box);
    }

    private void CommitProfileRename(System.Windows.Controls.TextBox box)
    {
        if (!ReferenceEquals(_renamingBox, box)) return;
        string wanted = box.Text.Trim();
        EndProfileRename(box);
        if (wanted.Length == 0 || wanted == _set.Active) { box.Text = _set.Active; return; }
        _set.Rename(_set.Active, wanted);
        RefreshProfileBar();
        MarkDirty();
    }

    private void EndProfileRename(System.Windows.Controls.TextBox box)
    {
        _renamingBox = null;
        box.IsReadOnly = true;
        box.IsHitTestVisible = false;
    }

    // ── 手柄视图：热点与源键选择 ───────────────────────────────────

    /// <summary>
    /// 把 PNG 自身比例的 hotspot 坐标换算到卡片 Canvas 坐标。
    /// 关键：PadImage 用 Stretch=Uniform，PNG 在 448×315 框里等比缩放并居中，
    /// 不同布局宽高比不同（PS5 更宽），必须按真实渲染区域换算，否则高亮整体偏移。
    /// </summary>
    private static (double cx, double cy, double d) SpotRect(
        string layout, (string n, double x, double y, double d) s)
    {
        var (pw, ph) = PadPixels(layout);
        double scale = Math.Min(IW / pw, IH / ph);
        double drawW = pw * scale, drawH = ph * scale;
        double offX = IX + (IW - drawW) / 2;
        double offY = IY + (IH - drawH) / 2;
        return (offX + s.x * drawW, offY + s.y * drawH, s.d * drawW);
    }

    /// <summary>
    /// 手柄模型图纯展示：不再生成任何可点击热点，也不响应鼠标。
    /// 高亮完全由 RefreshPadHighlight 读取虚拟手柄输出驱动；鼠标只用于下方 GL/GR/截图/Home 芯片。
    /// </summary>
    private void BuildHotspots((string n, double x, double y, double d)[] spots, string layout)
    {
        HotspotLayer.Children.Clear(); // 保持层存在，但不再放入任何可命中元素
    }

    /// <summary>热点显示名 → 虚拟手柄输出按键位（用于按最终输出点亮模型图）。</summary>
    private static GamepadButtons SpotButton(string spot, string layout) => spot switch
    {
        // 面键按线稿丝印的位置走：Xbox/PS5 是 下=A/×、右=B/○；NS 线稿是 下=B、右=A。
        "A" => layout == "pro2" ? GamepadButtons.East : GamepadButtons.South,
        "B" => layout == "pro2" ? GamepadButtons.South : GamepadButtons.East,
        "X" => layout == "pro2" ? GamepadButtons.North : GamepadButtons.West,
        "Y" => layout == "pro2" ? GamepadButtons.West : GamepadButtons.North,
        "×" => GamepadButtons.South,
        "○" => GamepadButtons.East,
        "□" => GamepadButtons.West,
        "△" => GamepadButtons.North,
        "L3" => GamepadButtons.LeftStick,
        "R3" => GamepadButtons.RightStick,
        "十字键" => GamepadButtons.DPadDown | GamepadButtons.DPadRight |
                    GamepadButtons.DPadLeft | GamepadButtons.DPadUp,
        "－" or "视图" => GamepadButtons.Back,
        "＋" or "菜单" => GamepadButtons.Start,
        "Home" or "PS 键" => GamepadButtons.Home,
        "触摸板" => GamepadButtons.Capture,   // PS5 触摸板按下在虚拟手柄里就是 Capture 位
        _ => GamepadButtons.None
    };

    /// <summary>一个热点的常驻高亮元素：只切可见性，绝不在轮询里 Clear/重建。</summary>
    private sealed class SpotVisual
    {
        public string Name { get; }
        public GamepadButtons Bits { get; }
        public UIElement[] Parts { get; }
        public bool Shown { get; set; }
        public SpotVisual(string name, GamepadButtons bits, params UIElement[] parts)
        { Name = name; Bits = bits; Parts = parts; }
        public void Show(bool on)
        {
            if (Shown == on) return;
            Shown = on;
            Visibility v = on ? Visibility.Visible : Visibility.Collapsed;
            foreach (var p in Parts) p.Visibility = v;
        }
    }
    private readonly List<SpotVisual> _spotVisuals = new();

    private OutputPadSnapshot _lastSnap;

    /// <summary>
    /// 瞬时按下 / 延迟松开：新键位一出现立刻采纳（轻按零延迟）；
    /// 只有「松开」才要求持续 100ms，滤掉长按过程中偶发的单帧丢失，不再闪。
    /// </summary>
    private static ulong Debounce(ulong current, ref ulong stable, ref ulong candidate, ref long since)
    {
        if (current == stable) return stable;
        if ((current & ~stable) != 0)
        {
            stable = current;
            candidate = current;
            since = Environment.TickCount64;
        }
        else if (current == candidate)
        {
            if (Environment.TickCount64 - since >= 100) stable = current;
        }
        else
        {
            candidate = current;
            since = Environment.TickCount64;
        }
        return stable;
    }

    /// <summary>
    /// 按虚拟手柄最终输出实时点亮模型图。所有高亮元素在布局切换时一次性建好，
    /// 轮询只做两件事：切换可见性、移动摇杆点。既不重建元素也不受轴抖动影响。
    /// </summary>
    private void RefreshPadHighlight()
    {
        if (!_ready || !IsVisible || PadView.Visibility != Visibility.Visible) return;

        var vm = DataContext as MainViewModel;
        OutputPadSnapshot snap = vm?.VirtualOutput ?? new OutputPadSnapshot(0, 2048, 2048, 2048, 2048);
        _lastSnap = snap;

        // 点亮口径 = 最终输出（映射之后）。这样 GL/GR/C 这类不在图上的键，
        // 映射到 L3/A/… 时图上的那颗也会跟着亮，而不是什么都不显示。
        ulong stableOut = Debounce(snap.Buttons, ref _stableButtons, ref _debounceCandidate, ref _debounceSince);

        foreach (SpotVisual v in _spotVisuals)
        {
            bool on = v.Bits != GamepadButtons.None && ((ulong)v.Bits & stableOut) != 0;
            v.Show(on);
        }

        // 「按下一个键」的大标识：实时显示这一下最终输出成什么键（含模型上看不到的扳机）
        UpdateLiveBadge(stableOut, snap.Lt, snap.Rt);

        // 摇杆点：中心死区 + 粗量化，只移动常驻元素。
        UpdateStickIndicator(0, QuantizeAxis(snap.Lx), QuantizeAxis(snap.Ly));
        UpdateStickIndicator(1, QuantizeAxis(snap.Rx), QuantizeAxis(snap.Ry));
    }

    private string _padBadgeKey = "#"; // 大标识当前显示的是谁，避免每帧重设图片（"" = 空）

    /// <summary>
    /// 「按下一个键」的大标识：没按键时留空（设计稿要求），按下就显示这一下最终输出成什么键，松开清空。
    /// 扳机（L2/R2）在模型图上被机身挡住看不到，只有这里能给反馈，所以它优先级最高。
    /// 只在变化时换图，轮询里没有额外开销。
    /// </summary>
    private void UpdateLiveBadge(ulong stable, ushort lt, ushort rt)
    {
        string key = "";
        string? icon = null;

        if (lt >= GamepadState.TriggerMax / 2) { key = "L2"; icon = BadgeIcon(GamepadButtons.L2, _layout); }
        else if (rt >= GamepadState.TriggerMax / 2) { key = "R2"; icon = BadgeIcon(GamepadButtons.R2, _layout); }
        else
        {
            // 两轮：先看面键/十字键/肩键，摇杆按下放到最后（同时按多个时优先显示更有信息量的那颗）
            for (int pass = 0; pass < 2 && key.Length == 0; pass++)
            {
                foreach (SpotVisual v in _spotVisuals)
                {
                    if (v.Bits == GamepadButtons.None) continue;
                    if (((ulong)v.Bits & stable) == 0) continue;
                    bool stickClick = v.Name is "L3" or "R3";
                    if ((pass == 0) == stickClick) continue;
                    key = v.Name;
                    icon = BadgeIcon(v.Bits, _layout);
                    break;
                }
            }
        }

        if (key == _padBadgeKey) return;
        _padBadgeKey = key;

        if (key.Length == 0 || icon is null)
        {
            PadOutputBadge.Source = null;
            PadOutputBadge.Visibility = Visibility.Collapsed;
            return;
        }
        WpfImage? img = IconImage(icon, 44);
        PadOutputBadge.Source = img?.Source;
        PadOutputBadge.Visibility = img is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 内部按键位 → 输出平台的图标。面键按各家丝印取字：
    /// Xbox 下=A / 右=B，PS5 下=× / 右=○，NS 下=B / 右=A（Nintendo 的 AB 是反的）。
    /// </summary>
    private static string? BadgeIcon(GamepadButtons bit, string layout)
    {
        bool ns = layout is "pro2" or "pro1";
        string? code = bit switch
        {
            GamepadButtons.South => ns ? PadButtons.B : PadButtons.A,
            GamepadButtons.East => ns ? PadButtons.A : PadButtons.B,
            GamepadButtons.West => ns ? PadButtons.Y : PadButtons.X,
            GamepadButtons.North => ns ? PadButtons.X : PadButtons.Y,
            GamepadButtons.L1 => PadButtons.L,
            GamepadButtons.R1 => PadButtons.R,
            GamepadButtons.L2 => PadButtons.ZL,
            GamepadButtons.R2 => PadButtons.ZR,
            GamepadButtons.LeftStick => PadButtons.L3,
            GamepadButtons.RightStick => PadButtons.R3,
            GamepadButtons.Back => PadButtons.Minus,
            GamepadButtons.Start => PadButtons.Plus,
            GamepadButtons.Capture => PadButtons.Touchpad,
            GamepadButtons.Home => PadButtons.Home,
            GamepadButtons.DPadUp => PadButtons.Up,
            GamepadButtons.DPadDown => PadButtons.Down,
            GamepadButtons.DPadLeft => PadButtons.Left,
            GamepadButtons.DPadRight => PadButtons.Right,
            _ => null
        };
        return code is null ? null : PadIcon(code, layout);
    }

    private static int QuantizeAxis(ushort v)
    {
        int delta = v - GamepadState.AxisCenter;
        // 中心 ±70 counts（约 1.7% 行程）视为静止，吃掉传感器底噪。
        if (Math.Abs(delta) < 70) return 0;
        return Math.Clamp((int)Math.Round(delta / 256.0), -8, 8);
    }

    /// <summary>
    /// 高亮素材名映射：spot 显示名 → Assets/Highlights/{layout}_{asset}.png。
    /// 素材是从手柄线稿逐像素抠出的该按键圆形/十字轮廓，天然对准，不再用代码画线。
    /// </summary>
    private static string HighlightAsset(string layout, string spot) => layout switch
    {
        "xbox" => spot switch
        {
            "Y" => "Y", "X" => "X", "B" => "B", "A" => "A",
            "视图" => "View", "菜单" => "Menu", "L3" => "L3", "R3" => "R3", _ => ""
        },
        "ps5" => spot switch
        {
            "△" => "Tri", "□" => "Square", "○" => "Circle", "×" => "Cross",
            "PS 键" => "PS", "触摸板" => "Touch", "L3" => "L3", "R3" => "R3", _ => ""
        },
        _ => spot switch   // switch / pro1
        {
            "Y" => "Y", "X" => "X", "B" => "B", "A" => "A",
            "－" => "Minus", "＋" => "Plus", "L3" => "L3", "R3" => "R3", _ => ""
        }
    };

    /// <summary>布局切换时一次性建好全部常驻高亮元素（像素级点亮素材 + 摇杆点）。</summary>
    private void BuildHighlights(string layout)
    {
        _spotVisuals.Clear();
        HighlightLayer.Children.Clear();
        var spots = layout == "xbox" ? XboxSpots : layout == "ps5" ? Ps5Spots : SwitchSpots;

        void AddOverlay(string name, GamepadButtons bits, string asset)
        {
            UIElement? visual = MakeOverlay(layout, asset);
            if (visual == null) return;
            HighlightLayer.Children.Add(visual);
            _spotVisuals.Add(new SpotVisual(name, bits, visual));
        }

        foreach (var s in spots)
        {
            // 十字键拆成四个臂：按下哪个方向只亮哪一条
            if (s.Item1 == "十字键")
            {
                AddOverlay("十字键↑", GamepadButtons.DPadUp, "DpadUp");
                AddOverlay("十字键↓", GamepadButtons.DPadDown, "DpadDown");
                AddOverlay("十字键←", GamepadButtons.DPadLeft, "DpadLeft");
                AddOverlay("十字键→", GamepadButtons.DPadRight, "DpadRight");
                continue;
            }

            GamepadButtons bits = SpotButton(s.Item1, layout);
            if (bits == GamepadButtons.None) continue;
            AddOverlay(s.Item1, bits, HighlightAsset(layout, s.Item1));
        }

        // 肩键：直接点亮线稿上的肩键本体（不再用悬浮胶囊）；扳机在模型上看不到，不做提示
        AddOverlay("L1", GamepadButtons.L1, "ShL");
        AddOverlay("R1", GamepadButtons.R1, "ShR");

        // PS5 触摸板两侧那两颗小胶囊：创建 = Back，选项 = Start
        if (layout == "ps5")
        {
            AddOverlay("创建", GamepadButtons.Back, "Create");
            AddOverlay("选项", GamepadButtons.Start, "Options");
        }

        if (Environment.GetEnvironmentVariable("PRO2_MAP_HL_DEBUG") == "1")
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hl-err.log"),
                "layout=" + layout + " visuals=" + _spotVisuals.Count + " spots=" + spots.Length + Environment.NewLine);
        BuildStickIndicators(layout);
        _stableButtons = 0;
        _debounceCandidate = 0;
        _debounceSince = 0;
    }

    /// <summary>
    /// 高亮素材：整块点亮的透明 PNG（亮环 + 圆内透光 + 外圈晕光，与设计稿同一套算法），
    /// 尺寸/位置取素材生成器写出的归一化包围盒，直接贴在线稿对应像素上，不需要再猜圆心。
    /// </summary>
    private static WpfImage? MakeOverlay(string layout, string asset)
    {
        if (string.IsNullOrEmpty(asset)) return null;
        var map = HighlightGeometry.For(layout);
        if (map == null || !map.TryGetValue(asset, out var geo)) return null;

        // Pro1/Pro2 与 Switch 共用同一张线稿，素材前缀也是 switch
        string prefix = layout is "pro2" or "pro1" ? "switch" : layout;
        var (pw, ph) = PadPixels(layout);
        double scale = Math.Min(IW / pw, IH / ph);
        double drawW = pw * scale, drawH = ph * scale;
        double offX = IX + (IW - drawW) / 2;
        double offY = IY + (IH - drawH) / 2;

        WpfImage img;
        try
        {
            img = new WpfImage
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri($"pack://application:,,,/Assets/Highlights/{prefix}_{asset}.png", UriKind.Absolute)),
                Width = geo.W * drawW,
                Height = geo.H * drawH,
                Stretch = System.Windows.Media.Stretch.Fill,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("PRO2_MAP_HL_DEBUG") == "1")
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hl-err.log"),
                    layout + " " + asset + ": " + ex.GetType().Name + " " + ex.Message + Environment.NewLine);
            return null;
        }
        RenderOptions.SetBitmapScalingMode(img, System.Windows.Media.BitmapScalingMode.HighQuality);
        Canvas.SetLeft(img, offX + geo.X * drawW);
        Canvas.SetTop(img, offY + geo.Y * drawH);
        return img;
    }

    /// <summary>布局切换时重建常驻摇杆点元素（光晕 + 圆点）。</summary>
    private void BuildStickIndicators(string layout)
    {
        StickHighlightLayer.Children.Clear();
        var spots = layout == "xbox" ? XboxSpots : layout == "ps5" ? Ps5Spots : SwitchSpots;
        for (int i = 0; i < 2; i++)
        {
            var s = spots.FirstOrDefault(p => p.Item1 == (i == 0 ? "L3" : "R3"));
            if (s == default)
            {
                _stickGlow[i] = _stickDot[i] = null;
                continue;
            }
            var geom = SpotRect(layout, s);
            _stickGeom[i] = geom;
            double rd = Math.Max(6.5, geom.d * 0.135);

            // 光晕收紧：只做一圈贴着圆点的柔光，别糊成一块雾
            var glow = new Ellipse
            {
                Width = rd + 9, Height = rd + 9,
                Fill = new SolidColorBrush(Color.FromArgb(150, Accent.R, Accent.G, Accent.B)),
                Effect = new BlurEffect { Radius = 6 },
                Visibility = Visibility.Collapsed
            };
            var dot = new Ellipse
            {
                Width = rd, Height = rd,
                Fill = new SolidColorBrush(Accent),
                Visibility = Visibility.Collapsed
            };
            _stickGlow[i] = glow;
            _stickDot[i] = dot;
            StickHighlightLayer.Children.Add(glow);
            StickHighlightLayer.Children.Add(dot);
        }
    }

    /// <summary>把常驻摇杆点移到量化后的位置；中心时隐藏。</summary>
    private void UpdateStickIndicator(int index, int ax, int ay)
    {
        var glow = _stickGlow[index];
        var dot = _stickDot[index];
        if (glow == null || dot == null) return;
        if (ax == 0 && ay == 0)
        {
            if (glow.Visibility != Visibility.Collapsed)
            {
                glow.Visibility = Visibility.Collapsed;
                dot.Visibility = Visibility.Collapsed;
            }
            return;
        }
        var (cx, cy, d) = _stickGeom[index];
        double px = cx + ax / 8.0 * d * 0.36;
        double py = cy - ay / 8.0 * d * 0.36; // 屏幕 y 向下；摇杆上推 ay>0 应向上
        Canvas.SetLeft(glow, px - glow.Width / 2);
        Canvas.SetTop(glow, py - glow.Height / 2);
        Canvas.SetLeft(dot, px - dot.Width / 2);
        Canvas.SetTop(dot, py - dot.Height / 2);
        glow.Visibility = Visibility.Visible;
        dot.Visibility = Visibility.Visible;
    }

    private void SyncChips()
    {
        var rows = new[] { (Btn: ChipGL, Key: "GL"), (Btn: ChipGR, Key: "GR"), (Btn: ChipShot, Key: "截图"), (Btn: ChipHome, Key: "Home") };
        foreach (var (btn, key) in rows)
            btn.Style = (Style)FindResource(key == _padSource ? "ChipOn" : "Chip");
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string key) return;
        _padSource = key;
        SyncChips();
        HidePickView();
        RefreshPadCard();
    }

    /// <summary>当前物理布局（ABXY 丝印）：配置里存过就用它，否则按输出平台给默认值。</summary>
    private string CurrentFaceLayout()
    {
        string? want = _profile.FaceLayout;
        if (want is "xbox" or "pro2") return want;
        // 没设置过：Xbox 输出默认 Xbox 丝印，Pro2(NS) 输出默认 NS 丝印。
        // 注意 OutputMode 里 Pro2 也记作 "xbox"，默认值必须看 IsXboxSelected。
        bool xboxOutput = (DataContext as MainViewModel)?.IsXboxSelected ?? false;
        return xboxOutput ? "xbox" : "pro2";
    }

    /// <summary>ABXY 对调开关：切的是「手柄物理布局」，图、热点、输出映射一起换。</summary>
    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string key) return;
        if (CurrentFaceLayout() == key) return;
        _profile.FaceLayout = key;
        // 只换映射语义：线稿、热点、高亮都不动，分段控件跟着亮
        PadMappingEngine.SetFaceLayout(key);
        ApplyLayout();
        RefreshPadCard();
    }

    private void ApplyLayout()
    {
        bool xbox = CurrentFaceLayout() == "xbox";
        SegXbox.Visibility = xbox ? Visibility.Visible : Visibility.Collapsed;
        SegPro2.Visibility = xbox ? Visibility.Collapsed : Visibility.Visible;
        SegTextXbox.Foreground = (SolidColorBrush)FindResource(xbox ? "T1" : "T3");
        SegTextPro2.Foreground = (SolidColorBrush)FindResource(xbox ? "T3" : "T1");
        SegTextXbox.FontWeight = xbox ? FontWeights.Bold : FontWeights.Normal;
        SegTextPro2.FontWeight = xbox ? FontWeights.Normal : FontWeights.Bold;
        if (_builtHighlightsFor != _artLayout)
        {
            _builtHighlightsFor = _artLayout;
            BuildHighlights(_artLayout); // 一次性重建全部常驻高亮元素（面键/十字键/肩键/摇杆/触摸板）
        }
    }

    private void RefreshPadCard()
    {
        if (!_ready) return;

        PadTitle.Text = _padSource is "GL" or "GR" ? _padSource + " 背键"
            : SourceKeys.IsAnalog(_padSource) ? _padSource
            : _padSource + " 键";

        BindingTarget target = _profile.Get(_padSource, BindingTrigger.Tap);
        PadOutput.Text = BindingLabels.Target(target, _layout);

        // 当前映射的大图标：没有图标时把文字往左挪，避免留一个空位
        string? icon = TargetIcon(target, _layout);
        WpfImage? iconImage = IconImage(icon, 20);
        PadOutputIcon.Source = iconImage?.Source;
        PadOutputIcon.Visibility = iconImage is null ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(PadOutput, iconImage is null ? 14 : 40);
        PadOutput.Width = iconImage is null ? 160 : 146;

        // 下方大标识：只在按下时显示（见 UpdateLiveBadge），没按键时保持空白
        _padBadgeKey = "#";
        UpdateLiveBadge(_stableButtons, _lastSnap.Lt, _lastSnap.Rt);
    }

    // ── 选择目标（内联，不弹层）────────────────────────────────────
    private void PickTarget_Click(object sender, RoutedEventArgs e)
    {
        if (PadPickView.Visibility == Visibility.Visible) HidePickView();
        else ShowPickView();
    }

    private void ShowPickView()
    {
        PickSubtitle.Text = PadTitle.Text + " · 轻按";
        BuildPickList();
        SetProfileBarVisible(false);
        PadEditView.Visibility = Visibility.Collapsed;
        PadPickView.Visibility = Visibility.Visible;
    }

    private void PickCancel_Click(object sender, RoutedEventArgs e) => HidePickView();

    private void HidePickView()
    {
        PadPickView.Visibility = Visibility.Collapsed;
        PadEditView.Visibility = Visibility.Visible;
        SetProfileBarVisible(true);
    }

    /// <summary>选择/管理视图里不显示配置条，避免和标题、返回按钮抢位置。</summary>
    private void SetProfileBarVisible(bool on)
    {
        Visibility v = on ? Visibility.Visible : Visibility.Collapsed;
        ProfilePrev.Visibility = v;
        ProfileChip.Visibility = v;
        ProfileNext.Visibility = v;
        ProfileMore.Visibility = v;
    }

    private void BuildPickList()
    {
        PickHost.Children.Clear();
        const double cw = 72, ch = 34, gapX = 6, gapY = 6;
        int index = 0;

        // 目标按输出平台过滤：只用三家都有的通用键 + 该平台特有键。
        // GL/GR 是手柄背键（源），不能当输出目标；触摸板仅 PS5 有；截图仅 NS/Xbox 有。
        string[] targets = _layout == "ps5"
            ? [PadButtons.A, PadButtons.B, PadButtons.X, PadButtons.Y,
               PadButtons.L, PadButtons.R, PadButtons.ZL, PadButtons.ZR,
               PadButtons.L3, PadButtons.R3, PadButtons.Plus, PadButtons.Minus,
               PadButtons.Up, PadButtons.Down, PadButtons.Left, PadButtons.Right,
               PadButtons.Touchpad, PadButtons.Home]
            : [PadButtons.A, PadButtons.B, PadButtons.X, PadButtons.Y,
               PadButtons.L, PadButtons.R, PadButtons.ZL, PadButtons.ZR,
               PadButtons.L3, PadButtons.R3, PadButtons.Plus, PadButtons.Minus,
               PadButtons.Up, PadButtons.Down, PadButtons.Left, PadButtons.Right,
               PadButtons.Capture, PadButtons.Home];

        foreach (string code in targets)
        {
            AddPickItem(index++, BindingLabels.Pad(code, _layout), BindingTarget.Pad(code), cw, ch, gapX, gapY);
        }

        AddPickItem(index++, "未映射", BindingTarget.None, cw, ch, gapX, gapY);
    }

    private static readonly string[] KeyIconNames =
    [
        "space", "shift", "ctrl", "alt", "escape", "tab", "enter", "backspace", "capslock",
        "arrow_up", "arrow_down", "arrow_left", "arrow_right"
    ];

    /// <summary>目标图标：手柄键用平台图标，键盘用键帽，鼠标用鼠标图标；没有就返回 null 用文字。</summary>
    private static string? TargetIcon(BindingTarget target, string layout)
    {
        if (target.Kind == BindingTargetKind.PadButton) return PadIcon(target.Code, layout);
        if (target.Kind == BindingTargetKind.MouseButton)
            return target.Code switch
            {
                "Left" => "mouse/mouse_left",
                "Right" => "mouse/mouse_right",
                "Middle" => "mouse/mouse_scroll",
                _ => "mouse/mouse"
            };
        if (target.Kind == BindingTargetKind.MouseAxis)
            return target.Code is "Wheel" or "WheelUp" or "WheelDown" ? "mouse/mouse_scroll" : "mouse/mouse_move";
        if (target.Kind == BindingTargetKind.Keyboard)
        {
            string key = target.Code;
            if (key.StartsWith("D", StringComparison.Ordinal) && key.Length == 2 && char.IsDigit(key[1]))
                key = key[1].ToString();
            else if (key.StartsWith("NumPad", StringComparison.Ordinal)) key = key[6..];
            else if (key.Length == 1) key = key.ToLowerInvariant();
            else key = key switch
            {
                "Space" => "space",
                "Return" or "Enter" => "enter",
                "Escape" => "escape",
                "Back" => "backspace",
                "Tab" => "tab",
                "LeftShift" or "RightShift" => "shift",
                "LeftCtrl" or "RightCtrl" => "ctrl",
                "LeftAlt" or "RightAlt" => "alt",
                "CapsLock" => "capslock",
                "Up" => "arrow_up",
                "Down" => "arrow_down",
                "Left" => "arrow_left",
                "Right" => "arrow_right",
                _ => ""
            };
            if (key.Length == 1 && "abcdefghijklmnopqrstuvwxyz0123456789".Contains(key))
                return "key/keyboard_" + key;
            return Array.IndexOf(KeyIconNames, key) >= 0 ? "key/keyboard_" + key : null;
        }
        return null;
    }

    /// <summary>输出平台的图标前缀：PS5 / Xbox / 其余（Pro2 物理布局、Pro1 输出）都用 Switch Pro。</summary>
    /// <summary>
    /// 图标集：PS5→PlayStation；Xbox→Xbox；ns2→Switch 2（键鼠页源按键，含 GL/GR/C）；
    /// 其余（Pro1/Pro2 输出）→ Switch 1。Pro1 输出的是 Switch 协议，所以标识也随 NS1。
    /// </summary>
    private static string IconSet(string layout) =>
        layout == "ps5" ? "ps" : layout == "xbox" ? "xbox" : layout == "ns2" ? "ns2" : "ns1";

    private static string? PadIcon(string code, string layout)
    {
        string set = IconSet(layout);

        if (set == "ps")
        {
            return code switch
            {
                PadButtons.A => "pad/psp_cross",
                PadButtons.B => "pad/psp_circle",
                PadButtons.X => "pad/psp_square",
                PadButtons.Y => "pad/psp_triangle",
                PadButtons.L => "pad/psp_l1",
                PadButtons.R => "pad/psp_r1",
                PadButtons.ZL => "pad/psp_l2",
                PadButtons.ZR => "pad/psp_r2",
                PadButtons.L3 => "pad/psp_l3",
                PadButtons.R3 => "pad/psp_r3",
                PadButtons.Plus => "pad/psp_options",
                PadButtons.Minus => "pad/psp_create",
                PadButtons.Up => "pad/psp_dup",
                PadButtons.Down => "pad/psp_ddown",
                PadButtons.Left => "pad/psp_dleft",
                PadButtons.Right => "pad/psp_dright",
                PadButtons.Touchpad => "pad/psp_touchpad",
                // PS5 没有 Home / 截图，用文字 "PS" 代替图标（不再借用 Xbox 手柄图标）。
                _ => null
            };
        }

        if (set == "xbox")
        {
            return code switch
            {
                PadButtons.A => "pad/xbox_a",
                PadButtons.B => "pad/xbox_b",
                PadButtons.X => "pad/xbox_x",
                PadButtons.Y => "pad/xbox_y",
                PadButtons.L => "pad/xbox_lb",
                PadButtons.R => "pad/xbox_rb",
                PadButtons.ZL => "pad/xbox_lt",
                PadButtons.ZR => "pad/xbox_rt",
                PadButtons.L3 => "pad/xbox_ls",
                PadButtons.R3 => "pad/xbox_rs",
                PadButtons.Plus => "pad/xbox_menu",
                PadButtons.Minus => "pad/xbox_view",
                PadButtons.Up => "pad/xbox_dpad_up",
                PadButtons.Down => "pad/xbox_dpad_down",
                PadButtons.Left => "pad/xbox_dpad_left",
                PadButtons.Right => "pad/xbox_dpad_right",
                PadButtons.Capture => "pad/xbox_share",
                PadButtons.Home => "pad/xbox_guide",
                _ => null
            };
        }

        // Switch 2（NS2）：键鼠页源按键标识，含 GL/GR 背键与 C 键
        if (set == "ns2")
        {
            return code switch
            {
                PadButtons.A => "pad/ns2_a",
                PadButtons.B => "pad/ns2_b",
                PadButtons.X => "pad/ns2_x",
                PadButtons.Y => "pad/ns2_y",
                PadButtons.L => "pad/ns2_l",
                PadButtons.R => "pad/ns2_r",
                PadButtons.ZL => "pad/ns2_zl",
                PadButtons.ZR => "pad/ns2_zr",
                PadButtons.GL => "pad/ns2_gl",
                PadButtons.GR => "pad/ns2_gr",
                PadButtons.L3 => "pad/ns2_l3",
                PadButtons.R3 => "pad/ns2_r3",
                PadButtons.Plus => "pad/ns2_plus",
                PadButtons.Minus => "pad/ns2_minus",
                PadButtons.Up => "pad/ns2_dup",
                PadButtons.Down => "pad/ns2_ddown",
                PadButtons.Left => "pad/ns2_dleft",
                PadButtons.Right => "pad/ns2_dright",
                PadButtons.Home => "pad/ns2_home",
                // 截图键：方框内套圆的专用图标（此前缺映射，退化成「截图」两个字）
                PadButtons.Capture => "pad/ns2_capture",
                // NS2 的 C 键（Pro2 手柄特有），单独用 C 图标
                _ => code == "C" ? "pad/ns2_c" : null
            };
        }

        // Switch 1（NS1）：Pro1 输出模式的标识
        return code switch
        {
            PadButtons.A => "pad/ns1_a",
            PadButtons.B => "pad/ns1_b",
            PadButtons.X => "pad/ns1_x",
            PadButtons.Y => "pad/ns1_y",
            PadButtons.L => "pad/ns1_l",
            PadButtons.R => "pad/ns1_r",
            PadButtons.ZL => "pad/ns1_zl",
            PadButtons.ZR => "pad/ns1_zr",
            PadButtons.L3 => "pad/ns1_l3",
            PadButtons.R3 => "pad/ns1_r3",
            PadButtons.Plus => "pad/ns1_plus",
            PadButtons.Minus => "pad/ns1_minus",
            PadButtons.Up => "pad/ns1_dup",
            PadButtons.Down => "pad/ns1_ddown",
            PadButtons.Left => "pad/ns1_dleft",
            PadButtons.Right => "pad/ns1_dright",
            PadButtons.Capture => "pad/switch_capture",
            PadButtons.Home => "pad/ns1_home",
            _ => code == "C" ? "pad/ns2_c" : null
        };
    }

    private static WpfImage? IconImage(string? relative, double size)
    {
        if (relative is null) return null;
        try
        {
            var image = new WpfImage
            {
                Width = size, Height = size,
                Stretch = Stretch.Uniform,
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/InputPrompts/" + relative + ".png", UriKind.Absolute)),
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void AddPickItem(int index, string label, BindingTarget target, double cw, double ch, double gapX, double gapY)
    {
        int col = index % 3, row = index / 3;
        BindingTarget current = _profile.Get(_padSource, BindingTrigger.Tap);
        bool selected = current.Kind == target.Kind && current.Code == target.Code;

        // 目标项：图标 + 文字。手柄键用平台图标，键盘用键帽，鼠标用鼠标图标；
        // 没有对应图标（例如 Xbox 的 LB/RT）就退回纯文字，避免出现空白格。
        string? icon = TargetIcon(target, _layout);
        object content;
        if (icon is not null && IconImage(icon, 22) is { } img)
        {
            // 有图标就只放图标，文字只留作 tooltip，避免图标旁边再挂字符。
            img.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            img.VerticalAlignment = VerticalAlignment.Center;
            content = img;
        }
        else
        {
            content = new TextBlock { Text = label, FontSize = 11 };
        }

        var button = new Button
        {
            Content = content,
            Width = cw,
            Height = ch,
            ToolTip = label,
            Style = (Style)FindResource(selected ? "ChipOn" : "Chip"),
            Tag = target
        };
        button.Click += PickItem_Click;
        Canvas.SetLeft(button, col * (cw + gapX));
        Canvas.SetTop(button, row * (ch + gapY));
        PickHost.Children.Add(button);
    }

    private void PickItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not BindingTarget target) return;
        _profile.Set(_padSource, BindingTrigger.Tap, target);
        MarkDirty();
        HidePickView();
        RefreshPadCard();
    }

    // ── 保存 / 恢复 ───────────────────────────────────────────────
    private void MarkDirty() => SaveButton.Content = "保存并应用";

    private void ResetThisKey_Click(object sender, RoutedEventArgs e)
    {
        // 手柄模式只恢复轻按这一位。
        MappingProfile defaults = ProfileSet.DefaultFor(_set.mode);
        _profile.Set(_padSource, BindingTrigger.Tap, defaults.Get(_padSource, BindingTrigger.Tap));

        MarkDirty();
        RefreshPadCard();
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        _profile = ProfileSet.DefaultFor(_set.mode);
        _set.Current.Bindings = _profile;
        MarkDirty();
        RefreshPadCard();
        RefreshKbmCard();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _set.Current.Bindings = _profile;
        bool ok = _library.Save();
        // 落盘后立刻下发到输出通路，不需要重连手柄
        (DataContext as MainViewModel)?.ApplyMappingFromDisk();
        SaveButton.Content = ok ? "已保存" : "保存失败";
    }

    // ── 键鼠：屏幕键盘 ──────────────────────────────────────────────
    private void BuildKeyboard()
    {
        KeyboardHost.Children.Clear();
        const double pad = 8, gap = 3, cols = 15;
        const double w = 728, h = 208;
        double span = (w - pad * 2 - gap * (cols - 1)) / cols;
        double rh = (h - pad * 2 - gap * 4) / 5;
        double fs = Math.Max(7, Math.Min(13, Math.Round(w / 60)));

        KeyboardHost.Children.Add(new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(7, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        });

        var outs = ActiveKeys();
        for (int ri = 0; ri < KeyRows.Length; ri++)
        {
            double u = 0;
            foreach (var (k, sp) in KeyRows[ri])
            {
                double kw = sp * span + (sp - 1) * gap;
                double kx = pad + u * (span + gap);
                double ky = pad + ri * (rh + gap);
                // 空格键在表格里没有丝印（宽度 6 格、文字留空），点亮要按「空格」这个名字查
                bool on = outs.Contains(k.Length > 0 ? k : "空格");

                if (on)
                {
                    var glow = new Border
                    {
                        Width = kw + 10, Height = rh + 8,
                        CornerRadius = new CornerRadius(8),
                        Background = new SolidColorBrush(Color.FromArgb(87, Accent.R, Accent.G, Accent.B)),
                        Effect = new BlurEffect { Radius = 10 }
                    };
                    Canvas.SetLeft(glow, kx - 5);
                    Canvas.SetTop(glow, ky - 4);
                    KeyboardHost.Children.Add(glow);
                }

                var key = new Border
                {
                    Width = kw, Height = rh, CornerRadius = new CornerRadius(4),
                    Background = on
                        ? new SolidColorBrush(Color.FromArgb(235, Accent.R, Accent.G, Accent.B))
                        : new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)),
                    BorderBrush = on
                        ? new SolidColorBrush(Accent)
                        : new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = k,
                        FontSize = k.Length > 1 ? fs - 1.5 : fs,
                        Foreground = (SolidColorBrush)FindResource(on ? "Ink" : "T3"),
                        FontWeight = on ? FontWeights.Bold : FontWeights.Normal,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                        TextAlignment = System.Windows.TextAlignment.Center
                    }
                };
                Canvas.SetLeft(key, kx);
                Canvas.SetTop(key, ky);
                KeyboardHost.Children.Add(key);
                u += sp;
            }
        }
    }

    /// <summary>按当前源键、当前触发位的真实映射点亮键盘。左摇杆的默认目标 W 代表整组 WASD。</summary>
    private HashSet<string> ActiveKeys()
    {
        var on = new HashSet<string>(StringComparer.Ordinal);
        BindingTarget target = _kbmTrigger == BindingTrigger.Combo
            ? _profile.GetCombo(_kbmSource).Target
            : _profile.Get(_kbmSource, _kbmTrigger);
        if (target.Kind == BindingTargetKind.Keyboard) on.Add(KeyboardKeyLabel(target.Code));

        // 左摇杆＝整组 WASD：未映射或映射到 W 时，四个键一起亮（此前漏了 W 本体）
        if (_kbmSource == SourceKeys.LeftStick && (target.IsNone || target.Code == "W"))
        {
            on.Add("W");
            on.Add("A");
            on.Add("S");
            on.Add("D");
        }

        return on;
    }

    /// <summary>键盘表上的丝印名：D1 → 1。</summary>
    private static string KeyboardKeyLabel(string code)
    {
        if (code.Length == 2 && code[0] == 'D' && char.IsDigit(code[1])) return code[1].ToString();
        return BindingLabels.KeyboardName(code);
    }

    // ── 键鼠：源键网格（图标）────────────────────────────────────────
    /// <summary>界面源键名 → 输出键码（源键是物理位置，图标签按当前平台换）。</summary>
    private static readonly Dictionary<string, string> SourceToPadCode = new(StringComparer.Ordinal)
    {
        ["A"] = PadButtons.A, ["B"] = PadButtons.B, ["X"] = PadButtons.X, ["Y"] = PadButtons.Y,
        ["↑"] = PadButtons.Up, ["↓"] = PadButtons.Down, ["←"] = PadButtons.Left, ["→"] = PadButtons.Right,
        ["L"] = PadButtons.L, ["R"] = PadButtons.R, ["ZL"] = PadButtons.ZL, ["ZR"] = PadButtons.ZR,
        ["L3"] = PadButtons.L3, ["R3"] = PadButtons.R3, ["＋"] = PadButtons.Plus, ["－"] = PadButtons.Minus,
        ["截图"] = PadButtons.Capture, ["Home"] = PadButtons.Home
    };

    /// <summary>源键图标跟当前平台走，不再一律 Xbox。背键只有 Xbox 图标（硬件特性）。</summary>
    /// <summary>
    /// 键鼠页的手柄源按键标识固定用 NS2（Pro2 手柄物理布局，含 GL/GR/C），
    /// 不跟随映射页的输出平台——源与输出是两件事。
    /// </summary>
    private string? SourceIcon(string source) => source switch
    {
        "GL" => "pad/ns2_gl",
        "GR" => "pad/ns2_gr",
        SourceKeys.LeftStick => "pad/stick_left",
        SourceKeys.RightStick => "pad/stick_right",
        _ => SourceToPadCode.TryGetValue(source, out string? code) ? PadIcon(code, "ns2") : null
    };

    private string SourceLabel(string source) => source switch
    {
        "＋" => BindingLabels.Pad(PadButtons.Plus, _layout),
        "－" => BindingLabels.Pad(PadButtons.Minus, _layout),
        "GL" => "GL 背键",
        "GR" => "GR 背键",
        SourceKeys.LeftStick => "左摇杆（WASD）",
        SourceKeys.RightStick => "右摇杆（鼠标）",
        _ => source + " 键"
    };

    private void BuildSources()
    {
        SourceHost.Children.Clear();
        const double sw = 52, sh = 36, sg = 8, srow = 10;
        double sy = 70;
        foreach (var row in SourceRows)
        {
            double rowW = row.Length * sw + (row.Length - 1) * sg;
            double sx = Math.Round((496 - rowW) / 2);
            foreach (var k in row)
            {
                bool on = k == _kbmSource;
                var button = new Button
                {
                    Width = sw,
                    Height = sh,
                    Style = (Style)FindResource(on ? "ChipOn" : "Chip"),
                    Tag = k,
                    ToolTip = SourceLabel(k)
                };

                string? icon = SourceIcon(k);
                WpfImage? image = IconImage(icon, 26);
                if (image is not null)
                {
                    image.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                    image.VerticalAlignment = VerticalAlignment.Center;
                    button.Content = image;
                }
                else
                {
                    button.Content = new TextBlock { Text = k, FontSize = 11 };
                }

                button.Click += Source_Click;
                Canvas.SetLeft(button, sx);
                Canvas.SetTop(button, sy);
                SourceHost.Children.Add(button);
                sx += sw + sg;
            }
            sy += sh + srow;
        }
    }

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string k)
        {
            _kbmSource = k;
            _kbmTrigger = BindingTrigger.Tap;
            _comboPartner = "";
            _moreOpen = false;
            HideKbmPick();
            BuildSources();
            UpdateKbmOutput();
        }
    }

    private static bool IsAnalogSource(string source) => SourceKeys.IsAnalog(source);

    private string TargetLayout => _layout;

    private void RefreshKbmCard()
    {
        if (!_ready) return;
        BuildSources();
        UpdateKbmOutput();
    }

    private void UpdateKbmOutput()
    {
        // 配置管理页盖在键盘上，别在它下面把键盘又显示出来。
        if (KbmProfileView.Visibility == Visibility.Visible) return;

        bool analog = IsAnalogSource(_kbmSource);
        bool mouseView = _kbmSource is SourceKeys.RightStick or SourceKeys.Gyro;
        bool picking = KbmPickView.Visibility == Visibility.Visible;

        if (!picking)
        {
            MouseOutView.Visibility = mouseView ? Visibility.Visible : Visibility.Collapsed;
            KeyboardHost.Visibility = mouseView ? Visibility.Collapsed : Visibility.Visible;
        }

        KbmTitle.Text = mouseView ? "鼠标输出" : "屏幕键盘";
        KbmSrcTitle.Text = analog ? _kbmSource : _kbmSource + " 键";

        if (analog)
        {
            // 摇杆是固定输出，没有触发位；只把结果填进轻按行，避免卡片空着。
            _moreOpen = false;
            SyncKbmCardView();
            KbmTapRow.IsEnabled = false;
            KbmTapValue.Text = _kbmSource == SourceKeys.LeftStick ? "W / A / S / D" : "鼠标移动";
            KbmTapValue.Foreground = (SolidColorBrush)FindResource("T3");
            KbmMoreRow.Visibility = Visibility.Collapsed;
            KbmOutputLabel.Text = "输出 · " + (_kbmSource == SourceKeys.LeftStick ? "键盘 WASD" : "鼠标移动");
            if (!picking && _kbmSource == SourceKeys.LeftStick) BuildKeyboard();
            return;
        }

        KbmTapRow.IsEnabled = true;
        KbmMoreRow.Visibility = Visibility.Visible;
        RefreshKbmSlots();
        if (!picking) BuildKeyboard();
    }



    private void SyncKbmCardView()
    {
        KbmMainView.Visibility = _moreOpen ? Visibility.Collapsed : Visibility.Visible;
        KbmMoreView.Visibility = _moreOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>刷新右卡：轻按行 + 更多动作摘要，以及更多动作页里的三行。</summary>
    private void RefreshKbmSlots()
    {
        bool tapBound = _profile.IsTapBound(_kbmSource);
        // 只有双击 / 长按依赖轻按；组合键独立存在，没轻按也允许进二级页。
        if (!tapBound && _kbmTrigger is BindingTrigger.Double or BindingTrigger.Long)
        {
            _kbmTrigger = BindingTrigger.Tap;
            _moreOpen = false;
        }

        BindingTarget tap = _profile.Get(_kbmSource, BindingTrigger.Tap);
        BindingTarget dbl = _profile.Get(_kbmSource, BindingTrigger.Double);
        BindingTarget lng = _profile.Get(_kbmSource, BindingTrigger.Long);
        (BindingTarget comboTarget, string comboPartner) = _profile.GetCombo(_kbmSource);

        // 轻按：主入口，任何时候都能点。
        KbmTapValue.Text = tap.IsNone ? "未映射" : BindingLabels.Target(tap, TargetLayout);
        KbmTapValue.Foreground = (SolidColorBrush)FindResource(tap.IsNone ? "T4" : "T1");

        // 更多动作：入口一直可用；只有双击 / 长按依赖轻按，未设轻按时提示「请优先设置轻按」。
        var extras = new List<string>();
        if (!dbl.IsNone) extras.Add("双击");
        if (!lng.IsNone) extras.Add("长按");
        if (!comboTarget.IsNone) extras.Add("组合键");

        KbmMoreRow.Opacity = 1.0;
        KbmMoreValue.Text = extras.Count > 0 ? string.Join(" · ", extras) : "未启用";
        KbmMoreValue.Foreground = (SolidColorBrush)FindResource(extras.Count > 0 ? "T3" : "T4");

        // 更多动作页三行：双击 / 长按要先有轻按，组合键不受限制。
        KbmRowDouble.IsEnabled = tapBound;
        KbmRowLong.IsEnabled = tapBound;
        KbmRowDouble.Opacity = tapBound ? 1.0 : 0.7;
        KbmRowLong.Opacity = tapBound ? 1.0 : 0.7;

        KbmValDouble.Text = tapBound ? BindingLabels.Target(dbl, TargetLayout) : "请优先设置轻按";
        KbmValDouble.Foreground = (SolidColorBrush)FindResource(tapBound && !dbl.IsNone ? "T3" : "T4");
        KbmValLong.Text = tapBound ? BindingLabels.Target(lng, TargetLayout) : "请优先设置轻按";
        KbmValLong.Foreground = (SolidColorBrush)FindResource(tapBound && !lng.IsNone ? "T3" : "T4");

        // 组合键：先写清楚是哪两颗手柄键（L + R），再写它输出什么，别让「R + 键盘 X」读成手柄配键盘。
        bool comboReady = !comboTarget.IsNone && comboPartner.Length > 0;
        KbmValCombo.Text = comboReady
            ? _kbmSource + " + " + comboPartner + " › " + BindingLabels.Target(comboTarget, TargetLayout)
            : "未设置";
        KbmValCombo.FontSize = comboReady ? 10.5 : 11.5;
        KbmValCombo.Foreground = (SolidColorBrush)FindResource(comboTarget.IsNone ? "T4" : "T3");

        SyncKbmCardView();

        BindingTarget current = _kbmTrigger switch
        {
            BindingTrigger.Double => dbl,
            BindingTrigger.Long => lng,
            BindingTrigger.Combo => comboTarget,
            _ => tap
        };
        KbmOutputLabel.Text = "输出 · " + BindingLabels.Target(current, TargetLayout);
    }

    /// <summary>当前键的组合搭子：优先取已保存的，其次取上下文默认值。</summary>
    private string ResolvePartner()
    {
        string saved = _profile.GetCombo(_kbmSource).Partner;
        if (!string.IsNullOrEmpty(saved)) return saved;
        if (!string.IsNullOrEmpty(_comboPartner)) return _comboPartner;
        return _kbmSource switch { "L" => "R", "R" => "L", _ => "" };
    }

    private void KbmRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;

        // 更多动作入口：一直可用，但进来前先把上方可能开着的选择面板收掉。
        if (tag == "4")
        {
            HideKbmPick();
            _kbmTrigger = BindingTrigger.Tap;
            _moreOpen = true;
            SyncKbmCardView();
            return;
        }

        // 双击 / 长按必须先有轻按，否则点了没反应（行本身已是禁用态）。
        if ((tag == "1" || tag == "2") && !_profile.IsTapBound(_kbmSource)) return;

        _kbmTrigger = tag switch
        {
            "1" => BindingTrigger.Double,
            "2" => BindingTrigger.Long,
            "3" => BindingTrigger.Combo,
            _ => BindingTrigger.Tap
        };

        if (_kbmTrigger == BindingTrigger.Tap) _moreOpen = false;
        _comboPartner = ResolvePartner();
        RefreshKbmSlots();

        // 组合键还没选同按键时先选，否则设了目标也不会触发。
        if (_kbmTrigger == BindingTrigger.Combo && string.IsNullOrEmpty(_comboPartner))
        {
            ShowKbmPartnerPick();
            return;
        }

        ShowKbmPick();
    }

    private void KbmMoreBack_Click(object sender, RoutedEventArgs e)
    {
        // 返回一级：右卡回主视图，上方（键盘/选择面板）也要一起收回去。
        HideKbmPick();
        _moreOpen = false;
        _kbmTrigger = BindingTrigger.Tap;
        RefreshKbmSlots();
    }

    private void KbmPartnerEdit_Click(object sender, RoutedEventArgs e) => ShowKbmPartnerPick();

    // ── 键鼠：目标选择（铺在键盘上方）──────────────────────────────
    private static readonly (string Label, BindingTarget Target)[] KbmMouseTargets =
    [
        ("左键", BindingTarget.MouseBtn("Left")),
        ("右键", BindingTarget.MouseBtn("Right")),
        ("中键", BindingTarget.MouseBtn("Middle")),
        ("滚轮上", BindingTarget.Mouse("WheelUp")),
        ("滚轮下", BindingTarget.Mouse("WheelDown"))
    ];

    /// <summary>常用键：不进捕获也能一键选中。键鼠模式只出键盘 / 鼠标。</summary>
    private static readonly (string Label, string Code)[] KbmCommonKeys =
    [
        ("空格", "Space"), ("Ctrl", "LeftCtrl"), ("Shift", "LeftShift"), ("Alt", "LeftAlt"),
        ("Esc", "Escape"), ("Tab", "Tab"), ("Enter", "Return"), ("退格", "Back"),
        ("↑", "Up"), ("↓", "Down"), ("←", "Left"), ("→", "Right"),
        ("1", "D1"), ("2", "D2"), ("3", "D3"), ("4", "D4"),
        ("Q", "Q"), ("E", "E"), ("R", "R"), ("F", "F"),
        ("G", "G"), ("C", "C"), ("X", "X"), ("Z", "Z")
    ];

    /// <summary>组合搭子候选：全部实体按键。</summary>
    private static readonly string[] KbmPartnerKeys =
    [
        "A", "B", "X", "Y", "↑", "↓", "←", "→",
        "L", "R", "ZL", "ZR", "L3", "R3", "＋", "－",
        "截图", "Home", "GL", "GR"
    ];

    private void ShowKbmPick()
    {
        _pickingPartner = false;

        bool combo = _kbmTrigger == BindingTrigger.Combo;
        string source = _kbmSource is "GL" or "GR" ? _kbmSource + " 背键" : _kbmSource + " 键";
        KbmCaptureHint.Text = "点「捕获」后按键盘上的键";
        KbmPartnerChip.Visibility = Visibility.Collapsed;
        KbmPartnerEditButton.Content = "换组合键";

        if (combo)
        {
            // 组合键输出：上半部把整组亮出来（A + 图标 B），右边是换组合键；下半部才是「输出」选键。
            KbmPickTitle.Visibility = Visibility.Collapsed;
            KbmPickSubtitle.Visibility = Visibility.Collapsed;
            KbmComboHead.Visibility = Visibility.Visible;
            BuildComboChip();

            KbmCaptureButton.Visibility = Visibility.Collapsed;
            KbmClearButton.Visibility = Visibility.Collapsed;
            KbmCaptureHint.Visibility = Visibility.Collapsed;
            KbmPartnerEditButton.Visibility = Visibility.Visible;
            Canvas.SetLeft(KbmPartnerEditButton, 564);
            Canvas.SetTop(KbmPartnerEditButton, 20);
            KbmPartnerEditButton.Width = 96;
            KbmPartnerEditButton.Height = 28;

            KbmPickDivider.Visibility = Visibility.Visible;
            Canvas.SetTop(KbmPickDivider, 116);
            KbmMouseLabel.Visibility = Visibility.Visible;
            KbmMouseHost.Visibility = Visibility.Visible;
            Canvas.SetTop(KbmMouseLabel, 124);
            Canvas.SetTop(KbmMouseHost, 140);
            KbmPadLabel.Text = "常用键";
            KbmPadLabel.Visibility = Visibility.Visible;
            Canvas.SetTop(KbmPadLabel, 176);
            Canvas.SetTop(KbmPadHost, 192);
            KbmPadHost.Height = 96;
        }
        else
        {
            KbmPickTitle.Text = "选择目标";
            KbmPickTitle.Visibility = Visibility.Visible;
            KbmPickSubtitle.Text = source + " · " + BindingLabels.Trigger(_kbmTrigger);
            KbmPickSubtitle.Visibility = Visibility.Visible;
            KbmComboHead.Visibility = Visibility.Collapsed;

            KbmCaptureButton.Visibility = Visibility.Visible;
            KbmClearButton.Visibility = Visibility.Visible;
            KbmCaptureHint.Visibility = Visibility.Visible;
            KbmPartnerEditButton.Visibility = Visibility.Collapsed;
            Canvas.SetLeft(KbmPartnerEditButton, 240);
            Canvas.SetTop(KbmPartnerEditButton, 72);
            KbmPartnerEditButton.Width = 96;
            KbmPartnerEditButton.Height = 30;

            KbmPickDivider.Visibility = Visibility.Visible;
            Canvas.SetTop(KbmPickDivider, 112);
            KbmMouseLabel.Visibility = Visibility.Visible;
            KbmMouseHost.Visibility = Visibility.Visible;
            Canvas.SetTop(KbmMouseLabel, 116);
            Canvas.SetTop(KbmMouseHost, 132);
            KbmPadLabel.Text = "常用键";
            KbmPadLabel.Visibility = Visibility.Visible;
            Canvas.SetTop(KbmPadLabel, 168);
            Canvas.SetTop(KbmPadHost, 184);
            KbmPadHost.Height = 102;
        }

        BuildKbmPick();
        KbmPickView.Visibility = Visibility.Visible;
        KeyboardHost.Visibility = Visibility.Collapsed;
        MouseOutView.Visibility = Visibility.Collapsed;
        KbmTitle.Visibility = Visibility.Collapsed;
        KbmOutputLabel.Visibility = Visibility.Collapsed;
    }

    /// <summary>组合键抬头：只放符号 —— 当前键图标 + 「+」+ 伙伴键图标，不再跟文字。</summary>
    private void BuildComboChip()
    {
        KbmComboChip.Children.Clear();
        string partner = ResolvePartner();
        if (string.IsNullOrEmpty(partner)) partner = "?";
        AddChipIcon(KbmComboChip, _kbmSource, 32, "T1");
        KbmComboChip.Children.Add(ComboChipPlus());
        AddChipIcon(KbmComboChip, partner, 32, "AccentInk");
    }

    /// <summary>组合键选择页抬头：当前键图标 + 「+」，表示还差一颗要补。</summary>
    private void BuildPartnerChip()
    {
        KbmPartnerChip.Children.Clear();
        AddChipIcon(KbmPartnerChip, _kbmSource, 30, "T1");
        KbmPartnerChip.Children.Add(ComboChipPlus());
    }

    private void AddChipIcon(System.Windows.Controls.Panel host, string code, double size, string fallbackBrush)
    {
        WpfImage? icon = IconImage(PadIconOf(code), size);
        if (icon is not null)
        {
            icon.Margin = new Thickness(0, 0, 10, 0);
            host.Children.Add(icon);
            return;
        }
        host.Children.Add(ComboChipText(BindingLabels.Pad(code, _layout), 20, fallbackBrush));
    }

    private TextBlock ComboChipPlus() => ComboChipText("+", 18, "T4");

    private TextBlock ComboChipText(string text, double size, string brushKey) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeights.Bold,
        Foreground = (SolidColorBrush)FindResource(brushKey),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0)
    };

    /// <summary>组合键：选一颗键和当前键一起按。复用同一块面板，只留按键图标。</summary>
    private void ShowKbmPartnerPick()
    {
        _pickingPartner = true;

        KbmPickTitle.Text = "选择组合键";
        KbmPickSubtitle.Visibility = Visibility.Collapsed;
        KbmPickDivider.Visibility = Visibility.Collapsed;
        KbmComboHead.Visibility = Visibility.Collapsed;
        BuildPartnerChip();
        KbmPartnerChip.Visibility = Visibility.Visible;
        KbmCaptureButton.Visibility = Visibility.Collapsed;
        KbmClearButton.Visibility = Visibility.Collapsed;
        KbmCaptureHint.Visibility = Visibility.Collapsed;
        KbmPartnerEditButton.Visibility = Visibility.Collapsed;
        KbmMouseLabel.Visibility = Visibility.Collapsed;
        KbmMouseHost.Visibility = Visibility.Collapsed;
        KbmPadLabel.Text = "接着按的键";
        KbmPadLabel.Visibility = Visibility.Visible;

        Canvas.SetTop(KbmPadLabel, 92);
        Canvas.SetTop(KbmPadHost, 108);
        KbmPadHost.Height = 150;

        KbmMouseHost.Children.Clear();
        KbmPadHost.Children.Clear();
        int slot = 0;
        foreach (string code in KbmPartnerKeys)
        {
            if (code == _kbmSource) continue;
            AddIconChip(KbmPadHost, slot++, code, code, PadIconOf(code), 8, 30);
        }

        KbmPadHost.Visibility = Visibility.Visible;
        KbmPickView.Visibility = Visibility.Visible;
        KeyboardHost.Visibility = Visibility.Collapsed;
        MouseOutView.Visibility = Visibility.Collapsed;
        KbmTitle.Visibility = Visibility.Collapsed;
        KbmOutputLabel.Visibility = Visibility.Collapsed;
    }

    private void HideKbmPick()
    {
        if (KbmPickView.Visibility != Visibility.Visible) return;
        _capturing = false;
        _pickingPartner = false;
        KbmPickView.Visibility = Visibility.Collapsed;
        KbmComboHead.Visibility = Visibility.Collapsed;
        KbmPickTitle.Visibility = Visibility.Visible;
        KbmPartnerChip.Visibility = Visibility.Collapsed;
        KbmCaptureHint.Visibility = Visibility.Visible;
        KbmPartnerEditButton.Visibility = Visibility.Collapsed;
        KbmTitle.Visibility = Visibility.Visible;
        KbmOutputLabel.Visibility = Visibility.Visible;
        UpdateKbmOutput();
    }

    private void KbmPickCancel_Click(object sender, RoutedEventArgs e) => HideKbmPick();

    private string? PadIconOf(string source) => SourceIcon(source);

    private void BuildKbmPick()
    {
        KbmMouseHost.Children.Clear();
        KbmPadHost.Children.Clear();

        for (int i = 0; i < KbmMouseTargets.Length; i++)
        {
            BindingTarget target = KbmMouseTargets[i].Target;
            AddIconChip(KbmMouseHost, i, target.Code, target, TargetIcon(target, TargetLayout), 8, 30);
        }

        for (int i = 0; i < KbmCommonKeys.Length; i++)
        {
            BindingTarget target = BindingTarget.Key(KbmCommonKeys[i].Code);
            AddIconChip(KbmPadHost, i, KbmCommonKeys[i].Label, target, TargetIcon(target, TargetLayout), 8, 30);
        }
    }

    /// <summary>目标胶囊：有图标就只放图标，没有图标才用文字。</summary>
    private void AddIconChip(Canvas host, int index, string label, object tag,
        string? icon, int columns, double height)
    {
        const double cw = 84, gx = 6, gy = 6;
        int col = index % columns, row = index / columns;

        bool selected = tag is BindingTarget t && IsCurrentTarget(t);

        object content;
        WpfImage? image = IconImage(icon, height - 4);
        if (image is not null)
        {
            image.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            image.VerticalAlignment = VerticalAlignment.Center;
            content = image;
        }
        else
        {
            content = new TextBlock { Text = label, FontSize = 10 };
        }

        var button = new Button
        {
            Content = content,
            Width = cw,
            Height = height,
            ToolTip = label,
            Style = (Style)FindResource(selected ? "ChipOn" : "Chip"),
            Tag = tag
        };
        button.Click += _pickingPartner ? KbmPartnerItem_Click : KbmPickItem_Click;
        Canvas.SetLeft(button, col * (cw + gx));
        Canvas.SetTop(button, row * (height + gy));
        host.Children.Add(button);
    }

    private bool IsCurrentTarget(BindingTarget target)
    {
        BindingTarget current = _kbmTrigger == BindingTrigger.Combo
            ? _profile.GetCombo(_kbmSource).Target
            : _profile.Get(_kbmSource, _kbmTrigger);
        return current.Kind == target.Kind && current.Code == target.Code;
    }

    private void KbmPickItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        if (b.Tag is BindingTarget target) ApplyKbmTarget(target);
    }

    /// <summary>搭子格：Tag 存的是源键名，不是目标。</summary>
    private void KbmPartnerItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string code) return;
        if (string.IsNullOrEmpty(code)) return;

        _comboPartner = code;
        _kbmTrigger = BindingTrigger.Combo;

        // 已经有组合目标时只换同按键，不必重设目标。
        (BindingTarget existing, _) = _profile.GetCombo(_kbmSource);
        if (!existing.IsNone)
        {
            _profile.SetCombo(_kbmSource, existing, code);
            MarkDirty();
            RefreshKbmSlots();
            ShowKbmPick();
            return;
        }

        // 还没有组合目标：选完同按键直接接着选目标。
        RefreshKbmSlots();
        ShowKbmPick();
    }

    private void KbmClear_Click(object sender, RoutedEventArgs e) => ApplyKbmTarget(BindingTarget.None);

    /// <summary>写入当前触发位。键鼠只有轻按能连带锁住双击/长按。</summary>
    private void ApplyKbmTarget(BindingTarget target)
    {
        if (_kbmTrigger == BindingTrigger.Combo)
        {
            _profile.SetCombo(_kbmSource, target, ResolvePartner());
        }
        else
        {
            _profile.Set(_kbmSource, _kbmTrigger, target);
            if (_kbmTrigger == BindingTrigger.Tap && target.IsNone)
            {
                _profile.ClearSecondaryTriggers(_kbmSource);
                _kbmTrigger = BindingTrigger.Tap;
            }
        }

        MarkDirty();
        HideKbmPick();
        RefreshKbmSlots();
    }

    // ── 键鼠：捕获按键 ─────────────────────────────────────────────
    private void CaptureKbm_Click(object sender, RoutedEventArgs e)
    {
        Focus();
        Keyboard.Focus(this);
        _capturing = true;
        KbmCaptureHint.Text = "按下要绑定的键…（Esc 取消）";
    }

    private void BindingsPage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _capturing = false;
            KbmCaptureHint.Text = "点「捕获」后按键盘上的键";
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        _capturing = false;
        ApplyKbmTarget(BindingTarget.Key(key.ToString()));
        KbmCaptureHint.Text = "点「捕获」后按键盘上的键";
    }
}
