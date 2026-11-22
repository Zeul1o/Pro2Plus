using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Pro2Plus.UI.Pages;

namespace Pro2Plus;

/// <summary>
/// 一比一复刻版外壳：1060×840 固定设计画布、左侧四项导航、逐页页脚。
/// 页面实例懒加载并缓存，旧 .cs 逻辑通过 MainTabs.SelectedIndex 继续联动。
/// </summary>
public partial class MainWindow
{
    private static readonly string[] PageTitles = { "连接", "按键映射", "陀螺仪与震动", "设置" };

    private readonly UIElement?[] pageCache = new UIElement?[4];

    // 设计稿导出的两组原生光场（含 Figma 自己的高斯模糊与 dithering）。
    // A：01 连接 / 03 陀螺仪（双蓝 blob）；B：02 映射 / 04 设置（蓝+青+紫三 blob）。
    private static readonly BitmapImage LightA = LoadLight("LightA.png");
    private static readonly BitmapImage LightB = LoadLight("LightB.png");

    private static BitmapImage LoadLight(string file)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new System.Uri(
            "pack://application:,,,/Assets/Light/" + file, System.UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private sealed class NavEntry
    {
        public string Label { get; init; } = "";
        public double Y { get; init; }
    }

    /// <summary>构造里在 InitializeComponent() 之后、设置 SelectedIndex 之前调用。</summary>
    private void InitShell()
    {
        MainTabs.ItemsSource = new[]
        {
            new NavEntry { Label = "连接", Y = 100 },
            new NavEntry { Label = "按键映射", Y = 140 },
            new NavEntry { Label = "陀螺仪与震动", Y = 180 },
            new NavEntry { Label = "设置", Y = 220 }
        };
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageHost is null || MainTabs is null) return;
        int index = MainTabs.SelectedIndex;
        if (index < 0 || index >= PageTitles.Length) return;

        PageTitleText.Text = PageTitles[index];
        // 页脚左右两栏改为绑定 VM 实时状态（映射模式 / USB·蓝牙），不再随 tab 写死文本。
        if (GlowLayer is not null) GlowLayer.Source = (index == 0 || index == 2) ? LightA : LightB;
        if (GlowPurple is not null)
            GlowPurple.Visibility = (index == 0 || index == 2) ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Content = GetPage(index);
        if (Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_ADV") == "1"
            && PageHost.Content is GyroPage gp)
        {
            // 冒烟可延迟展开，便于连拍抓过渡帧
            if (int.TryParse(Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_ADV_DELAY"), out int advDelay)
                && advDelay > 0)
            {
                var t = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(advDelay)
                };
                t.Tick += (_, _) => { t.Stop(); gp.OpenAdvancedForSmoke(); };
                t.Start();
            }
            else gp.OpenAdvancedForSmoke();
        }
    }

    private UIElement GetPage(int index)
    {
        if (pageCache[index] is { } cached) return cached;

        UIElement page;
        try
        {
            page = index switch
            {
                0 => new ConnectPage(),
                1 => new BindingsPage(),
                2 => new GyroPage(),
                _ => new SettingsPage()
            };
        }
        catch (System.Exception ex)
        {
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(System.AppContext.BaseDirectory, "nav-error.txt"),
                    "index=" + index + "\r\n" + ex);
            }
            catch
            {
                // 诊断文件写不进去也不能影响导航。
            }
            page = new PlaceholderPage(PageTitles[index], "页面加载失败：" + ex.Message);
        }

        pageCache[index] = page;
        return page;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

}
