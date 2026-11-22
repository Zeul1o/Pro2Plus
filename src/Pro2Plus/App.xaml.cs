using System;
using System.Threading;
using System.Windows;

namespace Pro2Plus;

public partial class App : System.Windows.Application
{
    private Mutex? singleInstanceMutex;
    private bool ownsSingleInstanceMutex;

    public App()
    {
        // Before OnStartup, MainWindow.InitializeComponent, or tray creation.
        // A dedicated compatibility EXE enables this without asking a user whose
        // normal window crashes to find an in-app setting.
        RenderingStartupPolicy.Apply(Environment.GetCommandLineArgs());
        HookDiagnostics();
    }

    /// <summary>未处理异常也写进连接诊断日志，回传时能看到崩溃发生在哪个连接阶段之后。</summary>
    private void HookDiagnostics()
    {
        DispatcherUnhandledException += (_, args) =>
            ConnectionDiag.Crash("ui_dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ConnectionDiag.Crash("app_domain", ex);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            ConnectionDiag.Crash("unobserved_task", args.Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (AdapterRestartCommand.IsCommand(e.Args))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunBluetoothRestartHelperAsync(e.Args);
            return; // Must precede the manager mutex and stale-process cleanup.
        }
        if (BleStackPresetTool.IsCommand(e.Args))
        {
            base.OnStartup(e);
            PresetCommandOutcome result = BleStackPresetTool.Execute(e.Args);
            System.Windows.MessageBox.Show(
                result.Message,
                result.Success ? "Pro2+" : "Pro2+ · 操作失败",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            int exitCode = result.Success ? 0 : 1;
            Shutdown(exitCode);
            return;
        }

        bool bleDiagnosticCli = Array.Exists(
            e.Args,
            argument => string.Equals(
                argument,
                "--ble-diagnostic",
                StringComparison.OrdinalIgnoreCase));
        bool pureSoftware133 = Array.Exists(
            e.Args,
            argument => string.Equals(
                argument,
                "--pure-software-133",
                StringComparison.OrdinalIgnoreCase));
        if (pureSoftware133)
        {
            SoftwareOnlyMode.EnableForCurrentProcess();
        }
        bool uiSmoke = Environment.GetEnvironmentVariable("PRO2_UI_SMOKE") == "1";

        // 映射判定自检：不接手柄，用合成帧核对轻按 / 双击 / 长按的仲裁。
        if (Environment.GetEnvironmentVariable("PRO2_ENGINE_TEST") == "1")
        {
            string report = Pro2Plus.Mapping.KbmMappingEngineSmoke.Run();
            string? reportPath = Environment.GetEnvironmentVariable("PRO2_ENGINE_TEST_OUT");
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(reportPath)!);
                System.IO.File.WriteAllText(reportPath, report);
            }
            int exit = report.Contains("FAIL", StringComparison.Ordinal) ? 3 : 0;
            Environment.ExitCode = exit;
            Shutdown(exit);
            return;
        }

