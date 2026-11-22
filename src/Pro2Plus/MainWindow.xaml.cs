using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Pro2Plus;

public partial class MainWindow : Window
{
    private Forms.NotifyIcon? trayIcon;
    private Forms.ContextMenuStrip? trayMenu;
    private bool shutdownInProgress;
    private bool shutdownComplete;
    private bool trayTipShown;
    private readonly DispatcherTimer presentationTimer = new(DispatcherPriority.Background)
    { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        InitShell();
        MainTabs.SelectedIndex = 0;
        DataContext = new MainViewModel(IsUiSmokeRun());
        presentationTimer.Tick += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.RefreshCompactTelemetry();
                vm.RefreshLinkTelemetry();
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            UpdatePresentationRefresh();
        };
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged += MainViewModel_PropertyChanged;
            viewModel.UserNotificationRequested += MainViewModel_UserNotificationRequested;
        }
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        if (!IsUiSmokeRun()) CreateTrayIcon();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int enabled = 1;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            }

            // Win11：标题栏直接取页面底色，避免深色界面顶着一条浅色栏。
            // COLORREF 是 0x00BBGGRR，#08090C → 0x000C0908。
            int caption = unchecked((int)0x000C0908);
            _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            int captionText = unchecked((int)0x00FBF9F7);
            _ = DwmSetWindowAttribute(handle, 36, ref captionText, sizeof(int));
        }
        catch
        {
            // Dark title bars are cosmetic; unsupported Windows builds keep the system default.
        }
    }

    private void CreateTrayIcon()
    {
        // 托盘三件事：左键打开主界面、右键弹菜单、最小化时给一条气泡提示。
        trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Pro2+",
            Visible = true
        };

        // 右键菜单：导出连接诊断（回传排障用）+ 退出；模式切换仍在主界面做，避免后台误起设备。
        trayMenu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F)
        };
        var exportItem = new Forms.ToolStripMenuItem("导出连接诊断…");
        exportItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ExportConnectionDiag));
        trayMenu.Items.Add(exportItem);
        trayMenu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ExitFromTray));
        trayMenu.Items.Add(exitItem);
        trayIcon.ContextMenuStrip = trayMenu;

        // 用 MouseUp 判左键：NotifyIcon.Click 在右键时也会触发，会把菜单和主界面一起弹出来。
        trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(new Action(ShowFromTray));
        };

        // 自检：PRO2_TRAY_SELFTEST=1 时导出菜单项并自动点一次「退出」，用来验证托盘退出链路。
        string? selftest = Environment.GetEnvironmentVariable("PRO2_TRAY_SELFTEST");
        if (selftest == "1")
        {
            var items = new System.Text.StringBuilder();
            foreach (Forms.ToolStripItem item in trayMenu.Items) items.Append(item.Text).Append('|');
            string? resultPath = Environment.GetEnvironmentVariable("PRO2_TRAY_SELFTEST_RESULT");
            if (!string.IsNullOrWhiteSpace(resultPath))
                System.IO.File.WriteAllText(resultPath, items.ToString());
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                exitItem.PerformClick();
            };
            timer.Start();
        }
    }

    /// <summary>托盘「导出连接诊断」：合成一个可直接回传的 txt，并在资源管理器里选中它。</summary>
    private void ExportConnectionDiag()
    {
        try
        {
            string path = ConnectionDiag.Export();
            ConnectionDiag.Reveal(path);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "导出诊断失败：" + ex.Message,
                "Pro2+", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>托盘「退出」：走和关窗同一条关闭流程（先停设备再退进程），窗口隐藏时也能退。</summary>
    private void ExitFromTray()
    {
        Close();
    }

    /// <summary>
    /// 首次运行引导：虚拟手柄依赖 USBIP 驱动，而单文件发布把安装包嵌在 exe 里。
    /// 检测不到 usbip.exe 就问一次，同意后解出自带安装包静默/引导安装，装完即可用。
    /// </summary>
    private void PromptInstallUsbipIfMissing()
    {
        try
        {
            if (UsbipRuntimeLocator.Find(null) != null) return;
        }
        catch
        {
            return; // 探测失败别在启动时弹错误，进页面后仍可手动「安装/修复 USBIP」
        }

        MessageBoxResult answer = System.Windows.MessageBox.Show(
            this,
            "这是首次运行，需要安装 USBIP 驱动（虚拟手柄通过它挂载，只需装一次）。\n\n" +
            "安装包已经内置在这个 exe 里，不需要联网；安装时会弹一次管理员确认，装完就可以直接进入游戏。\n\n" +
            "现在就安装吗？（选“否”可稍后在设置页手动安装）",
            "Pro2+ · 首次运行",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;
        if (DataContext is MainViewModel viewModel && viewModel.InstallUsbipCommand.CanExecute(null))
            viewModel.InstallUsbipCommand.Execute(null);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdatePresentationRefresh();
        if (!IsUiSmokeRun()) FitStartupWindow();
        if (DataContext is MainViewModel initial) initial.RefreshCompactTelemetry();
        if (IsUiSmokeRun())
        {
            // 冒烟模式可选指定初始页签，便于逐页核对版式。
            if (int.TryParse(Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_TAB"), out int tabIndex) &&
                tabIndex >= 0 && tabIndex < MainTabs.Items.Count)
            {
                MainTabs.SelectedIndex = tabIndex;
            }
            // 可选指定输出模式（Pro2 / Xbox / PS5 / Kbm），便于核对映射页联动视图。
            var smokeMode = Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_MODE");
            if (!string.IsNullOrWhiteSpace(smokeMode) && DataContext is MainViewModel modeVm)
            {
                modeVm.SelectCompactModeCommand.Execute(smokeMode);
            }
            return;
        }

        if (DataContext is MainViewModel viewModel)
        {
            // 单文件发布：驱动安装包内嵌在 exe 里，首次运行先问一次，
            // 别等用户点了「进入游戏」才发现驱动缺失。
            PromptInstallUsbipIfMissing();

            if (Array.Exists(Environment.GetCommandLineArgs(), argument =>
                string.Equals(argument, "--connect-pro2", StringComparison.OrdinalIgnoreCase)))
            {
                await viewModel.ConnectForLaunchAsync();
                if (Environment.GetCommandLineArgs().Contains("--ble-diagnostic-after-connect", StringComparer.OrdinalIgnoreCase))
                    await viewModel.DiagnoseForLaunchAsync();
            }
            else
                await viewModel.RunStartupAutomationAsync();
        }
    }

    private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 新版外壳用固定栅格，不需要在这里重算布局。
    }

    private void MainViewModel_UserNotificationRequested(string title, string message)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (trayIcon != null)
            {
                trayIcon.BalloonTipTitle = title;
                trayIcon.BalloonTipText = message;
                trayIcon.ShowBalloonTip(5000);
            }
            else
            {
                System.Windows.MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }));
    }

    private void RefreshSteamControllerCache_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            this,
            "刷新会先正常拔出当前虚拟手柄，然后请求 Steam 正常退出并重新打开。运行中的 Steam 游戏可能阻止退出，请先保存游戏进度。程序不会强制结束 Steam。是否继续？",
            "刷新 Steam 控制器缓存",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.RefreshSteamControllerCacheCommand.CanExecute(null))
        {
            viewModel.RefreshSteamControllerCacheCommand.Execute(null);
        }
    }

    private async void RestartBluetooth_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.CanOperate) return;
        await vm.RequestBluetoothRestartAsync(targets =>
        {
            var dialog = new BluetoothRestartWindow(targets) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.SelectedTarget : null;
        });
    }

    private void FitStartupWindow()
    {
        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var area = screen.WorkingArea;
        Rect bounds = StartupWindowLayout.Fit(new Rect(area.Left / dpi.DpiScaleX,
            area.Top / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY));
        MinWidth = Math.Min(720, bounds.Width); MinHeight = Math.Min(560, bounds.Height);
        MaxWidth = area.Width / dpi.DpiScaleX; MaxHeight = area.Height / dpi.DpiScaleY;
        Width = bounds.Width; Height = bounds.Height;
        Left = bounds.Left; Top = bounds.Top;
    }

    private void OneClickWin10Optimize_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            this,
            viewModel.Win10OneClickConfirmation,
            "一键优化 Win10 蓝牙",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        ExecuteConfirmedCommand(answer, vm => vm.OneClickWin10OptimizeCommand);
    }

    private void ConfigureBleTurbo_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        bool experimentalConsent = viewModel.IsWin10BleExperimental;
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            this,
            viewModel.Win10BleConfigurationConfirmation,
            "应用并测试 Win10 BLE 预设",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        ExecuteConfirmedCommand(answer, vm => vm.ConfigureBleTurboCommand, experimentalConsent);
    }

    private void ExecuteConfirmedCommand(
        MessageBoxResult answer,
        Func<MainViewModel, System.Windows.Input.ICommand> selector,
        object? commandParameter = null)
    {
        if (answer != MessageBoxResult.Yes || DataContext is not MainViewModel viewModel)
        {
            return;
        }
        System.Windows.Input.ICommand command = selector(viewModel);
        if (command.CanExecute(commandParameter))
        {
            command.Execute(commandParameter);
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            string? exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon != null)
                {
                    return icon;
                }
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdatePresentationRefresh();
        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        if (IsUiSmokeRun())
        {
            return;
        }

        Hide();
        if (!trayTipShown && trayIcon != null)
        {
            trayTipShown = true;
            trayIcon.BalloonTipTitle = "Pro2+ 仍在后台运行";
            trayIcon.BalloonTipText = "点击托盘图标可以回到主界面。";
            trayIcon.ShowBalloonTip(2500);
        }
    }

    private void ShowFromTray()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Show();
        UpdatePresentationRefresh();
        Activate();
    }

    private void UpdatePresentationRefresh()
    {
        if (shutdownInProgress || !IsVisible || WindowState == WindowState.Minimized)
        {
            presentationTimer.Stop();
            return;
        }
        if (!presentationTimer.IsEnabled)
        {
            // Hidden time is not a UI rate window. Resume from cached live evidence.
            if (DataContext is MainViewModel vm)
            {
                vm.ResetCompactTelemetrySample();
                vm.RefreshCompactTelemetry();
            }
            presentationTimer.Start();
        }
    }

    private static bool IsUiSmokeRun()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("PRO2_UI_SMOKE"),
            "1",
            StringComparison.Ordinal);
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (shutdownComplete)
        {
            return;
        }

        e.Cancel = true;
        if (shutdownInProgress)
        {
            return;
        }

        shutdownInProgress = true;
        try
        {
            if (DataContext is MainViewModel viewModel)
            {
                await viewModel.ShutdownAsync();
            }
        }
        finally
        {
            shutdownComplete = true;
            shutdownInProgress = false;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Close));
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        presentationTimer.Stop();
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= MainViewModel_PropertyChanged;
            viewModel.UserNotificationRequested -= MainViewModel_UserNotificationRequested;
        }
        trayIcon?.Dispose();
        trayIcon = null;
        trayMenu?.Dispose();
        trayMenu = null;
        System.Windows.Application.Current.Shutdown(0);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int valueSize);
}