        // 震动保持节奏自检（离线，合成时间轴）
        if (Environment.GetEnvironmentVariable("PRO2_RUMBLE_HOLD_TEST") == "1")
        {
            string holdReport = RumbleHoldContinuitySmoke.Run();
            string? holdPath = Environment.GetEnvironmentVariable("PRO2_RUMBLE_HOLD_TEST_OUT");
            if (!string.IsNullOrWhiteSpace(holdPath))
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(holdPath)!);
                System.IO.File.WriteAllText(holdPath, holdReport);
            }
            int holdExit = holdReport.Contains("summary=PASS", StringComparison.Ordinal) ? 0 : 3;
            Environment.ExitCode = holdExit;
            Shutdown(holdExit);
            return;
        }

        // 输出选项目录自检（常驻，离线）
        if (Environment.GetEnvironmentVariable("PRO2_OPTIONS_TEST") == "1")
        {
            string optionsReport = OutputOptionsSmoke.Run();
            string? optionsPath = Environment.GetEnvironmentVariable("PRO2_OPTIONS_TEST_OUT");
            if (!string.IsNullOrWhiteSpace(optionsPath))
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(optionsPath)!);
                System.IO.File.WriteAllText(optionsPath, optionsReport);
            }
            int optionsExit = optionsReport.Contains("summary=PASS", StringComparison.Ordinal) ? 0 : 3;
            Environment.ExitCode = optionsExit;
            Shutdown(optionsExit);
            return;
        }

        // 辅助模块重写差分自检（常驻，离线）
        if (Environment.GetEnvironmentVariable("PRO2_AUX_TEST") == "1")
        {
            string auxReport = AuxiliaryRewriteSmoke.Run();
            string? auxPath = Environment.GetEnvironmentVariable("PRO2_AUX_TEST_OUT");
            if (!string.IsNullOrWhiteSpace(auxPath))
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(auxPath)!);
                System.IO.File.WriteAllText(auxPath, auxReport);
            }
            int auxExit = auxReport.Contains("summary=PASS", StringComparison.Ordinal) ? 0 : 3;
            Environment.ExitCode = auxExit;
            Shutdown(auxExit);
            return;
        }

        // 摇杆链路自检：不接手柄，用合成的 0x05 报告核对零位 / 死区的最终输出。
        if (Environment.GetEnvironmentVariable("PRO2_STICK_TEST") == "1")
        {
            string stickReport = StickPipelineSmoke.Run();
            string? stickPath = Environment.GetEnvironmentVariable("PRO2_STICK_TEST_OUT");
            if (!string.IsNullOrWhiteSpace(stickPath))
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(stickPath)!);
                System.IO.File.WriteAllText(stickPath, stickReport);
            }
            int stickExit = stickReport.Contains("FAIL", StringComparison.Ordinal) ? 3 : 0;
            Environment.ExitCode = stickExit;
            Shutdown(stickExit);
            return;
        }

        singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: uiSmoke ? @"Local\PRO2UiSmoke" : @"Global\Pro2PlusManager",
            createdNew: out bool createdNew);
        ownsSingleInstanceMutex = createdNew;
        if (!ownsSingleInstanceMutex)
        {
            try
            {
                ownsSingleInstanceMutex = singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                ownsSingleInstanceMutex = true;
            }
        }

        if (!ownsSingleInstanceMutex)
        {
            if (bleDiagnosticCli)
            {
                Environment.ExitCode = 6;
                Shutdown(6);
                Environment.Exit(6);
                return;
            }

            System.Windows.MessageBox.Show(
                "Pro2+ 已在运行。请点击托盘图标打开；若正在退出，请稍后再试。",
                "Pro2+",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        // Acquire the shared mutex before stale-process cleanup. A second click
        // must not terminate the live manager and drop its BLE connection.
        if (!bleDiagnosticCli && !uiSmoke)
        {
            StartupProcessGuard.CleanupConflictingProcesses();

            // 上次异常退出可能留下 VIIPER 和它的空手柄，先收干净再开界面。
            OrphanViiperCleanup.Cleanup();
        }
        base.OnStartup(e);
        if (bleDiagnosticCli)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunBleDiagnosticCliAsync(e.Args);
            return;
        }

        Connection.LinkDebug.Enabled = Connection.LinkDebug.Requested(AppContext.BaseDirectory);
        Connection.LinkDebug.Open("PRO2 Controller Assistant");
        Connection.LinkDebug.Write("APP START uiSmoke=" + uiSmoke +
                                   " args=" + string.Join(" ", e.Args) +
                                   " log=" + Connection.LinkDebug.FilePath);

        MainWindow window = new();
        if (uiSmoke) window.ShowActivated = false;
        MainWindow = window;
        // 开机自启走 --tray：托盘图标在窗口构造时已创建，这里不 Show()，
        // 用户点托盘再进入主界面。OnExplicitShutdown 由 MainWindow_Closed 收尾。
        bool trayLaunch = Array.Exists(e.Args, argument =>
            string.Equals(argument, LaunchAtLoginEntry.TrayStartArgument, StringComparison.OrdinalIgnoreCase));
        if (trayLaunch)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Connection.LinkDebug.Write("APP START tray_launch=1 window_shown=0");
        }
        else
        {
            window.Show();
        }
        // 探针要等用户配对，不能被冒烟的自动关窗打断。
        bool stickProbe = Environment.GetEnvironmentVariable("PRO2_STICK_PROBE") == "1";
        if (uiSmoke && !stickProbe) _ = FinishUiSmokeAsync(window);
        if (Environment.GetEnvironmentVariable("PRO2_LINK_CONNECT") == "1")
            _ = ProbeLinkAsync(window);
        if (stickProbe) _ = ProbeStickAsync(window);
    }

    /// <summary>
    /// 诊断用：连上手柄后静置采样若干帧，把四个轴的零位偏移与抖动写到文件。
    /// 用来判断漂移是硬件零位偏了还是软件没做死区，不参与正常运行。
    /// </summary>
    private static async System.Threading.Tasks.Task ProbeStickAsync(MainWindow window)
    {
        string? path = Environment.GetEnvironmentVariable("PRO2_STICK_PROBE_OUT");
        if (string.IsNullOrWhiteSpace(path) || window.DataContext is not MainViewModel vm) return;

        try
        {
            int waitSeconds = int.TryParse(
                Environment.GetEnvironmentVariable("PRO2_STICK_PROBE_WAIT"), out int waitSec)
                ? Math.Clamp(waitSec, 0, 600) : 120;
            int seconds = int.TryParse(Environment.GetEnvironmentVariable("PRO2_STICK_PROBE_SEC"), out int sec)
                ? Math.Clamp(sec, 1, 120) : 2;

            async System.Threading.Tasks.Task Status(string text)
            {
                try { await System.IO.File.WriteAllTextAsync(path, text); } catch { }
            }

            string head = "PROBE wait=" + waitSeconds + "s sample=" + seconds + "s\r\n";
            await Status(head + "WAITING 0s link=none\r\n");
            await vm.LinkService.ConnectAsync();

            // 配对窗口：蓝牙要先开软件再长按 2 秒，等 1.2 秒根本来不及。
            // 这里主动等链路出帧，拿到再开始采样；链路失败就自动重连（蓝牙一轮扫描只有 10 秒）。
            IGamepadRealtimeInputSource? realtime = null;
            Connection.Pro2InputSession? session = null;
            bool gotFrame = false;
            long lastStatus = -1000;
            long lastReconnect = long.MinValue;
            var waitClock = System.Diagnostics.Stopwatch.StartNew();
            while (waitClock.ElapsedMilliseconds < waitSeconds * 1000)
            {
                session = vm.LinkService.InputSession;
                realtime = session as IGamepadRealtimeInputSource;
                if (realtime is not null &&
                    realtime.TryGetNewest(out _, out TimeSpan age, out _) &&
                    age < TimeSpan.FromSeconds(2))
                {
                    gotFrame = true;
                    break;
                }

                if (session is null && waitClock.ElapsedMilliseconds - lastReconnect >= 8000)
                {
                    lastReconnect = waitClock.ElapsedMilliseconds;
                    await vm.LinkService.ConnectAsync();
                }

                if (waitClock.ElapsedMilliseconds - lastStatus >= 500)
                {
                    lastStatus = waitClock.ElapsedMilliseconds;
                    await Status(head +
                        "WAITING " + (waitClock.ElapsedMilliseconds / 1000) + "s link=" +
                        (session is null ? "none" : "session-no-frame") + "\r\n");
                }
                await System.Threading.Tasks.Task.Delay(100);
            }

            if (!gotFrame || realtime is null)
            {
                await Status(head + "NO_FRAMES link=" + (session is null ? "none" : "session-no-frame"));
                return;
            }

            await Status(head + "SAMPLING " + DateTime.Now.ToString("HH:mm:ss") + "\r\n");

            int n = 0;
            var minLx = ushort.MaxValue; var maxLx = ushort.MinValue;
            var minLy = ushort.MaxValue; var maxLy = ushort.MinValue;
            var minRx = ushort.MaxValue; var maxRx = ushort.MinValue;
            var minRy = ushort.MaxValue; var maxRy = ushort.MinValue;
            long sumLx = 0, sumLy = 0, sumRx = 0, sumRy = 0;

            // 每 500ms 打一行均值，用来看零位会不会随时间（发热）走。
            var buckets = new System.Collections.Generic.List<string>();
            long bSumRx = 0, bSumRy = 0; int bN = 0;
            long bucketStart = 0;

            // PRO2_STICK_PROBE_RAW=1：采**原始值**而不是归一化输出。
            // 这是唯一能分清「摇杆物理漂」与「软件零位没跟上」的采法，
            // 归一化输出会被死区剪掉残差，看不出来。
            bool rawMode = Environment.GetEnvironmentVariable("PRO2_STICK_PROBE_RAW") == "1";
            long bSumLx = 0, bSumLy = 0;

            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < seconds * 1000)
            {
                // 只统计新到的那一帧：age 偏大说明还是上一帧，重复累加会把均值刷偏。
                if (realtime.TryGetNewest(out GamepadState s, out TimeSpan frameAge, out _) &&
                    frameAge <= TimeSpan.FromMilliseconds(20))
                {
                    ushort lx = s.Lx, ly = s.Ly, rx = s.Rx, ry = s.Ry;
                    if (rawMode && session?.RawStickAxes is { } rawAxes)
                    {
                        lx = rawAxes.Lx; ly = rawAxes.Ly; rx = rawAxes.Rx; ry = rawAxes.Ry;
                    }

                    n++;
                    sumLx += lx; sumLy += ly; sumRx += rx; sumRy += ry;
                    bSumLx += lx; bSumLy += ly; bSumRx += rx; bSumRy += ry; bN++;
                    if (lx < minLx) minLx = lx; if (lx > maxLx) maxLx = lx;
                    if (ly < minLy) minLy = ly; if (ly > maxLy) maxLy = ly;
                    if (rx < minRx) minRx = rx; if (rx > maxRx) maxRx = rx;
                    if (ry < minRy) minRy = ry; if (ry > maxRy) maxRy = ry;
                }
                if (deadline.ElapsedMilliseconds - bucketStart >= 500)
                {
                    if (bN > 0)
                        buckets.Add(
                            (rawMode ? "raw-bucket " : "bucket ") + bucketStart + "ms  Lx=" + (bSumLx / bN) +
                            "  Ly=" + (bSumLy / bN) + "  Rx=" + (bSumRx / bN) +
                            "  Ry=" + (bSumRy / bN) + "  frames=" + bN);
                    bSumLx = bSumLy = bSumRx = bSumRy = 0; bN = 0;
                    bucketStart = deadline.ElapsedMilliseconds;
                }
                await System.Threading.Tasks.Task.Delay(4);
            }

            double Avg(long sum) => n == 0 ? 0 : (double)sum / n;
            string Unit(double raw) => ((raw - GamepadState.AxisCenter) / 2047.0).ToString("F5");
            string Line(string name, long sum, ushort lo, ushort hi) =>
                name + " avg=" + Avg(sum).ToString("F2") + " min=" + (n == 0 ? 0 : lo) +
                " max=" + (n == 0 ? 0 : hi) + " span=" + (n == 0 ? 0 : hi - lo) +
                " unit=" + (n == 0 ? "0" : Unit(Avg(sum)));

            await System.IO.File.WriteAllTextAsync(path,
                "frames=" + n + "\r\n" +
                Line("Lx", sumLx, minLx, maxLx) + "\r\n" +
                Line("Ly", sumLy, minLy, maxLy) + "\r\n" +
                Line("Rx", sumRx, minRx, maxRx) + "\r\n" +
                Line("Ry", sumRy, minRy, maxRy) + "\r\n" +
                "axis " + (session is null ? "none" : session.StickDiagnostic) + "\r\n" +
                string.Join("\r\n", buckets) + "\r\n");
        }
        catch (Exception ex)
        {
            try { await System.IO.File.WriteAllTextAsync(path, "exception " + ex.GetType().Name + ": " + ex.Message); } catch { }
        }
        finally
        {
            window.Close();
            System.Windows.Application.Current?.Shutdown();
        }
    }

    /// <summary>诊断用：跑一次完整连接流程，把最终状态写到文件，便于没有界面时核对。</summary>
    private static async System.Threading.Tasks.Task ProbeLinkAsync(MainWindow window)
    {
        string? path = Environment.GetEnvironmentVariable("PRO2_LINK_RESULT");
        if (string.IsNullOrWhiteSpace(path) || window.DataContext is not MainViewModel vm) return;

        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await vm.LinkService.ConnectAsync();
        }
        catch (Exception ex)
        {
            await System.IO.File.WriteAllTextAsync(path, "exception " + ex.GetType().Name + ": " + ex.Message);
            return;
        }

        Connection.Pro2LinkSnapshot snapshot = vm.LinkService.Snapshot;
        await System.IO.File.WriteAllTextAsync(path,
            "kind=" + snapshot.Kind + "\r\n" +
            "phase=" + snapshot.Phase + "\r\n" +
            "title=" + snapshot.Title + "\r\n" +
            "detail=" + snapshot.Detail + "\r\n" +
            "elapsed_ms=" + started.ElapsedMilliseconds + "\r\n");
    }

    private async System.Threading.Tasks.Task FinishUiSmokeAsync(MainWindow window)
    {
        // 冒烟模式默认 300ms 后关闭；设 PRO2_UI_SMOKE_HOLD=秒数 可以让窗口停住，
        // 便于截图核对版式（不影响正常运行路径）。
        int holdSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_HOLD"), out int parsed)
            ? Math.Clamp(parsed, 1, 300) : 0;
        // 冒烟截图：PRO2_UI_SMOKE_SHOT=<路径> 时截取当前窗口，用于核对高亮渲染。
        // bits 提前注入，让 50ms 的背景轮询 timer 有时间点亮后再截图。
        string? shot = Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_SHOT");
        string? smokeBits = Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_BITS");
        if (ulong.TryParse(smokeBits, out ulong sb)) MainViewModel.SmokeHighlightButtons = sb;
        // 冒烟截图：PRO2_UI_SMOKE_AXES="lx,ly,rx,ry" 注入摇杆轴值，核对位置点
        string? smokeAxes = Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_AXES");
        if (!string.IsNullOrWhiteSpace(smokeAxes))
        {
            string[] parts = smokeAxes.Split(',');
            if (parts.Length == 4 &&
                ushort.TryParse(parts[0], out ushort lx) && ushort.TryParse(parts[1], out ushort ly) &&
                ushort.TryParse(parts[2], out ushort rx) && ushort.TryParse(parts[3], out ushort ry))
                MainViewModel.SmokeAxes = (lx, ly, rx, ry);
        }
        // 冒烟截图：PRO2_UI_SMOKE_TRIG="lt,rt" 注入扳机行程，核对大标识的 L2/R2 图标
        string? smokeTrig = Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_TRIG");
        if (!string.IsNullOrWhiteSpace(smokeTrig))
        {
            string[] parts = smokeTrig.Split(',');
            if (parts.Length == 2 &&
                ushort.TryParse(parts[0], out ushort lt) && ushort.TryParse(parts[1], out ushort rt))
                MainViewModel.SmokeTriggers = (lt, rt);
        }
        // 冒烟截图：PRO2_UI_SMOKE_CALIB=center|range|gyro + PRO2_UI_SMOKE_CALIB_STATE=idle|running|done
        string? calibKind = Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_CALIB");
        if (!string.IsNullOrWhiteSpace(calibKind) && window.DataContext is MainViewModel smokeVm)
        {
            smokeVm.ApplyCalibrationPreview(
                calibKind,
                Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_CALIB_STATE") ?? "idle");
        }
        await System.Threading.Tasks.Task.Delay(holdSeconds > 0 ? holdSeconds * 1000 : 300);
        if (!string.IsNullOrWhiteSpace(shot))
        {
            try
            {
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)window.ActualWidth, (int)window.ActualHeight, 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(window);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(shot)!);
                using var fs = System.IO.File.Create(shot);
                enc.Save(fs);
            }
            catch { }
        }
        string? path = Environment.GetEnvironmentVariable("PRO2_UI_SMOKE_RESULT");
        if (!string.IsNullOrWhiteSpace(path))
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllTextAsync(path,
                "UI_LOADED=true\r\n" +
                "VERSION=" + System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                    typeof(App).Assembly)?.InformationalVersion + "\r\n" +
                "UI_RENDERING=" + RenderingStartupPolicy.Telemetry + "\r\n" +
                "GYRO_QUALITY=" + QualityFeatureFlags.GyroEnabled + "\r\n" +
                "HAPTICS_QUALITY=" + QualityFeatureFlags.HapticsEnabled + "\r\n" +
                "BATTERY_INIT_SNAPSHOT=" + InitialBatterySnapshot.IsRequested(Environment.GetCommandLineArgs()) + "\r\n" +
                "RUNNING=" + ((MainViewModel)window.DataContext).Running + "\r\n" +
                "MODE=" + ((MainViewModel)window.DataContext).SelectedModeLabel + "\r\n" +
                "LINK_PROBE=" + DescribeWiredProbe() + "\r\n" +
                "MAP_SELFTEST=" + RunMappingSelfTest() + "\r\n");
        }
        window.Close();
    }

    /// <summary>冒烟用：跑一遍映射表的「写盘 → 读回 → 比对」，验证持久化闭环。</summary>
    private static string RunMappingSelfTest()
    {
        try
        {
            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pro2-bindings-selftest.json");
            Mapping.MappingProfile profile = Mapping.MappingProfile.CreateDefault();
            profile.Set("GL", Mapping.BindingTrigger.Long, Mapping.BindingTarget.Key("Space"));
            profile.Set("A", Mapping.BindingTrigger.Double, Mapping.BindingTarget.Mouse("Wheel"));
            profile.FaceLayout = "pro2";   // ABXY 对调开关要能存盘

            if (!Mapping.MappingStore.Save(profile, temp)) return "save_failed";

            Mapping.MappingProfile back = Mapping.MappingStore.Load(temp);
            bool ok = back.Get("GL", Mapping.BindingTrigger.Long).Code == "Space"
                      && back.Get("A", Mapping.BindingTrigger.Double).Kind == Mapping.BindingTargetKind.MouseAxis
                      && back.Get("GL", Mapping.BindingTrigger.Tap).Code == Mapping.PadButtons.B
                      && back.Get("GL", Mapping.BindingTrigger.Double).IsNone
                      && back.FaceLayout == "pro2";

            // ABXY 对调：四种组合都按「丝印字母」发出去
            //   物理 NS + Xbox 输出：按下盘面底部（丝印 B）→ 发 Xbox 的 B（East）
            //   物理 NS + NS 输出  ：同上 → 发 NS 的 B（South）
            //   物理 Xbox + Xbox 输出：底部是 A → South
            //   物理 Xbox + NS 输出  ：底部是 A → NS 的 A（East）
            Mapping.MappingProfile pad = Mapping.MappingProfile.CreateIdentityDefault("xbox");
            Mapping.PadMappingEngine.SetProfile(pad);
            bool FaceCase(string face, string plat, bool expectEast)
            {
                Mapping.PadMappingEngine.SetFaceLayout(face);
                Mapping.PadMappingEngine.SetPlatform(plat);
                var press = new GamepadState { Buttons = GamepadButtons.South };
                GamepadButtons got = Mapping.PadMappingEngine.Apply(press).Buttons;
                return expectEast ? got == GamepadButtons.East : got == GamepadButtons.South;
            }
            bool abxyOk = FaceCase("pro2", "xbox", true)
                && FaceCase("pro2", "pro2", false)
                && FaceCase("xbox", "xbox", false)
                && FaceCase("xbox", "pro2", true);
            Mapping.PadMappingEngine.SetProfile(null);
            Mapping.PadMappingEngine.SetFaceLayout("xbox");
            Mapping.PadMappingEngine.SetPlatform("xbox");

            return ok && abxyOk ? "ok" : (ok ? "abxy_mismatch" : "mismatch");
        }
        catch (Exception ex)
        {
            return "error " + ex.GetType().Name;
        }
    }

    /// <summary>冒烟用：报告 libusb 是否可用、有没有插着 057E:2069。</summary>
    private static string DescribeWiredProbe()
    {
        try
        {
            Connection.Pro2LinkProbe probe = Connection.Pro2WiredLink.Probe();
            return (probe.Found ? "found:" + probe.Count : "none") + " " + probe.Detail;
        }
        catch (Exception ex)
        {
            return "error " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    private async System.Threading.Tasks.Task RunBleDiagnosticCliAsync(string[] args)
    {
        int exitCode = await BleDiagnosticCli.RunAsync(args);
        Shutdown(exitCode);
        Environment.Exit(exitCode);
    }

    private async System.Threading.Tasks.Task RunBluetoothRestartHelperAsync(string[] args)
    {
        int exitCode;
        try { exitCode = await AdapterRestartCommand.ExecuteAsync(args); }
        catch { exitCode = 5; }
        Shutdown(exitCode);
        Environment.Exit(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Connection.LinkDebug.Write("APP EXIT code=" + e.ApplicationExitCode +
                                   " at=" + DateTime.Now.ToString("HH:mm:ss.fff"));
        if (ownsSingleInstanceMutex)
        {
            try
            {
                singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
