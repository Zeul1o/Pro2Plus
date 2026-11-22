using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Pro2Plus.Connection;

namespace Pro2Plus;

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private const string UsbipPortableInstallHint =
        "如果已经安装在其他目录，请到“设置 → USBIP”选择现有 usbip.exe，无需先重复安装。";
    private const int MaxUiLogCharacters = 120000;
    private const int TrimmedUiLogCharacters = 70000;
    private readonly StringBuilder log = new();
    private readonly object logGate = new();
    private bool logDirty;
    private readonly bool previewOnly;
    private CancellationTokenSource? currentOperationCts;
    private bool enterGamePending;
    private int resumeGameQueued;
    private readonly Pro2ControllerSlot[] pro2Slots =
    [
        new(1, true),
        new(2, false),
        new(3, false),
        new(4, false)
    ];
    private readonly long[] lastDisconnectNoticeSequence = [-1, -1, -1, -1];
    private long lastOfflineNoticeTicks;
    private readonly FrameSignal[] reconnectWake = [new(), new(), new(), new()];
    private readonly long[] reconnectObservedTicks = new long[4];
    private readonly SynchronizationContext? uiContext = SynchronizationContext.Current;
    private readonly Pro2BleInputSource inputSource;
    private readonly ProfessionalHidAuditController professionalHidAuditController = new();
    private readonly SessionLogWriter sessionLog;
    private readonly AppSettings userSettings;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly SemaphoreSlim bleOperationGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCts = new();
    private CancellationTokenSource? autoReconnectCts;
    private Task? autoReconnectTask;
    private Process? viiperProcess;
    private int? localViiperApiPort;
    private OutputPadSession? session;
    private string host = "127.0.0.1";
    private string port = "3242";
    private string status = "Pro2+ 已就绪。";
    private string inputStatus = "真实 Pro2 BLE 输入未连接。";
    private string selectedPushRateLabel = OutputRateOption.Default.Label;
    private string selectedBackendLabel = VirtualBackendOption.Default.Label;
    private string selectedStickProcessingLabel = StickProcessingOption.Default.Label;
    private bool runtimeBatteryRefreshEnabled = true;
    private bool running;
    private bool busy;
    private bool shuttingDown;
    private bool autoReconnectEnabled;
    private bool launchAtLoginEnabled;
    private bool autoConnectOnPairingEnabled;
    private bool autoReconnectOnStartupEnabled;
    private bool startupAutomationActive;
    private bool startupAutomationPaused;
    private bool startupAutomationRan;
    private bool startupAutomationNoticeSent;
    private bool steamGhostNoticeSent;
    private DateTimeOffset? startupAutomationDeadlineUtc;
    private string runtimeReadinessText = "";
    private string professionalBiasStatusText =
        "Bias: NotCalibrated / source=none / applied=false / updates=0 / raw=0,0,0";
    private string professionalRawGyroText = "Raw Gyro raw: X=0 Y=0 Z=0";
    private string professionalCorrectedGyroText = "Corrected Gyro Preview (°/s): Pitch Rate=0 Yaw Rate=0 Roll Rate=0";
    private string professionalOutputGyroText = "Output Gyro (°/s): Pitch Rate=0 Yaw Rate=0 Roll Rate=0";
    private string professionalIntegratedAngleText = "Integrated Angle (°): Pitch Angle=0 Yaw Angle=0 Roll Angle=0 · integral_state=Disabled · integral_running=false";
    private string professionalNinetyDegreeTestText = "90° test idle.";
    private string selectedProfessionalHidAuditMode = ProfessionalHidAuditMode.Normal.ToString();
    private string professionalStaticGyroXRaw = "0";
    private string professionalStaticGyroYRaw = "0";
    private string professionalStaticGyroZRaw = "0";
    private string professionalHidAuditStatusText = "HID Audit: Normal · final report gyro follows selected_output_ds_raw.";
    private double rumbleMultiplier = 1.0;
    private double ps5GyroScale = 1.0;
    private bool professionalInvertGyroPitch;
    private bool professionalInvertGyroYaw;
    private bool professionalInvertGyroRoll;
    private ViiperVirtualMode selectedMode = ViiperVirtualMode.DualSenseLike;
    private ViiperVirtualMode? activeMode;
    private const int LostInputTimeoutMilliseconds = 2000;

    public MainViewModel(bool previewOnly = false)
    {
        this.previewOnly = previewOnly;
        linkService.Log = AppendLog;
        sessionLog = new SessionLogWriter(previewOnly
            ? Path.Combine(Path.GetTempPath(), "PRO2-ui-smoke-logs") : null);
        userSettings = previewOnly ? new AppSettings() : AppSettings.Load();
        // 陀螺零偏跟随设置页同一份文件持久化：校准一次，下次连接直接加载。
        Pro2GyroBiasStore.Configure(userSettings, allowPersist: !previewOnly);
        void RefreshGyroBiasText()
        {
            System.Windows.Application? app = System.Windows.Application.Current;
            void Raise()
            {
                OnPropertyChanged(nameof(GyroBiasSavedText));
                OnPropertyChanged(nameof(GyroBiasCalibrationState));
            }
            if (app == null || app.CheckAccess()) Raise();
            else app.Dispatcher.BeginInvoke(new Action(Raise));
        }
        Pro2GyroBiasStore.BiasSaved += RefreshGyroBiasText;
        Pro2GyroBiasStore.DeviceKeyChanged += _ => RefreshGyroBiasText();

        // C 键切换陀螺仪后落盘成功，设置页的草稿和开关一起跟上；
        // 这样重启、换有线/无线、重建会话都沿用最后一次切换的状态。
        GyroConfig.AllowPersist = !previewOnly;
        GyroConfig.StartEnabledPersisted += enabled =>
        {
            tuningGyroStartEnabled = enabled;
            if (tuningConfig != null) tuningConfig.Gyro.StartEnabled = enabled;
            System.Windows.Application? app = System.Windows.Application.Current;
            void Raise() => OnPropertyChanged(nameof(TuningGyroStartEnabled));
            if (app == null || app.CheckAccess()) Raise();
            else app.Dispatcher.BeginInvoke(new Action(Raise));
        };
        linkService.InputSessionChanged += OnLinkInputSessionChanged;
        InitializeRearButtonSettings();
        inputSource = pro2Slots[0].InputSource;

        InitializeBleCompatibilitySettings();
        InitializeMultiControllerSafety();
        inputSource.ConnectionStateChanged += OnInputConnectionStateChanged;
        foreach (Pro2ControllerSlot slot in pro2Slots)
        {
            int slotIndex = slot.Index;
            slot.InputSource.DisconnectDetected +=
                signal => OnPro2DisconnectDetected(slotIndex, signal);
        }
        PingCommand = new ActionCommand(_ => RunExclusiveAsync("Ping VIIPER", PingAsync));
        InstallUsbipCommand = new ActionCommand(_ => RunExclusiveAsync("安装/修复 usbip-win2", InstallUsbipAsync));
        InitializeUsbipPathCommands();
        StartViiperServerCommand = new ActionCommand(_ => RunExclusiveAsync("启动本地 VIIPER", StartLocalViiperServerAsync));
        ScanPro2InputCommand = new ActionCommand(_ => RunExclusiveAsync("扫描 Pro2 BLE", ScanPro2InputAsync));
        ConnectPro2InputCommand = new ActionCommand(_ => RunExclusiveAsync("进入游戏", token =>
        {
            manualReconnectTarget = null;
            return EnterGameAsync(token);
        }));
        DisconnectPro2InputCommand = new ActionCommand(_ => RunExclusiveAsync("断开 Pro2 BLE", DisconnectPro2InputAsync));
        CalibrateSourceGyroCommand = new ActionCommand(_ => RunExclusiveAsync("校准真实 Pro2 陀螺仪", CalibrateSourceGyroAsync));
        FixAudioDefaultsCommand = new ActionCommand(_ => RunExclusiveAsync("修复音频默认设备", FixAudioDefaultsAsync));
        RunWin10BleDiagnosticCommand = new ActionCommand(_ => RunExclusiveAsync(
            "Win10 BLE Turbo 诊断",
            RunWin10BleDiagnosticAsync));
        ConfigureBleTurboCommand = new ActionCommand(parameter => RunExclusiveAsync(
            "应用并实测 Win10 BLE 参数",
            token => RunBleTurboConfiguratorAsync(restore: false, token,
                experimentalConsent: parameter is true)));
        RestoreBleTurboCommand = new ActionCommand(_ => RunExclusiveAsync(
            "恢复 Win10 BLE 参数",
            token => RunBleTurboConfiguratorAsync(restore: true, token)));
        DumpControllerEnumerationCommand = new ActionCommand(_ => RunExclusiveAsync("设备枚举诊断", DumpControllerEnumerationAsync));
        CleanupStaleVirtualDevicesCommand = new ActionCommand(_ => RunExclusiveAsync("清理残留虚拟设备", CleanupStaleVirtualDevicesAsync));
        RefreshSteamControllerCacheCommand = new ActionCommand(_ => RunExclusiveAsync("刷新 Steam 控制器缓存", RefreshSteamControllerCacheAsync));
        ExportDiagnosticsLogCommand = new ActionCommand(_ => RunExclusiveAsync("导出诊断包", ExportDiagnosticsLogAsync));
        ScanPro2SlotCommand = new ActionCommand(parameter => RunExclusiveAsync(
            "扫描 Pro2 Slot",
            token => ScanPro2SlotAsync(SlotFromParameter(parameter), token)));
        ConnectPro2SlotCommand = new ActionCommand(parameter => RunExclusiveAsync(
            "连接 Pro2 Slot",
            token => StartPro2SlotAutoReconnectAsync(SlotFromParameter(parameter), token)));
        DisconnectPro2SlotCommand = new ActionCommand(parameter => RunExclusiveAsync(
            "断开 Pro2 Slot",
            token => DisconnectPro2SlotAsync(SlotFromParameter(parameter), token)));
        CalibrateStickCenterSlotCommand = new ActionCommand(parameter => RunExclusiveAsync(
            "校准 Pro2 摇杆零位",
            token => CalibrateStickCenterSlotAsync(SlotFromParameter(parameter), token)));
        CalibrateStickRangeSlotCommand = new ActionCommand(parameter => RunExclusiveAsync(
            "校准 Pro2 摇杆完整行程",
            token => CalibrateStickRangeSlotAsync(SlotFromParameter(parameter), token)));
        StartDualSenseCommand = new ActionCommand(_ => RunExclusiveAsync(
            "切换 PS5 模式",
            token => SwitchModeAsync(ViiperDeviceProfile.DualSenseLike, token)));
        StartPro1Command = new ActionCommand(_ => RunExclusiveAsync(
            "切换 Pro1 / Switch Pro",
            token => SwitchModeAsync(ViiperDeviceProfile.Pro1, token)));
        StartDualSenseProfessionalImuCommand = new ActionCommand(_ => RunExclusiveAsync(
            "切换 PS5 Professional IMU Test",
            token => SwitchModeAsync(ViiperDeviceProfile.DualSenseProfessionalImuTest, token)));
        StartPro2Command = new ActionCommand(_ => RunExclusiveAsync(
            "切换 Pro2 / Nintendo",
            token => SwitchModeAsync(ViiperDeviceProfile.Pro2, token)));
        StartXboxCommand = new ActionCommand(_ => RunExclusiveAsync(
            "切换 Xbox / XInput",
            token => SwitchModeAsync(ViiperDeviceProfile.Xbox, token)));
        StartXboxProfessionalImuCommand = new ActionCommand(_ => RunExclusiveAsync(
            "切换 Xbox Professional IMU Test",
            token => SwitchModeAsync(ViiperDeviceProfile.XboxProfessionalImuTest, token)));
        CalibrateGyroBiasCommand = new ActionCommand(_ =>
            ExecuteProfessionalImuAction("Calibrate Gyro Bias 3s", s => s.StartGyroBiasCalibration()));
        ResetGyroBiasCommand = new ActionCommand(_ =>
            ExecuteProfessionalImuAction("Reset Gyro Bias", s => s.ResetGyroBias()));
        ResetIntegralCommand = new ActionCommand(_ =>
            ExecuteProfessionalImuAction("Reset Integral", s => s.ResetProfessionalIntegral()));
        StartPitch90TestCommand = new ActionCommand(_ =>
            ExecuteProfessionalImuAction("Start Pitch 90° Test", s => s.StartNinetyDegreeTest(ProfessionalImuTestAxis.Pitch)));
        StartYaw90TestCommand = new ActionCommand(_ =>
            ExecuteProfessionalImuAction("Start Yaw 90° Test", s => s.StartNinetyDegreeTest(ProfessionalImuTestAxis.Yaw)));
        StartRoll90TestCommand = new ActionCommand(_ =>
            ExecuteProfessionalImuAction("Start Roll 90° Test", s => s.StartNinetyDegreeTest(ProfessionalImuTestAxis.Roll)));
        Stop90TestCommand = new ActionCommand(_ =>
            ExecuteProfessionalImuAction("Stop 90° Test", s => s.StopNinetyDegreeTest()));
        PulseGyroXCommand = new ActionCommand(_ => StartProfessionalHidPulse("X"));
        PulseGyroYCommand = new ActionCommand(_ => StartProfessionalHidPulse("Y"));
        PulseGyroZCommand = new ActionCommand(_ => StartProfessionalHidPulse("Z"));
        StopCommand = new ActionCommand(_ => RunExclusiveAsync("停止虚拟设备", _ => StopAsync()));
        ClearLogCommand = new ActionCommand(_ => ClearLog());
        rumbleMultiplier =
            AppSettings.NormalizeRumbleMultiplier(
                userSettings.RumbleMultiplier);
        ps5GyroScale =
            AppSettings.NormalizePs5GyroScale(
                userSettings.Ps5GyroScalePitch);
        professionalInvertGyroPitch = userSettings.ProfessionalInvertGyroPitch;
        professionalInvertGyroYaw = userSettings.ProfessionalInvertGyroYaw;
        professionalInvertGyroRoll = userSettings.ProfessionalInvertGyroRoll;
        selectedPushRateLabel =
            OutputRateOption.FromLabel(userSettings.PushRateLabel).Label;
        if (SoftwareOnlyMode.IsEnabled)
        {
            selectedPushRateLabel = OutputRateOption.Default.Label;
        }
        selectedBackendLabel =
            VirtualBackendOption.FromLabel(userSettings.BackendLabel).Label;
        selectedStickProcessingLabel =
            StickProcessingOption.FromLabel(userSettings.StickProcessingLabel).Label;
        runtimeBatteryRefreshEnabled = userSettings.RuntimeBatteryRefreshEnabled;
        launchAtLoginEnabled = !previewOnly && userSettings.LaunchAtLoginEnabled && LaunchAtLoginEntry.IsEnabled();
        autoConnectOnPairingEnabled = userSettings.AutoConnectOnPairingEnabled;
        linkService.AutoConnectOnPairing = autoConnectOnPairingEnabled;
        autoReconnectOnStartupEnabled = userSettings.AutoReconnectOnStartupEnabled;
        if (launchAtLoginEnabled)
        {
            // 老版本写的启动项没有 --tray，登录后仍会弹主界面。这里补一次参数，
            // 让「自启即进托盘」对存量用户也生效。
            try { LaunchAtLoginEntry.SetEnabled(true); }
            catch { /* 注册表不可写时保持原状，用户可在设置页重开关一次 */ }
        }
        if (userSettings.LaunchAtLoginEnabled != launchAtLoginEnabled)
        {
            userSettings.LaunchAtLoginEnabled = launchAtLoginEnabled;
            SaveUserSettings("[STARTUP]");
        }
        AppendLog("[MODE_PARSE] cfg_key=\"" + userSettings.SelectedModeKey +
                  "\" normalized=\"" + AppSettings.NormalizeModeKey(userSettings.SelectedModeKey) +
                  "\" resolved=\"" + ViewModelPolicies.ModeKey(ViewModelPolicies.ModeFromKey(userSettings.SelectedModeKey)) +
                  "\" options=" + string.Join(",", SlotModeCatalog.Options.Select(o => o.Key)) +
                  "\" slots=" + string.Join(",", userSettings.SlotModeKeys ?? []));
        selectedMode = ViewModelPolicies.ModeFromKey(userSettings.SelectedModeKey);
        inputSource.SetRumbleGain(rumbleMultiplier);
        inputSource.SetStickProcessingMode(SelectedStickProcessingOption.Mode);
        ApplyPro2SlotRuntimeOptions();
        if (!previewOnly)
        {
            RefreshRuntimeReadiness();
            _ = RefreshRuntimeReadinessAsync();
        }
        AppendLog("[SESSION_LOG] manager_log=disabled diag=connection_only");
        AppendLog("[UI_RENDERING] " + RenderingStartupPolicy.Telemetry);
        if (!string.IsNullOrWhiteSpace(StartupProcessGuard.LastSummary))
        {
            AppendLog(StartupProcessGuard.LastSummary);
        }
        AppendLog("Pro2+ v" + SystemBluetoothDiagnostics.CurrentAppVersion + "：Pro2 / Pro1 / Xbox / PS5 · 游戏按键图标安装与还原。");
        AppendLog("[BLE_INIT_POLICY] default=no_pair_finalize pairing_finalize_15_03=blocked comparison_checkbox=skip_usb_init next_connection_only=true");
        AppendLog("[FEATURE_POLICY] gyro_quality=" + QualityFeatureFlags.GyroEnabled +
            " haptics_quality=" + QualityFeatureFlags.HapticsEnabled +
            " battery_init_snapshot=" + InitialBatterySnapshot.IsRequested(Environment.GetCommandLineArgs()) +
            " runtime_battery_refresh=" + RuntimeBatteryRefreshEnabled +
            " runtime_battery_interval_s=" + RuntimeBatteryRefreshPolicy.IntervalSeconds +
            " audio_guard=built_in_always_on");
        AppendLog("[DATA_PATH_POLICY] source_push=fresh_only pro2_usb=4ms_zero_order_hold " +
            "pro2_usb_repeats_are_not_fd2=true other_modes=source_paced " +
            "digital_edges=bounded_fifo ps5_invalid_gravity=positive_y " +
            "stick=source_calibrated_no_extra_center_snap pcm=two_peak_lossy");
        AppendLog("[BLE_COMPATIBILITY] " + EnvironmentRouteInspector.Current.Telemetry);
        AppendLog("[LOG_POLICY] manager_log=disabled connection_diag=Documents/Pro2+/Logs " +
                  "recent_status=in_memory_only; VIIPER server log level=info.");
        AppendLog("[RUNTIME] " + RuntimeReadinessText);
        AppendLog("[RUMBLE_GAIN] multiplier=" + rumbleMultiplier.ToString("F1"));
        AppendLog("[STARTUP] launch_at_login=" + launchAtLoginEnabled +
                  " auto_reconnect_on_startup=" + autoReconnectOnStartupEnabled +
                  " selected_mode=" + ViewModelPolicies.ModeKey(selectedMode));
        if (SoftwareOnlyMode.IsEnabled)
        {
            AppendLog("[PURE_SOFTWARE_133] enabled=true input_min_hz=" +
                      SoftwareOnlyMode.MinimumAcceptedHz.ToString("F1") +
                      " gameplay_min_hz=60 compatibility_mode=true source_push=fresh_only pro2_usb=protocol_resample repeated_state_limit=" +
                      SoftwareOnlyMode.MaximumRepeatedStateRatio.ToString("F3"));
        }
        AppendLog("[LINK_TUNING] cadence=" + ViewModelPolicies.PushCadenceTelemetry(SelectedPushRateOption) +
                  " gyro_mode=" + ImuResampleOption.Default.Label +
                  " ps5_imu_map=" + Ps5ImuMappingOption.Default.Mapping.TelemetryValue +
                  " ps5_output_imu=" + SelectedPs5OutputImuTuning.TelemetryValue +
                  " gyro_axis_inv=" + default(GyroAxisInversion).TelemetryValue +
                  " stick=" + SelectedStickProcessingOption.Label +
                  " audio_guard=" + AudioEndpointGuardEnabled +
                  " backend=" + SelectedBackendOption.Label);
        InitializeEnvironmentInfo();
    }

    public string Host
    {
        get => host;
        set { host = value; OnPropertyChanged(); }
    }

    public string Port
    {
        get => port;
        set { port = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => status;
        set { status = value; OnPropertyChanged(); }
    }

    public string InputStatus
    {
        get => inputStatus;
        set { inputStatus = value; OnPropertyChanged(); }
    }

    public bool Running
    {
        get => running;
        set
        {
            running = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(CanManageRuntime));
            OnPropertyChanged(nameof(ModeHeadline));
            RefreshCompactPresentation();
        }
    }

    public bool Busy
    {
        get => busy;
        private set
        {
            busy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(CanOperate));
            OnPropertyChanged(nameof(CanManageRuntime));
            OnPropertyChanged(nameof(CanManualBleControl));
            OnPropertyChanged(nameof(CanTryWin11Compatibility));
            (ApplyRearButtonsCommand as ActionCommand)?.RefreshCanExecute();
            OnPropertyChanged(nameof(CompactCanConnect));
        }
    }

    public bool CanStart => !Busy;
    public bool CanStop => Running && !Busy;
    public bool CanOperate => !Busy;
    public bool CanManageRuntime => !Running && !Busy;
    public bool CanManualBleControl =>
        !Busy &&
        !AutoReconnectEnabled &&
        pro2Slots.All(slot => !slot.AutoReconnectEnabled);

    public bool AutoReconnectEnabled
    {
        get => autoReconnectEnabled;
        private set
        {
            if (autoReconnectEnabled == value)
            {
                return;
            }

            autoReconnectEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanManualBleControl));
            RaiseConnectionStateChanged();
        }
    }

    public bool LaunchAtLoginEnabled
    {
        get => launchAtLoginEnabled;
        set
        {
            if (launchAtLoginEnabled == value)
            {
                return;
            }

            try
            {
                LaunchAtLoginEntry.SetEnabled(value);
                launchAtLoginEnabled = LaunchAtLoginEntry.IsEnabled();
                userSettings.LaunchAtLoginEnabled = launchAtLoginEnabled;
                SaveUserSettings("[STARTUP]");
                AppendLog("[STARTUP] launch_at_login=" + launchAtLoginEnabled);
            }
            catch (Exception ex)
            {
                AppendLog("[STARTUP] launch_at_login failed: " + ex.Message);
                launchAtLoginEnabled = LaunchAtLoginEntry.IsEnabled();
                userSettings.LaunchAtLoginEnabled = launchAtLoginEnabled;
                SaveUserSettings("[STARTUP]");
                RequestUserNotification("开机自启动设置失败", ViewModelPolicies.FirstLine(ex.Message));
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(StartupAutomationSummary));
        }
    }

    /// <summary>
    /// 常驻监听广播：手柄长按 2 秒进入配对模式后自动连接。
    /// 只在未连接时扫描，连上立刻停——扫描会抢走维持 7.5 ms 连接间隔所需的无线电时间。
    /// </summary>
    public bool AutoConnectOnPairingEnabled
    {
        get => autoConnectOnPairingEnabled;
        set
        {
            if (autoConnectOnPairingEnabled == value) return;
            autoConnectOnPairingEnabled = value;
            userSettings.AutoConnectOnPairingEnabled = value;
            linkService.AutoConnectOnPairing = value;
            SaveUserSettings("[STARTUP]");
            AppendLog("[STARTUP] auto_connect_on_pairing=" + value);
            OnPropertyChanged();
        }
    }

    public bool AutoReconnectOnStartupEnabled
    {
        get => autoReconnectOnStartupEnabled;
        set
        {
            if (autoReconnectOnStartupEnabled == value)
            {
                return;
            }

            autoReconnectOnStartupEnabled = value;
            userSettings.AutoReconnectOnStartupEnabled = value;
            SaveUserSettings("[STARTUP]");
            AppendLog("[STARTUP] auto_reconnect_on_startup=" + value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(StartupAutomationSummary));
        }
    }

    public bool RuntimeBatteryRefreshEnabled
    {
        get => runtimeBatteryRefreshEnabled;
        set
        {
            if (runtimeBatteryRefreshEnabled == value) return;
            runtimeBatteryRefreshEnabled = value;
            userSettings.RuntimeBatteryRefreshEnabled = value;
            foreach (var slot in pro2Slots) slot.InputSource.SetRuntimeBatteryRefreshEnabled(value);
            SaveUserSettings("[BATTERY_RUNTIME]");
            AppendLog("[BATTERY_RUNTIME] user_enabled=" + value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CompactBatteryHint));
        }
    }

    public string StartupAutomationSummary =>
        LaunchAtLoginEnabled || AutoReconnectOnStartupEnabled
            ? "开机自启=" + (LaunchAtLoginEnabled ? "开" : "关") +
              " · 启动自动进入=" + (AutoReconnectOnStartupEnabled ? "开" : "关") +
              " · 上次模式=" + SelectedModeLabel
            : "开机自动化未启用。";

    public string RuntimeReadinessText
    {
        get => runtimeReadinessText;
        private set
        {
            runtimeReadinessText = value;
            OnPropertyChanged();
        }
    }

    public double RumbleMultiplier
    {
        get => rumbleMultiplier;
        set
        {
            double normalized =
                AppSettings.NormalizeRumbleMultiplier(value);
            if (Math.Abs(rumbleMultiplier - normalized) < 0.001)
            {
                return;
            }

            rumbleMultiplier = normalized;
            ApplyPro2SlotRuntimeOptions();
            userSettings.RumbleMultiplier = normalized;
            try
            {
                if (!previewOnly) userSettings.Save();
            }
            catch (Exception ex)
            {
                AppendLog("[RUMBLE_GAIN] settings save warning: " + ex.Message);
            }
            AppendLog("[RUMBLE_GAIN] multiplier=" + normalized.ToString("F1"));
            OnPropertyChanged();
            OnPropertyChanged(nameof(RumbleMultiplierText));
        }
    }

    public string RumbleMultiplierText =>
        rumbleMultiplier.ToString("F1") + "x";

    public double Ps5GyroScale
    {
        get => ps5GyroScale;
        set
        {
            double normalized =
                AppSettings.NormalizePs5GyroScale(value);
            if (Math.Abs(ps5GyroScale - normalized) < 0.001)
            {
                return;
            }

            ps5GyroScale = normalized;
            userSettings.Ps5GyroScalePitch = normalized;
            userSettings.Ps5GyroScaleYaw = normalized;
            userSettings.Ps5GyroScaleRoll = normalized;
            SaveUserSettings("[PS5_IMU]");
            AppendLog("[PS5_IMU] gyro_scale_pitch=" + normalized.ToString("0.##") +
                      " gyro_scale_yaw=" + normalized.ToString("0.##") +
                      " gyro_scale_roll=" + normalized.ToString("0.##") +
                      (Running ? " apply=next_session" : " apply=next_start"));
            if (session is { HasProfessionalImuRuntime: true })
            {
                AppendLog("[PRO_IMU] " + session.ResetProfessionalIntegral() +
                          " reason=ps5_gyro_scale_changed");
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(Ps5GyroScaleText));
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public string Ps5GyroScaleText =>
        ps5GyroScale.ToString("0.00") + "x";

    public bool ProfessionalInvertGyroPitch
    {
        get => professionalInvertGyroPitch;
        set
        {
            if (professionalInvertGyroPitch == value)
            {
                return;
            }

            professionalInvertGyroPitch = value;
            ApplyProfessionalGyroInversionSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfessionalGyroInversionSummary));
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public bool ProfessionalInvertGyroYaw
    {
        get => professionalInvertGyroYaw;
        set
        {
            if (professionalInvertGyroYaw == value)
            {
                return;
            }

            professionalInvertGyroYaw = value;
            ApplyProfessionalGyroInversionSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfessionalGyroInversionSummary));
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public bool ProfessionalInvertGyroRoll
    {
        get => professionalInvertGyroRoll;
        set
        {
            if (professionalInvertGyroRoll == value)
            {
                return;
            }

            professionalInvertGyroRoll = value;
            ApplyProfessionalGyroInversionSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfessionalGyroInversionSummary));
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public string ProfessionalGyroInversionSummary =>
        "Pitch=" + (professionalInvertGyroPitch ? "invert" : "normal") +
        ", Yaw=" + (professionalInvertGyroYaw ? "invert" : "normal") +
        ", Roll=" + (professionalInvertGyroRoll ? "invert" : "normal");

    public IReadOnlyList<string> PushRateChoices =>
        SoftwareOnlyMode.IsEnabled
            ? [OutputRateOption.Default.Label]
            : OutputRateOption.All.Select(o => o.Label).ToArray();

    public IReadOnlyList<string> BackendChoices =>
        VirtualBackendOption.All.Select(o => o.Label).ToArray();

    public IReadOnlyList<string> StickProcessingChoices =>
        StickProcessingOption.All.Select(o => o.Label).ToArray();

    public string SelectedPushRate
    {
        get => selectedPushRateLabel;
        set
        {
            string normalized = SoftwareOnlyMode.IsEnabled
                ? OutputRateOption.Default.Label
                : OutputRateOption.FromLabel(value).Label;
            if (selectedPushRateLabel == normalized)
            {
                return;
            }

            selectedPushRateLabel = normalized;
            userSettings.PushRateLabel = normalized;
            SaveUserSettings("[LINK_TUNING]");
            AppendLog("[LINK_TUNING] cadence=" + ViewModelPolicies.PushCadenceTelemetry(SelectedPushRateOption) +
                      " interval_ms=" + SelectedPushRateOption.Interval.TotalMilliseconds.ToString("F1") +
                      (Running ? " apply=next_session" : " apply=next_start"));
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public string SelectedBackend
    {
        get => selectedBackendLabel;
        set
        {
            string normalized = VirtualBackendOption.FromLabel(value).Label;
            if (selectedBackendLabel == normalized)
            {
                return;
            }

            selectedBackendLabel = normalized;
            userSettings.BackendLabel = normalized;
            SaveUserSettings("[BACKEND]");
            AppendLog("[BACKEND] selected=" + SelectedBackendOption.Label +
                      " mode=" + SelectedBackendOption.Mode +
                      (Running ? " apply=next_session" : " apply=next_start"));
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    public string SelectedStickProcessing
    {
        get => selectedStickProcessingLabel;
        set
        {
            string normalized = StickProcessingOption.FromLabel(value).Label;
            if (selectedStickProcessingLabel == normalized)
            {
                return;
            }

            selectedStickProcessingLabel = normalized;
            userSettings.StickProcessingLabel = normalized;
            ApplyPro2SlotRuntimeOptions();
            SaveUserSettings("[STICK_MODE]");
            AppendLog("[STICK_MODE] selected=" + SelectedStickProcessingOption.Label +
                      " mode=" + SelectedStickProcessingOption.Mode +
                      " apply=immediate");
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkTuningSummary));
        }
    }

    // Legacy settings cannot disable the built-in PS5 default-endpoint guard.
    public bool AudioEndpointGuardEnabled => true;

    public string LinkTuningSummary =>
        "push=" + (SelectedPushRateOption.SourcePaced
            ? "真实 BLE 源节奏"
            : SelectedPushRateOption.Hz.ToString("F1") + "Hz") +
        " · ps5_imu=+x+z-y calibrated_state->dsraw scale=" +
        SelectedPs5OutputImuTuning.GyroScalePitch.ToString("0.##") + "x" +
        " · stick=" + SelectedStickProcessingOption.Label +
        " · audio_guard=" + (AudioEndpointGuardEnabled ? "on" : "off") +
        " · backend=" + SelectedBackendOption.Label;

    public IReadOnlyList<Pro2ControllerSlot> Pro2Slots => pro2Slots;

    public bool IsDualSenseSelected => selectedMode == ViiperVirtualMode.DualSenseLike;
    public bool IsPro1Selected => selectedMode == ViiperVirtualMode.Pro1;
    public bool IsDualSenseProfessionalImuSelected => selectedMode == ViiperVirtualMode.DualSenseProfessionalImuTest;
    public bool IsPro2Selected => selectedMode == ViiperVirtualMode.Pro2;
    public bool IsXboxSelected => selectedMode == ViiperVirtualMode.Xbox;
    public bool IsKeyMouseSelected => selectedMode == ViiperVirtualMode.KeyMouse;
    public bool IsKeyMouseActive => Running && activeMode == ViiperVirtualMode.KeyMouse;
    public bool IsXboxProfessionalImuSelected => selectedMode == ViiperVirtualMode.XboxProfessionalImuTest;
    public bool IsDualSenseActive => Running && activeMode == ViiperVirtualMode.DualSenseLike;
    public bool IsPro1Active => Running && activeMode == ViiperVirtualMode.Pro1;
    public bool IsDualSenseProfessionalImuActive => Running && activeMode == ViiperVirtualMode.DualSenseProfessionalImuTest;
    public bool IsPro2Active => Running && activeMode == ViiperVirtualMode.Pro2;
    public bool IsXboxActive => Running && activeMode == ViiperVirtualMode.Xbox;
    public bool IsXboxProfessionalImuActive => Running && activeMode == ViiperVirtualMode.XboxProfessionalImuTest;
    public bool IsInputConnected =>
        inputSource.IsRunning &&
        inputSource.TryGetLatest(out _, out TimeSpan age) &&
        age <= TimeSpan.FromMilliseconds(LostInputTimeoutMilliseconds);
    public string BleButtonText => IsInputConnected
        ? "PRO2 已连接 · 自动守护中"
        : IsPrimarySleeping
            ? "PRO2 已休眠 · 按手柄任意键唤醒"
        : AutoReconnectEnabled
            ? "正在寻找 PRO2 · 自动重连中"
            : "连接 PRO2 · 进入游戏";
    public string BleStateText => IsInputConnected
        ? inputSource.IsPerformanceDegraded
            ? "BLE LIVE · DEGRADED · " + inputSource.LinkRateClass.ToUpperInvariant()
            : "BLE LIVE · INPUT ONLINE · " + inputSource.LinkRateClass.ToUpperInvariant()
        : IsPrimarySleeping
            ? "BLE SLEEPING · PRESS ANY BUTTON TO WAKE"
        : AutoReconnectEnabled
            ? "BLE SEARCHING · AUTO RECONNECT"
            : "BLE STANDBY · WAITING FOR PLAYER";
    public string SelectedModeLabel => SelectedProfile.Label;
    public string SelectedHeroName => selectedMode switch
    {
        ViiperVirtualMode.DualSenseLike => "DUALSENSE COMPATIBLE",
        ViiperVirtualMode.Pro1 => "SWITCH PRO COMPATIBLE",
        ViiperVirtualMode.DualSenseProfessionalImuTest => "DUALSENSE · LAB",
        ViiperVirtualMode.Pro2 => "PRO2 COMPATIBLE",
        ViiperVirtualMode.Xbox => "XINPUT COMPATIBLE",
        ViiperVirtualMode.XboxProfessionalImuTest => "XINPUT · LAB",
        ViiperVirtualMode.KeyMouse => "KEYBOARD + MOUSE",
        _ => ""
    };
    public string SelectedModeSubtitle => selectedMode switch
    {
        ViiperVirtualMode.DualSenseLike => "PS5 兼容模式 · 054C:0CE6",
        ViiperVirtualMode.Pro1 => "SWITCH PRO · 软件背键 · 057E:2009",
        ViiperVirtualMode.DualSenseProfessionalImuTest => "PROFESSIONAL IMU TEST · RAW→G/DPS→DS RAW · HD HAPTIC",
        ViiperVirtualMode.Pro2 => "NINTENDO PROTOCOL · HD RUMBLE · 057E:2069",
        ViiperVirtualMode.Xbox => "XINPUT PROTOCOL · 045E:028E",
        ViiperVirtualMode.XboxProfessionalImuTest => "PROFESSIONAL IMU TEST · DIAGNOSTIC ONLY · XINPUT",
        ViiperVirtualMode.KeyMouse => "HID 键鼠 · 2E8A:0010 键盘 + 2E8A:0011 鼠标",
        _ => ""
    };
    public string ModeHeadline => Running && activeMode.HasValue
        ? "ACTIVE LOADOUT · " + ActiveProfile.Label
        : "SELECTED LOADOUT · " + SelectedModeLabel;

    public string ProfessionalBiasStatusText
    {
        get => professionalBiasStatusText;
        private set { professionalBiasStatusText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 设置页陀螺仪校准的圆点/状态文字：以「已保存档案」为准。
    /// 校准写盘后 BiasSaved 事件会刷新它，避免会话遥测快照滞后导致「已保存却显示未校准」。
    /// 约定串供 CalibrationStatusConverter / CalibrationDotConverter 识别。
    /// </summary>
    public string GyroBiasCalibrationState =>
        Pro2GyroBiasStore.Load().YawPitchCalibrated ? "Calibrated" : "NotCalibrated";

    /// <summary>设置页显示：当前手柄的陀螺零偏是否已写入配置页，以及写入时间。</summary>
    public string GyroBiasSavedText
    {
        get
        {
            Pro2GyroBiasProfile profile = Pro2GyroBiasStore.Load();
            if (!profile.YawPitchCalibrated) return "未保存 · 静置后自动记录";
            string when = DateTimeOffset.TryParse(profile.SavedAtUtc, out DateTimeOffset saved)
                ? saved.ToLocalTime().ToString("MM-dd HH:mm")
                : "";
            string source = profile.Source == "saved" ? "已复用" : "已保存";
            return when.Length > 0 ? source + " · " + when : source;
        }
    }

    private StickCalibrationProfile CurrentStickProfile =>
        Pro2GyroBiasStore.LoadStickCalibration() ?? new StickCalibrationProfile();

    // 状态列已经写了「已校准/未校准」，这里只补一句「下一步该做什么」，不重复状态词。
    public string StickCenterCalibrationText => CurrentStickProfile.CenterCalibrated
        ? "已保存到配置页"
        : "请松开摇杆两秒";

    /// <summary>圆点/状态文字用：与上面那行说明同源，避免出现「绿点+未校准」的自相矛盾。</summary>
    public string StickCenterCalibrationState =>
        CurrentStickProfile.CenterCalibrated ? "Calibrated" : "NotCalibrated";

    public string StickRangeCalibrationState =>
        CurrentStickProfile.RangeCalibrated ? "Calibrated" : "NotCalibrated";

    public string StickRangeCalibrationText => CurrentStickProfile.RangeCalibrated
        ? "可打满行程"
        : "请沿外圈转满";

    /// <summary>PS5 原生陀螺仪映射灵敏度；映射到 DualSense 原生 IMU 数据。</summary>
    public double Ps5GyroSensitivity
    {
        get => userSettings.Ps5NativeGyroSensitivity;
        set
        {
            double next = AppSettings.NormalizeGyroSensitivity(value);
            if (Math.Abs(userSettings.Ps5NativeGyroSensitivity - next) < 1e-9) return;
            userSettings.Ps5NativeGyroSensitivity = next;
            SaveUserSettings("[GYRO_SENS]");
            OnPropertyChanged();
            OnPropertyChanged(nameof(Ps5GyroSensitivityText));
            RefreshActiveGyroSensitivity();
        }
    }

    public string Ps5GyroSensitivityText => Ps5GyroSensitivity.ToString("0.00") + "×";

    /// <summary>Pro1 原生陀螺仪映射灵敏度；映射到 Switch Pro 原生 IMU 数据。</summary>
    public double Pro1GyroSensitivity
    {
        get => userSettings.Pro1NativeGyroSensitivity;
        set
        {
            double next = AppSettings.NormalizeGyroSensitivity(value);
            if (Math.Abs(userSettings.Pro1NativeGyroSensitivity - next) < 1e-9) return;
            userSettings.Pro1NativeGyroSensitivity = next;
            SaveUserSettings("[GYRO_SENS]");
            OnPropertyChanged();
            OnPropertyChanged(nameof(Pro1GyroSensitivityText));
            RefreshActiveGyroSensitivity();
        }
    }

    public string Pro1GyroSensitivityText => Pro1GyroSensitivity.ToString("0.00") + "×";

    /// <summary>插线/蓝牙回连换了 InputSession 时，让运行中的虚拟手柄热切换。</summary>
    private void OnLinkInputSessionChanged(Pro2InputSession? next)
    {
        OutputPadSession? active = session;
        if (next is not null)
            next.SetRumbleGain(rumbleMultiplier);   // 新链路带上当前震动强度设置
        if (active is null || next is null) return;
        active.SetInputSource(next, next);
        AppendLog("[LINK_OUTPUT] 虚拟会话已热切换到 " + next.LinkLabel + " 输入源。");
    }

    private void RefreshActiveGyroSensitivity()
    {
        ViiperVirtualMode mode = activeMode ?? selectedMode;
        Pro2GyroBiasStore.ActiveGyroSensitivity = mode switch
        {
            ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.DualSenseProfessionalImuTest =>
                userSettings.Ps5NativeGyroSensitivity,
            ViiperVirtualMode.Pro1 => userSettings.Pro1NativeGyroSensitivity,
            // Pro2 / Xbox / 键鼠不走原生 IMU 映射，保持 1.0。
            _ => 1.0
        };
    }

    private void RaiseStickCalibrationChanged()
    {
        OnPropertyChanged(nameof(StickCenterCalibrationText));
        OnPropertyChanged(nameof(StickRangeCalibrationText));
        OnPropertyChanged(nameof(StickCenterCalibrationState));
        OnPropertyChanged(nameof(StickRangeCalibrationState));
    }

    public string ProfessionalRawGyroText
    {
        get => professionalRawGyroText;
        private set { professionalRawGyroText = value; OnPropertyChanged(); }
    }

    public string ProfessionalCorrectedGyroText
    {
        get => professionalCorrectedGyroText;
        private set { professionalCorrectedGyroText = value; OnPropertyChanged(); }
    }

    public string ProfessionalOutputGyroText
    {
        get => professionalOutputGyroText;
        private set { professionalOutputGyroText = value; OnPropertyChanged(); }
    }

    public string ProfessionalIntegratedAngleText
    {
        get => professionalIntegratedAngleText;
        private set { professionalIntegratedAngleText = value; OnPropertyChanged(); }
    }

    public string ProfessionalNinetyDegreeTestText
    {
        get => professionalNinetyDegreeTestText;
        private set { professionalNinetyDegreeTestText = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<string> ProfessionalHidAuditModeChoices =>
        Enum.GetNames<ProfessionalHidAuditMode>();

    public string SelectedProfessionalHidAuditMode
    {
        get => selectedProfessionalHidAuditMode;
        set
        {
            if (!Enum.TryParse(value, out ProfessionalHidAuditMode mode))
            {
                return;
            }

            string normalized = mode.ToString();
            if (selectedProfessionalHidAuditMode == normalized)
            {
                return;
            }

            selectedProfessionalHidAuditMode = normalized;
            string summary = professionalHidAuditController.SetMode(mode);
            ProfessionalHidAuditStatusText = "HID Audit: " + summary;
            AppendLog("[PRO_IMU_AUDIT] mode changed " + summary);
            OnPropertyChanged();
        }
    }

    public string ProfessionalStaticGyroXRaw
    {
        get => professionalStaticGyroXRaw;
        set
        {
            professionalStaticGyroXRaw = value;
            ApplyProfessionalStaticGyroRaw();
            OnPropertyChanged();
        }
    }

    public string ProfessionalStaticGyroYRaw
    {
        get => professionalStaticGyroYRaw;
        set
        {
            professionalStaticGyroYRaw = value;
            ApplyProfessionalStaticGyroRaw();
            OnPropertyChanged();
        }
    }

    public string ProfessionalStaticGyroZRaw
    {
        get => professionalStaticGyroZRaw;
        set
        {
            professionalStaticGyroZRaw = value;
            ApplyProfessionalStaticGyroRaw();
            OnPropertyChanged();
        }
    }

    public string ProfessionalHidAuditStatusText
    {
        get => professionalHidAuditStatusText;
        private set { professionalHidAuditStatusText = value; OnPropertyChanged(); }
    }

    public string LogText { get { lock (logGate) return log.ToString(); } }

    public ICommand PingCommand { get; }
    public ICommand InstallUsbipCommand { get; }
    public ICommand StartViiperServerCommand { get; }
    public ICommand ScanPro2InputCommand { get; }
    public ICommand ConnectPro2InputCommand { get; }
    public ICommand DisconnectPro2InputCommand { get; }
    public ICommand CalibrateSourceGyroCommand { get; }
    public ICommand FixAudioDefaultsCommand { get; }
    public ICommand RunWin10BleDiagnosticCommand { get; }
    public ICommand ConfigureBleTurboCommand { get; }
    public ICommand RestoreBleTurboCommand { get; }
    public ICommand DumpControllerEnumerationCommand { get; }
    public ICommand CleanupStaleVirtualDevicesCommand { get; }
    public ICommand RefreshSteamControllerCacheCommand { get; }
    public ICommand ExportDiagnosticsLogCommand { get; }
    public ICommand ScanPro2SlotCommand { get; }
    public ICommand ConnectPro2SlotCommand { get; }
    public ICommand DisconnectPro2SlotCommand { get; }
    public ICommand CalibrateStickCenterSlotCommand { get; }
    public ICommand CalibrateStickRangeSlotCommand { get; }
    public ICommand StartDualSenseCommand { get; }
    public ICommand StartPro1Command { get; }
    public ICommand StartDualSenseProfessionalImuCommand { get; }
    public ICommand StartPro2Command { get; }
    public ICommand StartXboxCommand { get; }
    public ICommand StartXboxProfessionalImuCommand { get; }
    public ICommand CalibrateGyroBiasCommand { get; }
    public ICommand ResetGyroBiasCommand { get; }
    public ICommand ResetIntegralCommand { get; }
    public ICommand StartPitch90TestCommand { get; }
    public ICommand StartYaw90TestCommand { get; }
    public ICommand StartRoll90TestCommand { get; }
    public ICommand Stop90TestCommand { get; }
    public ICommand PulseGyroXCommand { get; }
    public ICommand PulseGyroYCommand { get; }
    public ICommand PulseGyroZCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ClearLogCommand { get; }

    private void ExecuteProfessionalImuAction(
        string operation,
        Func<OutputPadSession, string> action)
    {
        if (session is not { HasProfessionalImuRuntime: true } activeSession)
        {
            string message = "[PRO_IMU] " + operation +
                             " ignored: start PS5 / Professional IMU Test or Xbox / Professional IMU Test first.";
            AppendLog(message);
            Status = "请先进入 Professional IMU Test 模式。";
            return;
        }

        string result = action(activeSession);
        AppendLog("[PRO_IMU_UI] " + operation + ": " + result);
        Status = result;
    }

    private void UpdateProfessionalImuSnapshot(ProfessionalImuUiSnapshot snapshot)
    {
        ProfessionalBiasStatusText = snapshot.BiasStatusText;
        ProfessionalRawGyroText = snapshot.RawGyroText;
        ProfessionalCorrectedGyroText = snapshot.CorrectedGyroText;
        ProfessionalOutputGyroText = snapshot.OutputGyroText;
        ProfessionalIntegratedAngleText = snapshot.IntegratedAngleText;
        ProfessionalNinetyDegreeTestText = snapshot.NinetyDegreeTestText;
    }

    private void ApplyProfessionalGyroInversionSettings()
    {
        userSettings.ProfessionalInvertGyroPitch = professionalInvertGyroPitch;
        userSettings.ProfessionalInvertGyroYaw = professionalInvertGyroYaw;
        userSettings.ProfessionalInvertGyroRoll = professionalInvertGyroRoll;
        SaveUserSettings("[PRO_IMU]");
        string summary = ProfessionalGyroInversionSummary;
        AppendLog("[PRO_IMU] gyro_output_inversion " + summary +
                  (session is { HasProfessionalImuRuntime: true }
                      ? " apply=immediate"
                      : " apply=next_professional_session"));
        if (session is { HasProfessionalImuRuntime: true } activeSession)
        {
            AppendLog("[PRO_IMU] " + activeSession.SetProfessionalGyroInversion(
                professionalInvertGyroPitch,
                professionalInvertGyroYaw,
                professionalInvertGyroRoll));
        }
    }

    private void ApplyPro2SlotRuntimeOptions()
    {
        // 新连接层（当前主流程）和旧 BLE 源都推同一份强度，保证两条链路一致。
        LinkService?.InputSession?.SetRumbleGain(rumbleMultiplier);
        foreach (Pro2ControllerSlot slot in pro2Slots)
        {
            slot.InputSource.SetRumbleGain(rumbleMultiplier);
            slot.InputSource.SetRuntimeBatteryRefreshEnabled(runtimeBatteryRefreshEnabled);
            slot.InputSource.SetStickProcessingMode(SelectedStickProcessingOption.Mode);
            ApplySavedStickCalibration(
                slot,
                LastKnownPro2AddressForSlot(slot),
                logChange: false);
            slot.RefreshFromSource();
        }
    }

    private void ApplyProfessionalStaticGyroRaw()
    {
        if (!short.TryParse(professionalStaticGyroXRaw, out short x) ||
            !short.TryParse(professionalStaticGyroYRaw, out short y) ||
            !short.TryParse(professionalStaticGyroZRaw, out short z))
        {
            ProfessionalHidAuditStatusText = "HID Audit: static raw 输入无效，范围应为 -32768..32767。";
            return;
        }

        string summary = professionalHidAuditController.SetStaticRaw(x, y, z);
        ProfessionalHidAuditStatusText = "HID Audit: " + summary;
        AppendLog("[PRO_IMU_AUDIT] static raw changed " + summary);
    }

    private void StartProfessionalHidPulse(string axis)
    {
        string summary = professionalHidAuditController.StartPulse(
            axis,
            8192,
            TimeSpan.FromSeconds(2),
            Stopwatch.GetTimestamp());
        selectedProfessionalHidAuditMode = ProfessionalHidAuditMode.ForceFinalGyroSyntheticPulse.ToString();
        OnPropertyChanged(nameof(SelectedProfessionalHidAuditMode));
        ProfessionalHidAuditStatusText = "HID Audit: synthetic pulse " + axis + " +8192 raw for 2s · " + summary;
        AppendLog("[PRO_IMU_AUDIT] synthetic pulse start axis=" + axis +
                  " raw=8192 duration_seconds=2 " + summary);
    }

    private async Task PingAsync(CancellationToken cancellationToken)
    {
        try
        {
            string response = await PingCoreAsync(cancellationToken);
            Status = "VIIPER server 已响应。";
            AppendLog("[PING] " + response);
        }
        catch (Exception ex)
        {
            Status = "VIIPER server 未就绪：" + ViewModelPolicies.FirstLine(ex.Message);
            AppendLog("ERROR ping: " + ex);
        }
    }

    private async Task<string> PingCoreAsync(CancellationToken cancellationToken)
    {
        return await PingCoreAsync(ParsePort(), cancellationToken);
    }

    private async Task<string> PingCoreAsync(int apiPort, CancellationToken cancellationToken)
    {
        var client = new OutputBridgeClient(Host, apiPort);
        return await client.PingAsync(cancellationToken);
    }

    private async Task StartLocalViiperServerAsync(CancellationToken cancellationToken)
    {
        int requestedApiPort = ParsePort();
        if (!ViewModelPolicies.IsLoopbackHost(Host))
        {
            throw new InvalidOperationException("启动本地 VIIPER 时 Host 必须是 localhost、127.0.0.1 或 ::1。");
        }

        DisposeExitedViiperProcess();
        if (viiperProcess is { HasExited: false })
        {
            if (localViiperApiPort.HasValue && localViiperApiPort.Value != requestedApiPort)
            {
                Port = localViiperApiPort.Value.ToString();
                requestedApiPort = localViiperApiPort.Value;
                AppendLog("[VIIPER_SERVER] restored active local API port " + requestedApiPort + ".");
            }
            Status = "本地 VIIPER server 已在运行，pid=" + viiperProcess.Id;
            AppendLog("[VIIPER_SERVER] already_running pid=" + viiperProcess.Id);
            string response = await PingCoreAsync(cancellationToken);
            AppendLog("[PING] " + response);
            return;
        }

        string? exe = FindLocalViiperExe();
        if (exe == null)
        {
            Status = "没有找到本地 VIIPER server，且内置 runtime 释放失败。请确认 EXE 完整。";
            AppendLog("ERROR viiper server: local runtime not found and embedded extraction failed.");
            return;
        }

        UsbipRuntime? usbip = FindConfiguredUsbip();
        if (usbip == null)
        {
            UsbipInstaller? installer = UsbipRuntimeLocator.FindBundledInstaller();
            Status = installer == null
                ? "未找到 usbip-win2 的 usbip.exe，也没有找到随包安装器。请确认发布包完整。" + UsbipPortableInstallHint
                : "未找到可用的 usbip-win2。请点击“安装/修复 usbip-win2”，使用 EXE 内置的正式安装器完成安装。" + UsbipPortableInstallHint;
            AppendLog("[USBIP] usbip.exe not found. VIIPER can answer ping without it, but all three modes will fail during auto-attach.");
            if (installer != null)
            {
                AppendLog("[USBIP] bundled installer available: " + installer.InstallerPath);
            }
            else
            {
                AppendLog("[USBIP] bundled installer missing. Keep usbip-win2 next to the EXE under usbip-win2\\" + UsbipRuntimeLocator.BundledVersion + ".");
            }
            RefreshRuntimeReadiness();
            throw new InvalidOperationException(Status);
        }
        UsbipProbeResult usbipProbe = await UsbipRuntimeLocator.ProbeAsync(
            usbip,
            cancellationToken);
        if (!usbipProbe.Ready)
        {
            Status = "已找到 usbip.exe，但 USBIP 内核驱动尚未就绪。若刚安装完成，通常需要重启 Windows；重启后仍失败再点击“安装/修复 USBIP”。";
            AppendLog("[USBIP] driver probe failed: " + usbipProbe.Detail);
            throw new InvalidOperationException(Status);
        }

        string logRoot = AppPaths.LogsDirectory;
        Directory.CreateDirectory(logRoot);

        IReadOnlyList<BridgePortPair> candidates = ViewModelPolicies.BuildBridgePortCandidates(requestedApiPort);
        var failures = new List<string>();
        AppendLog("[VIIPER_PREFLIGHT] requested_api=127.0.0.1:" + requestedApiPort +
                  " candidates=" + string.Join(",", candidates.Select(c => c.ApiPort + "/" + c.UsbPort)));
        foreach (BridgePortPair candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool portsReady = await PrepareViiperPortCandidateAsync(
                candidate,
                failures,
                cancellationToken);
            if (!portsReady)
            {
                continue;
            }

            ViiperStartupFailure? failure = await TryStartLocalViiperServerAsync(
                exe,
                usbip,
                candidate,
                logRoot,
                cancellationToken);
            if (failure == null)
            {
                return;
            }

            failures.Add(failure.Summary);
            if (failure.PortConflict)
            {
                AppendLog("[VIIPER_PREFLIGHT] retrying alternate port pair after port conflict. failed_api=" +
                          candidate.ApiPort + " failed_usb=" + candidate.UsbPort);
                continue;
            }

            throw new InvalidOperationException(failure.UserMessage);
        }

        throw new InvalidOperationException(
            "VIIPER server 启动失败：所有候选端口都不可用或启动失败。诊断：" +
            string.Join(" | ", failures.Take(8)));
    }

    private async Task<bool> PrepareViiperPortCandidateAsync(
        BridgePortPair candidate,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        bool apiFree = IsLoopbackTcpPortAvailable(candidate.ApiPort, out string apiDetail);
        bool usbFree = IsLoopbackTcpPortAvailable(candidate.UsbPort, out string usbDetail);
        AppendLog("[VIIPER_PREFLIGHT] candidate source=" + candidate.Source +
                  " api=127.0.0.1:" + candidate.ApiPort + " " + apiDetail +
                  " usb=127.0.0.1:" + candidate.UsbPort + " " + usbDetail);

        if (!apiFree)
        {
            string? ping = await TryPingViiperOnPortAsync(
                candidate.ApiPort,
                TimeSpan.FromMilliseconds(850),
                cancellationToken);
            if (ping != null && ViewModelPolicies.LooksLikeViiperPing(ping))
            {
                AppendLog("[VIIPER_PREFLIGHT] api port " + candidate.ApiPort +
                          " is occupied by a VIIPER server; cleaning stale process before retry. " + ping);
                AppendLog(StartupProcessGuard.CleanupConflictingProcesses());
                await Task.Delay(900, cancellationToken);
                apiFree = IsLoopbackTcpPortAvailable(candidate.ApiPort, out apiDetail);
                usbFree = IsLoopbackTcpPortAvailable(candidate.UsbPort, out usbDetail);
                AppendLog("[VIIPER_PREFLIGHT] after cleanup api=127.0.0.1:" + candidate.ApiPort + " " + apiDetail +
                          " usb=127.0.0.1:" + candidate.UsbPort + " " + usbDetail);
            }
        }

        if (!apiFree)
        {
            string summary = "api " + candidate.ApiPort + " unavailable: " + apiDetail;
            failures.Add(summary);
            AppendLog("[VIIPER_PREFLIGHT] " + summary + " auto_fallback=1");
            return false;
        }

        if (!usbFree)
        {
            string summary = "usb " + candidate.UsbPort + " unavailable: " + usbDetail;
            failures.Add(summary);
            AppendLog("[VIIPER_PREFLIGHT] " + summary + " auto_fallback=1");
            return false;
        }

        return true;
    }

    private async Task<ViiperStartupFailure?> TryStartLocalViiperServerAsync(
        string exe,
        UsbipRuntime usbip,
        BridgePortPair ports,
        string logRoot,
        CancellationToken cancellationToken)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string logPath = Path.Combine(
            logRoot,
            "viiper_server_" + stamp + "_api" + ports.ApiPort + "_usb" + ports.UsbPort + ".log");
        string args = "server --api.addr=127.0.0.1:" + ports.ApiPort +
                      " --usb.addr=127.0.0.1:" + ports.UsbPort +
                      " --api.device-handler-connect-timeout=60s" +
                      " --usb.write-batch-flush-interval=0ms" +
                      " --update-notify=none" +
                      " --log.level=info" +
                      " --log.file=\"" + logPath + "\"";
        ProcessStartInfo startInfo = new()
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["PATH"] = UsbipRuntimeLocator.BuildPathWithUsbipDirectory(
            startInfo.Environment.TryGetValue("PATH", out string? path)
                ? path ?? ""
                : Environment.GetEnvironmentVariable("PATH") ?? "",
            usbip);
        startInfo.WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;

        ProcessOutputTail processOutput = new(80);
        Process? started = Process.Start(startInfo);
        if (started == null)
        {
            return new ViiperStartupFailure(
                false,
                "process_start returned null",
                "无法启动 VIIPER server：Process.Start 返回空进程。");
        }

        processOutput.Attach(started);
        viiperProcess = started;
        localViiperApiPort = ports.ApiPort;
        Port = ports.ApiPort.ToString();

        Status = "正在启动本地 VIIPER server，pid=" + started.Id;
        AppendLog("[USBIP] using " + usbip.ExePath);
        AppendLog("[VIIPER_SERVER] launch pid=" + started.Id +
                  " api=127.0.0.1:" + ports.ApiPort +
                  " usb=127.0.0.1:" + ports.UsbPort +
                  " exe=" + exe +
                  " args=" + args +
                  " log=" + logPath);

        ViiperStartupFailure? failure = await WaitForViiperStartupAsync(
            started,
            ports,
            logPath,
            processOutput,
            cancellationToken);
        if (failure != null)
        {
            await CleanupFailedViiperProcessAsync(started);
            if (ReferenceEquals(viiperProcess, started))
            {
                viiperProcess = null;
                localViiperApiPort = null;
            }
            return failure;
        }

        return null;
    }

    private async Task<ViiperStartupFailure?> WaitForViiperStartupAsync(
        Process process,
        BridgePortPair ports,
        string logPath,
        ProcessOutputTail processOutput,
        CancellationToken cancellationToken)
    {
        string lastPingError = "";
        for (int attempt = 1; attempt <= 20; attempt++)
        {
            await Task.Delay(250, cancellationToken);
            if (process.HasExited)
            {
                return BuildViiperStartupFailure(
                    process,
                    ports,
                    logPath,
                    processOutput,
                    "early_exit",
                    lastPingError);
            }

            using var pingTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pingTimeout.CancelAfter(TimeSpan.FromMilliseconds(650));
            try
            {
                string ping = await PingCoreAsync(ports.ApiPort, pingTimeout.Token);
                Status = "本地 VIIPER server 已就绪，pid=" + process.Id + "。";
                AppendLog("[PING] " + ping);
                if (ports.ApiPort != 3242)
                {
                    AppendLog("[VIIPER_SERVER] using alternate ports api=127.0.0.1:" +
                              ports.ApiPort + " usb=127.0.0.1:" + ports.UsbPort +
                              " reason=preferred_port_unavailable_or_failed");
                }
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastPingError = "ping timeout attempt=" + attempt;
            }
            catch (Exception ex)
            {
                lastPingError = ViewModelPolicies.FirstLine(ex.Message);
            }
        }

        return BuildViiperStartupFailure(
            process,
            ports,
            logPath,
            processOutput,
            "api_ping_timeout",
            lastPingError);
    }

    private ViiperStartupFailure BuildViiperStartupFailure(
        Process process,
        BridgePortPair ports,
        string logPath,
        ProcessOutputTail processOutput,
        string stage,
        string lastPingError)
    {
        int? exitCode = null;
        if (process.HasExited)
        {
            exitCode = process.ExitCode;
        }

        string logTail = ReadTailText(logPath, 40);
        string processTail = processOutput.Snapshot();
        string combined = logTail + "\n" + processTail + "\n" + lastPingError;
        bool portConflict = ViewModelPolicies.LooksLikePortConflict(combined);
        string category = portConflict
            ? "port_conflict"
            : ViewModelPolicies.LooksLikeUsbipDriverIssue(combined)
                ? "usbip_driver_or_permission"
                : stage == "api_ping_timeout"
                    ? "api_unreachable"
                    : "early_exit_unknown";
        string exitText = exitCode.HasValue ? exitCode.Value.ToString() : "running";
        AppendLog("[VIIPER_DIAG] category=" + category +
                  " stage=" + stage +
                  " exit=" + exitText +
                  " api=127.0.0.1:" + ports.ApiPort +
                  " usb=127.0.0.1:" + ports.UsbPort +
                  " last_ping=\"" + ViewModelPolicies.SanitizeForLog(lastPingError) + "\"" +
                  " log=" + logPath);
        if (!string.IsNullOrWhiteSpace(logTail))
        {
            AppendLog("[VIIPER_LOG_TAIL]\n" + logTail);
        }
        if (!string.IsNullOrWhiteSpace(processTail))
        {
            AppendLog("[VIIPER_PROCESS_TAIL]\n" + processTail);
        }

        string hint = category switch
        {
            "port_conflict" =>
                "端口被占用、保留或权限拒绝。程序会自动尝试下一组 API/USBIP 端口；若全部失败，请关闭旧 VIIPER/USBIP/占用端口程序或重启 Windows。",
            "usbip_driver_or_permission" =>
                "更像 USBIP 驱动、权限或安全软件问题。请安装/修复 usbip-win2；若刚安装完成，请重启 Windows 后再试。",
            "api_unreachable" =>
                "VIIPER 进程未退出，但 API 端口一直无法响应。程序已清理该进程，避免留下半启动状态。",
            _ =>
                "VIIPER 在写出明确错误前退出。EXE 已回灌 viiper_server 日志尾部和 stdout/stderr 尾部，请按上方 category 与 tail 定位。"
        };
        string summary = "category=" + category +
                         " exit=" + exitText +
                         " api=" + ports.ApiPort +
                         " usb=" + ports.UsbPort +
                         (string.IsNullOrWhiteSpace(lastPingError) ? "" : " last_ping=" + lastPingError);
        string userMessage =
            "VIIPER server 启动失败：" + summary + "。日志：" + logPath + "。" + hint;
        return new ViiperStartupFailure(portConflict, summary, userMessage);
    }

    private async Task CleanupFailedViiperProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            AppendLog("[VIIPER_DIAG] failed startup cleanup warning: " + ex.Message);
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task InstallUsbipAsync(CancellationToken cancellationToken)
    {
        UsbipInstaller? installer = UsbipRuntimeLocator.FindBundledInstaller();
        if (installer == null)
        {
            Status = "没有找到随包 usbip-win2 安装器。请确认发布包完整。";
            AppendLog("[USBIP] bundled installer not found: " + UsbipRuntimeLocator.InstallerFileName);
            return;
        }

        UsbipRuntime? existing = FindConfiguredUsbip();
        if (existing != null)
        {
            AppendLog("[USBIP] existing usbip.exe found: " + existing.ExePath);
            AppendLog("[USBIP] launching bundled installer anyway for repair/update.");
        }

        Status = "正在启动 usbip-win2 安装器，请在 UAC/安装向导中确认。安装期间 USB 设备可能会短暂重启。";
        AppendLog("[USBIP] installer=" + installer.InstallerPath);
        if (!string.IsNullOrWhiteSpace(installer.LicensePath))
        {
            AppendLog("[USBIP] license=" + installer.LicensePath);
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = installer.InstallerPath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(installer.InstallerPath) ?? AppContext.BaseDirectory
            });

            if (process == null)
            {
                Status = "未能启动 usbip-win2 安装器。";
                AppendLog("[USBIP] installer process returned null.");
                return;
            }

            await process.WaitForExitAsync(cancellationToken);
            int installerExitCode = process.ExitCode;
            AppendLog("[USBIP] installer exited code=" + installerExitCode);
            if (installerExitCode is 1641 or 3010)
            {
                Status = "USBIP 驱动安装成功，但 Windows 要求重启。请重启后再启动角色，不要反复重装。";
                AppendLog("[USBIP] reboot_required=1 installer_exit=" + installerExitCode);
            }
            else if (installerExitCode != 0)
            {
                Status = "USBIP 安装器返回错误 exit=" + installerExitCode + "。请查看安装向导提示，或再次点击“安装/修复 USBIP”。";
                AppendLog("[USBIP] installer_failed exit=" + installerExitCode);
            }
            await Task.Delay(1000, cancellationToken);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Status = "已取消 usbip-win2 安装。";
            AppendLog("[USBIP] installer cancelled by user.");
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Status = "启动 usbip-win2 安装器失败：" + ViewModelPolicies.FirstLine(ex.Message);
            AppendLog("ERROR usbip installer: " + ex);
            return;
        }

        UsbipRuntime? runtime = FindConfiguredUsbip();
        if (runtime != null)
        {
            UsbipProbeResult probe = await UsbipRuntimeLocator.ProbeAsync(
                runtime,
                cancellationToken);
            if (probe.Ready)
            {
                Status = "usbip-win2 已就绪：" + runtime.ExePath;
                AppendLog("[USBIP] ready " + runtime.ExePath + " / " + probe.Detail);
            }
            else
            {
                Status = "安装器已结束，但 USBIP 内核驱动检测未通过。大概率是安装后尚未重启；请重启 Windows 后再打开本 EXE，避免反复安装。";
                AppendLog("[USBIP] driver probe failed after installer: " + probe.Detail);
                AppendLog("[USBIP] reboot_required_suspected=1 reason=probe_failed_after_installer");
            }
        }
        else
        {
            Status = "usbip-win2 安装器已结束，但还没找到 usbip.exe。若安装器提示重启，请重启后再打开本 EXE。" + UsbipPortableInstallHint;
            AppendLog("[USBIP] usbip.exe still not found after installer exit; reboot may be required.");
        }
        await RefreshRuntimeReadinessAsync(cancellationToken);
    }

    private async Task ScanPro2InputAsync(CancellationToken cancellationToken)
    {
        await bleOperationGate.WaitAsync(cancellationToken);
        try
        {
            Status = "正在扫描 Pro2 BLE，窗口仍可正常响应，请等待约 8 秒。";
            InputStatus = "正在扫描未配对的 Pro2 BLE 广播...";
            var progress = new DirectProgress<string>(AppendLog);
            var candidates = await inputSource.ScanAsync(progress, TimeSpan.FromSeconds(8), cancellationToken);
            if (candidates.Count == 0)
            {
                string diagnostic = inputSource.LastScanDiagnostic;
                InputStatus = string.IsNullOrWhiteSpace(diagnostic)
                    ? "没有扫描到真实 Pro2 BLE。唤醒手柄并确保没有被 ESP32、Switch、手机或旧进程占用。"
                    : "没有扫描到真实 Pro2 BLE：" + diagnostic;
                Status = InputStatus;
                AppendLog("[PRO2_BLE] scan none");
                return;
            }

            InputStatus = "发现 " + candidates.Count + " 个 Pro2 BLE 候选。";
            foreach (string candidate in candidates)
            {
                AppendLog("[PRO2_BLE] candidate " + candidate);
            }
        }
        finally
        {
            bleOperationGate.Release();
        }
    }

    /// <summary>
    /// 启动时只做「清场」，不做任何自动连接。
    ///
    /// 旧版会在启动瞬间切换模式、拉起 VIIPER、创建虚拟手柄并开始自动重连；
    /// 于是没插手柄时系统里也会多出一个空手柄设备，插线或拔线时旧链路反复
    /// 断联重连，通知一条接一条地弹。
    ///
    /// v1.0 的顺序改成：先连上真实手柄，再由用户决定要不要创建输出。
    /// </summary>
    public Task RunStartupAutomationAsync()
    {
        if (shuttingDown || startupAutomationRan)
        {
            return Task.CompletedTask;
        }

        startupAutomationRan = true;

        if (AutoReconnectOnStartupEnabled)
        {
            // 存量设置里可能还留着旧的「启动后自动进入上次模式」，直接归零，
            // 否则每次启动都会走进旧流程。setter 会顺手写回设置文件。
            AutoReconnectOnStartupEnabled = false;
            AppendLog("[STARTUP] legacy_auto_enter_cleared=1");
        }

        AppendLog("[STARTUP] automation=none reason=connect_first");
        return Task.CompletedTask;
    }

    private async Task TryConnectPro2InputOnceAsync(CancellationToken cancellationToken)
    {
        long attemptStarted = Stopwatch.GetTimestamp();
        Status = "正在扫描并连接 Pro2 BLE；将依次验证 GATT、初始化命令和 live 输入。";
        InputStatus = "正在连接 Pro2 BLE...";
        var progress = new DirectProgress<string>(AppendLog);
        await bleOperationGate.WaitAsync(cancellationToken);
        try
        {
            Pro2ControllerSlot primarySlot = pro2Slots[0];
            IReadOnlySet<string> preferred = PrimaryPreferredAddresses(primarySlot);
            bool onlyPreferred = !string.IsNullOrWhiteSpace(manualReconnectAddress) || startupAutomationActive && preferred.Count > 0;
            if (manualReconnectTarget != null)
                await inputSource.StartManualAsync(manualReconnectTarget, ConnectedPro2AddressesExcept(primarySlot),
                    ResolveStickCalibration, progress, cancellationToken);
            else await inputSource.StartAsync(
                progress,
                ConnectedPro2AddressesExcept(primarySlot),
                preferred,
                onlyPreferred,
                ResolveStickCalibration,
                cancellationToken);
        }
        finally
        {
            bleOperationGate.Release();
            AppendLog("[PRO2_CONNECT_TIMING] phase=ble_attempt slot=1 elapsed_ms=" +
                Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                " transport_connected=" + inputSource.IsTransportConnected + " source_running=" + inputSource.IsRunning +
                " high_rate_ready=" + inputSource.IsHighRateReady + " validation_unchanged=true");
        }
        InputStatus = inputSource.Status;
        if (inputSource.IsRunning)
        {
            pro2Slots[0].RefreshFromSource();
            RememberConnectedAddress(pro2Slots[0]);
            MarkStartupAutomationConnected("primary");
            Status = inputSource.IsPerformanceDegraded
                ? "真实 BLE 已连接，但输入速率未达标；保留物理连接，未通过验收时不创建虚拟输出。"
                : Running
                    ? "真实 Pro2 BLE 已接入，当前虚拟模式将自动切换为 live 输入和 rumble 写回。"
                    : "真实 Pro2 BLE 已连接，可以启动任一虚拟模式。";
        }
        else
        {
            Status = "真实 Pro2 BLE 未连接。请唤醒手柄，并确认没有被其他设备占用。";
        }
        RaiseConnectionStateChanged();
    }

    private async Task DisconnectPro2InputAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await StopAutoReconnectAsync();
        await bleOperationGate.WaitAsync(cancellationToken);
        try
        {
            await inputSource.StopAsync();
        }
        finally
        {
            bleOperationGate.Release();
        }
        InputStatus = inputSource.Status;
        RaiseConnectionStateChanged();
        if (Running)
        {
            Status = "已停止自动重连并断开真实 Pro2；虚拟设备继续运行 neutral 输入。再次点击“进入游戏”即可恢复持续连接。";
        }
    }

    private async Task CalibrateSourceGyroAsync(CancellationToken cancellationToken)
    {
        // 用户主动点「重新校准」：丢掉上次保存的档案，本次会话重新学习。
        Pro2GyroBiasStore.Clear();
        session?.ClearGyroBias();
        AppendLog("[PRO2_GYRO_CAL] saved_bias_cleared key=" + Pro2GyroBiasStore.CurrentKey);

        Pro2BleInputSource[] liveSources = pro2Slots
            .Where(slot => slot.InputSource.IsRunning)
            .Select(slot => slot.InputSource)
            .Distinct()
            .ToArray();
        if (liveSources.Length == 0)
        {
            Status = "没有已连接的真实 Pro2，无法校准陀螺仪。";
            AppendLog("[PRO2_GYRO_CAL] rejected=no_live_source");
            return;
        }

        foreach (Pro2BleInputSource source in liveSources)
        {
            AppendLog("[PRO2_GYRO_CAL] " + source.StartManualGyroCalibration());
        }
        Status = "正在校准真实 Pro2 陀螺仪，请将手柄静置三秒。按键和摇杆仍保持连接。";
        InputStatus = "陀螺仪零偏校准中，请勿移动手柄...";
        await Task.Delay(TimeSpan.FromSeconds(3.5), cancellationToken);

        string[] summaries = liveSources
            .Select((source, index) => "slot=" + (index + 1) + " " + source.GyroCalibrationSummary)
            .ToArray();
        foreach (string summary in summaries)
        {
            AppendLog("[PRO2_GYRO_CAL] " + summary);
        }
        bool complete = summaries.All(summary => summary.Contains("status=calibrated", StringComparison.Ordinal) &&
            (!QualityFeatureFlags.GyroEnabled || summary.Contains("result=manual_committed_3s", StringComparison.Ordinal)));
        Status = complete
            ? "真实 Pro2 陀螺仪零偏校准完成。未使用死区或滤波。"
            : "陀螺仪校准尚未提交，检测到移动；请保持静置后重试。";
        InputStatus = inputSource.Status + " · " + inputSource.GyroCalibrationSummary;
    }

    public Task ConnectForLaunchAsync() =>
        RunExclusiveAsync("启动连接 Pro2", EnterGameAsync);

    public async Task DiagnoseForLaunchAsync()
    {
        // The first connection attempt can return before automatic reconnect succeeds.
        // Do not hold the command gate while waiting for that reconnect to finish.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while ((!inputSource.IsRunning || !inputSource.IsTransportConnected || Busy) && DateTime.UtcNow < deadline)
            await Task.Delay(250);
        if (inputSource.IsRunning && inputSource.IsTransportConnected)
            await RunExclusiveAsync("启动后真实 BLE 诊断", RunWin10BleDiagnosticAsync);
        else
            AppendLog("[BLE_TURBO_REPORT] launch_diagnostic_skipped reason=no_ready_input_within_90s");
    }

    private Task EnterGameAsync(CancellationToken cancellationToken) =>
        EnterGameAsync(cancellationToken, allowAutomaticConnect: true);

    private async Task EnterGameAsync(CancellationToken cancellationToken, bool allowAutomaticConnect)
    {
        long enterStarted = Stopwatch.GetTimestamp();
        if (!allowAutomaticConnect && !inputSource.IsRunning)
        {
            Status = "所选地址的连接已中断，请在手动列表重试；不会自动切换到其他地址。";
            return;
        }

        // 先有真实手柄，才有输出。没有连线时不要创建虚拟手柄：
        // 否则系统的「设备和打印机」里会多一个空设备，而且后台还会反复重连。
        if (!inputSource.IsRunning)
        {
            await TryConnectPro2InputOnceAsync(cancellationToken);
            if (!inputSource.IsRunning)
            {
                enterGamePending = false;
                Status = "没有检测到手柄，所以没有创建虚拟手柄。插上 USB 线，或让手柄进入蓝牙配对后重试。";
                InputStatus = inputSource.Status;
                RaiseConnectionStateChanged();
                return;
            }
        }

        enterGamePending = true;
        if (SoftwareOnlyMode.IsEnabled && !inputSource.IsRunning)
        {
            AppendLog("[PURE_SOFTWARE_133] connect_order=physical_ble_before_virtual_device " +
                      "reason=isolate_win10_connection_interval_from_viiper_scheduler");
            Status = "先连接并实测真实 Pro2 BLE；达到 60Hz 兼容门槛即可游玩，仍优先优化到 125Hz。";
            await TryConnectPro2InputOnceAsync(cancellationToken);
            if (!inputSource.IsRunning || !inputSource.IsGameplayReady)
            {
                Status = inputSource.IsRunning
                    ? "蓝牙已连接，但真实 FD2 尚未达到 60Hz 兼容门槛；保持连接等待，不反复断开手柄。"
                    : "真实 Pro2 尚未连接，暂不创建虚拟输出；自动重连会继续寻找。";
                AppendLog("[PURE_SOFTWARE_133] preflight_result=rejected virtual_device_started=false");
                StartAutoReconnect();
                return;
            }

            AppendLog("[PURE_SOFTWARE_133] preflight_result=" + (inputSource.IsHighRateReady ? "high_rate" : "compatible_rate") +
                " gameplay_allowed=true virtual_device_start=next cadence=fresh_source_only");
        }

        if (SoftwareOnlyMode.IsEnabled && !inputSource.IsGameplayReady)
        {
            Status = "手柄已连接，但真实 FD2 尚未达到 60Hz 兼容门槛；连接保持，虚拟输出未启动。";
            StartAutoReconnect();
            return;
        }

        if (!Running || activeMode != selectedMode)
        {
            await SwitchModeAsync(SelectedProfile, cancellationToken);
        }

        if (!Running)
        {
            Status = "当前虚拟模式启动失败，尚未进入自动连接。请根据上方提示修复 USBIP/VIIPER 后重试。";
            return;
        }
        enterGamePending = false;

        if (activeMode.HasValue && ViewModelPolicies.IsMultiSlotMode(activeMode.Value))
        {
            await StartEnabledSlotsAutoReconnectAsync(cancellationToken);
        }
        else
        {
            StartAutoReconnect();
        }
        AppendLog("[PRO2_CONNECT_TIMING] phase=game_ready elapsed_ms=" +
            Stopwatch.GetElapsedTime(enterStarted).TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
            " mode=" + ViewModelPolicies.ModeKey(selectedMode) + " input_running=" + inputSource.IsRunning +
            " output_running=" + Running + " high_rate_ready=" + inputSource.IsHighRateReady);
    }

    private async Task SwitchModeAsync(
        ViiperDeviceProfile profile,
        CancellationToken cancellationToken)
    {
        SelectMode(profile.Mode);
        if (Running && activeMode.HasValue && ViewModelPolicies.IsMultiSlotMode(activeMode.Value) && ViewModelPolicies.IsMultiSlotMode(profile.Mode))
        {
            await ApplySlotModeAsync(pro2Slots[0], cancellationToken);
            return;
        }
        if (!ConnectionPhasePresentation.CanStartVirtual(
                SoftwareOnlyMode.IsEnabled, inputSource.IsRunning, inputSource.IsHighRateReady, inputSource.IsGameplayReady))
        {
            Status = inputSource.IsRunning
                ? "BLE 已连接，但真实输入尚未达到 60Hz 兼容门槛；请查看首页低速诊断。"
                : "请先点击连接 Pro2；真实输入验收通过后才创建所选虚拟手柄。";
            return;
        }
        if (Running && activeMode == profile.Mode)
        {
            Status = profile.Label + " 已经是当前角色。点击“连接 PRO2 · 进入游戏”接入真实手柄。";
            return;
        }

        bool resumePrimaryAutoReconnect = AutoReconnectEnabled && !ViewModelPolicies.IsMultiSlotMode(profile.Mode);
        bool resumeSlotAutoReconnect =
            ViewModelPolicies.IsMultiSlotMode(profile.Mode) &&
            (AutoReconnectEnabled || pro2Slots.Any(slot => slot.AutoReconnectEnabled));
        if (AutoReconnectEnabled || pro2Slots.Any(slot => slot.AutoReconnectEnabled))
        {
            AppendLog("[MODE_SWITCH] pausing Pro2 auto reconnect before virtual USB rebuild.");
            await StopAutoReconnectAsync();
        }

        await EnsureViiperReadyAsync(cancellationToken);
        if (Running)
        {
            AppendLog("[MODE_SWITCH] from=" + ActiveProfile.Label + " to=" + profile.Label);
            await StopSessionAsync(updateStatus: false);
            await Task.Delay(650, cancellationToken);
        }

        await CleanupVirtualDeviceResidueAsync(
            "before_start_" + profile.Mode,
            cancellationToken,
            includePnpDump: false);

        await StartAsync(profile, cancellationToken);
        if (resumeSlotAutoReconnect && Running && activeMode == profile.Mode)
        {
            AppendLog("[MODE_SWITCH] resuming multi-slot auto reconnect after virtual USB rebuild.");
            await StartEnabledSlotsAutoReconnectAsync(cancellationToken);
        }
        else if (resumePrimaryAutoReconnect && Running && activeMode == profile.Mode)
        {
            AppendLog("[MODE_SWITCH] resuming Pro2 auto reconnect after virtual USB rebuild.");
            StartAutoReconnect();
        }
    }

    private async Task EnsureViiperReadyAsync(CancellationToken cancellationToken)
    {
        if (ViewModelPolicies.IsLoopbackHost(Host))
        {
            UsbipRuntime? runtime = FindConfiguredUsbip();
            UsbipProbeResult? probe = runtime == null
                ? null
                : await UsbipRuntimeLocator.ProbeAsync(runtime, cancellationToken);
            if (runtime == null)
            {
                Status = "首次运行需要安装 USBIP 内核驱动，正在打开内置安装向导。完成后将继续启动所选角色。";
                AppendLog("[FIRST_RUN] usbip preflight failed: usbip.exe not found" +
                          "; launching embedded installer.");
                await InstallUsbipAsync(cancellationToken);
                runtime = FindConfiguredUsbip();
                if (runtime == null)
                {
                    throw new InvalidOperationException(
                        "USBIP 驱动尚未就绪。请完成 EXE 内置的正式安装向导；若安装器要求重启，请重启 Windows 后再次选择角色。" +
                        UsbipPortableInstallHint);
                }

                probe = await UsbipRuntimeLocator.ProbeAsync(runtime, cancellationToken);
                if (!probe.Ready)
                {
                    throw new InvalidOperationException(
                        "USBIP 内核驱动检测未通过：" + probe.Detail +
                        "。若刚完成安装，请重启 Windows 后再次选择角色。");
                }
            }
            else if (probe is not { Ready: true })
            {
                Status = "USBIP 程序已安装，但内核驱动未就绪。若刚安装/修复过，请重启 Windows；重启后仍失败再点“安装/修复 USBIP”。";
                AppendLog("[FIRST_RUN] usbip.exe found but driver probe failed; not relaunching installer automatically. detail=" +
                          (probe?.Detail ?? "unknown"));
                throw new InvalidOperationException(
                    "USBIP 已安装但驱动未就绪：" + (probe?.Detail ?? "unknown") +
                    "。通常是刚安装后尚未重启 Windows；请重启后再打开本 EXE。");
            }
        }

        try
        {
            await PingCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch when (ViewModelPolicies.IsLoopbackHost(Host))
        {
            await StartLocalViiperServerAsync(cancellationToken);
            await PingCoreAsync(cancellationToken);
        }
    }

    private void StartAutoReconnect()
    {
        if (autoReconnectTask is { IsCompleted: false })
        {
            Status = IsInputConnected
                ? "Pro2 已连接，自动重连守护正在运行。"
                : "自动连接已在持续寻找 Pro2，请保持手柄唤醒。";
            return;
        }

        autoReconnectCts?.Dispose();
        autoReconnectCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
        AutoReconnectEnabled = true;
        InputStatus = "自动连接已启动：将持续扫描最匹配的 Pro2，并在断联后自动恢复。";
        Status = "已进入游戏连接流程。无需重复点击；唤醒 Pro2 后程序会持续尝试，断联也会自动重连。";
        AppendLog("[PRO2_AUTO] enabled stale_timeout_ms=" + LostInputTimeoutMilliseconds);
        autoReconnectTask = AutoReconnectLoopAsync(autoReconnectCts.Token);
    }

    private async Task AutoReconnectLoopAsync(CancellationToken cancellationToken)
    {
        int attempt = 0;
        int failedAttemptStreak = 0;
        bool previouslyLive = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await WaitWhileAutoSleepingAsync(pro2Slots[0], cancellationToken);
                if (inputSource.TryConsumeDisconnectSignal(
                        out Pro2BleDisconnectSignal lowLevelDisconnect))
                {
                    previouslyLive = false;
                    NotifyPro2Offline(
                        slotIndex: 1,
                        lowLevelDisconnect,
                        autoReconnectContinues: true);
                    InputStatus = "Windows 已确认 Pro2 离线，正在清理连接并自动重连...";
                    await bleOperationGate.WaitAsync(cancellationToken);
                    try
                    {
                        await inputSource.StopAsync();
                    }
                    finally
                    {
                        bleOperationGate.Release();
                    }
                    RaiseConnectionStateChanged();
                }

                bool live = inputSource.IsRunning &&
                            inputSource.TryGetLatest(out _, out TimeSpan age) &&
                            age <= TimeSpan.FromMilliseconds(LostInputTimeoutMilliseconds);
                if (live)
                {
                    inputSource.RecheckLiveRate();
                    if (enterGamePending && inputSource.IsGameplayReady)
                        QueuePendingGameResume();
                    failedAttemptStreak = 0;
                    pro2Slots[0].RefreshFromSource();
                    RememberConnectedAddress(pro2Slots[0]);
                    MarkStartupAutomationConnected("primary_loop");
                    if (!previouslyLive)
                    {
                        previouslyLive = true;
                        OnAutoSleepLiveSessionStarted(pro2Slots[0], inputSource);
                        InputStatus = inputSource.Status;
                        Status = inputSource.IsHighRateReady
                            ? "真实 Pro2 BLE 已连接，输入验收通过；正在准备所选游戏模式。"
                            : inputSource.IsGameplayReady ? "真实输入已达到兼容速率，可以游玩；首页显示蓝牙优化建议。"
                            : "BLE 已连接，输入尚未达到 60Hz 兼容门槛；保留连接等待，不反复断开。";
                        AppendLog("[PRO2_AUTO] live; reconnect guard active.");
                        RaiseConnectionStateChanged();
                    }

                    if (await TryEnterAutoSleepAsync(pro2Slots[0], inputSource, cancellationToken))
                    {
                        previouslyLive = false;
                        continue;
                    }

                    ReportReconnectRecovery(1);
                    await WaitForReconnectCheckAsync(1, cancellationToken);
                    continue;
                }

                if (TryPauseStartupAutomationAfterTimeout("primary_loop", currentSlot: null))
                {
                    break;
                }

                if (inputSource.IsRunning)
                {
                    previouslyLive = false;
                    TimeSpan staleAge = inputSource.TryGetLatest(out _, out TimeSpan measuredAge)
                        ? measuredAge
                        : TimeSpan.MaxValue;
                    NotifyPro2Offline(
                        slotIndex: 1,
                        inputSource.CreateInputTimeoutDisconnectSignal(staleAge),
                        autoReconnectContinues: true);
                    AppendLog("[PRO2_AUTO] input stale age_ms=" +
                              (staleAge == TimeSpan.MaxValue ? "unknown" : staleAge.TotalMilliseconds.ToString("F0")) +
                              "; recycling BLE session.");
                    InputStatus = "检测到 Pro2 输入中断，正在清理旧连接并自动重连...";
                    await bleOperationGate.WaitAsync(cancellationToken);
                    try
                    {
                        await inputSource.StopAsync();
                    }
                    finally
                    {
                        bleOperationGate.Release();
                    }
                    RaiseConnectionStateChanged();
                }

                attempt++;
                previouslyLive = false;
                InputStatus = "自动连接第 " + attempt + " 次：正在扫描最匹配的 Pro2，请保持手柄唤醒...";
                Status = "自动重连持续运行中。没有找到手柄时会等待片刻后继续，无需手动重试。";
                AppendLog("[PRO2_AUTO] attempt=" + attempt + " begin.");
                try
                {
                    await TryConnectPro2InputOnceAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedAttemptStreak++;
                    TimeSpan retryDelay = ViewModelPolicies.Pro2ReconnectDelayForAttempt(failedAttemptStreak);
                    AppendLog("[PRO2_AUTO] attempt=" + attempt + " failed: " + ex);
                    InputStatus = "本轮连接异常：" + ViewModelPolicies.FirstLine(ex.Message) + "。" +
                                  retryDelay.TotalSeconds.ToString("F1") +
                                  " 秒后自动继续，连续失败会退避以避免手柄进入冷却。";
                    Status = "Pro2 自动重连仍在运行；失败退避中，不会反复猛连手柄。";
                    AppendLog("[PRO2_AUTO] attempt=" + attempt +
                              " retry_delay_ms=" + retryDelay.TotalMilliseconds.ToString("F0") +
                              " failed_streak=" + failedAttemptStreak +
                              " reason=connect_exception cooldown_guard=1.");
                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }
                RaiseConnectionStateChanged();

                if (!inputSource.IsRunning)
                {
                    failedAttemptStreak++;
                    TimeSpan retryDelay = ViewModelPolicies.Pro2ReconnectDelayForAttempt(failedAttemptStreak);
                    string diagnostic = inputSource.LastScanDiagnostic;
                    InputStatus = string.IsNullOrWhiteSpace(diagnostic)
                        ? "本轮未连接到 Pro2，" + retryDelay.TotalSeconds.ToString("F1") +
                          " 秒后自动继续扫描。可在手动控制区停止自动重连。"
                        : "本轮未连接到 Pro2：" + diagnostic + " " +
                          retryDelay.TotalSeconds.ToString("F1") + " 秒后自动继续扫描。";
                    Status = failedAttemptStreak >= 4
                        ? "自动重连持续运行中。已进入温和退避，避免连续连接触发手柄冷却。"
                        : "自动重连持续运行中。本轮没有找到可用 Pro2，将自动开始下一轮。";
                    AppendLog("[PRO2_AUTO] attempt=" + attempt +
                              " no_live; retry_delay_ms=" + retryDelay.TotalMilliseconds.ToString("F0") +
                              " failed_streak=" + failedAttemptStreak +
                              " cooldown_guard=1.");
                    await Task.Delay(retryDelay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppendLog("[PRO2_AUTO] cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR Pro2 auto reconnect: " + ex);
            InputStatus = "自动重连遇到异常，将在 3 秒后由用户重新进入游戏恢复：" + ViewModelPolicies.FirstLine(ex.Message);
            Status = "Pro2 自动重连异常停止：" + ViewModelPolicies.FirstLine(ex.Message);
        }
        finally
        {
            if (!shuttingDown && !cancellationToken.IsCancellationRequested)
            {
                AutoReconnectEnabled = false;
            }
            RaiseConnectionStateChanged();
        }
    }

    private async Task StartEnabledSlotsAutoReconnectAsync(CancellationToken cancellationToken)
    {
        Pro2ControllerSlot[] enabledSlots = pro2Slots.Where(slot => slot.Enabled).ToArray();
        if (enabledSlots.Length == 0)
        {
            pro2Slots[0].Enabled = true;
            enabledSlots = [pro2Slots[0]];
        }

        foreach (Pro2ControllerSlot slot in enabledSlots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StartPro2SlotAutoReconnectAsync(slot, cancellationToken);
        }

        UpdateMultiSlotInputStatus();
    }

    private async Task StartPro2SlotAutoReconnectAsync(
        Pro2ControllerSlot? slot,
        CancellationToken cancellationToken)
    {
        if (slot == null)
        {
            return;
        }

        slot.Enabled = true;
        if (!Running || !activeMode.HasValue || !ViewModelPolicies.IsMultiSlotMode(activeMode.Value))
        {
            ViiperDeviceProfile targetProfile = ViewModelPolicies.IsMultiSlotMode(selectedMode)
                ? SelectedProfile
                : ViiperDeviceProfile.Pro2;
            await SwitchModeAsync(targetProfile, cancellationToken);
        }

        if (!Running)
        {
            slot.Status = "请先连接手柄 1 并进入游戏，再连接其他槽位；本槽位的模式已保存。";
            return;
        }

        if (!slot.VirtualDeviceRunning)
        {
            if (!await StartSingleSlotSessionAsync(slot, RuntimeProfileForSlot(slot), cancellationToken))
            { UpdateMultiSlotInputStatus(); return; }
            if (slot.Session?.Profile.IsPs5Family == true) await ApplyDualSenseAudioGuardAsync(cancellationToken);
            AppendLog("[SLOT_HOT_ADD] slot=" + slot.Index + " existing_sessions_preserved=true");
        }

        if (slot.AutoReconnectTask is { IsCompleted: false })
        {
            slot.Status = slot.InputSource.IsRunning
                ? "已连接，自动守护正在运行。"
                : "自动连接已在持续寻找 Pro2。";
            UpdateMultiSlotInputStatus();
            return;
        }

        slot.AutoReconnectCts?.Dispose();
        slot.AutoReconnectCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
        slot.AutoReconnectEnabled = true;
        string preferredAddress = LastKnownPro2AddressForSlot(slot);
        slot.Status = string.IsNullOrWhiteSpace(preferredAddress)
            ? "自动连接已启动，正在寻找未被其他 Slot 占用的 Pro2。"
            : "自动连接已启动，优先寻找上次保存的 Pro2：" + preferredAddress;
        AppendLog("[SLOT_MULTI] slot=" + slot.Index + " auto enabled mode=" + slot.Session?.Profile.Mode +
                  " preferred=" + (string.IsNullOrWhiteSpace(preferredAddress) ? "none" : preferredAddress) +
                  " startup_auto=" + startupAutomationActive);
        slot.AutoReconnectTask = Pro2SlotAutoReconnectLoopAsync(slot, slot.AutoReconnectCts.Token);
        UpdateMultiSlotInputStatus();
    }

    private async Task Pro2SlotAutoReconnectLoopAsync(
        Pro2ControllerSlot slot,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        int failedAttemptStreak = 0;
        bool previouslyLive = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await WaitWhileAutoSleepingAsync(slot, cancellationToken);
                if (slot.InputSource.TryConsumeDisconnectSignal(
                        out Pro2BleDisconnectSignal lowLevelDisconnect))
                {
                    previouslyLive = false;
                    NotifyPro2Offline(
                        slot.Index,
                        lowLevelDisconnect,
                        autoReconnectContinues: true);
                    slot.Status = "Windows 已确认手柄离线，正在清理连接并自动重连。";
                    await bleOperationGate.WaitAsync(cancellationToken);
                    try
                    {
                        await slot.InputSource.StopAsync();
                    }
                    finally
                    {
                        bleOperationGate.Release();
                    }
                    slot.RefreshFromSource();
                    UpdateMultiSlotInputStatus();
                }

                bool live = slot.InputSource.IsRunning &&
                            slot.InputSource.TryGetLatest(out _, out TimeSpan age) &&
                            age <= TimeSpan.FromMilliseconds(LostInputTimeoutMilliseconds);
                if (live)
                {
                    slot.InputSource.RecheckLiveRate();
                    failedAttemptStreak = 0;
                    slot.RefreshFromSource();
                    RememberConnectedAddress(slot);
                    MarkStartupAutomationConnected("slot_" + slot.Index);
                    if (!previouslyLive)
                    {
                        previouslyLive = true;
                        OnAutoSleepLiveSessionStarted(slot, slot.InputSource);
                        AppendLog("[SLOT_MULTI] slot=" + slot.Index +
                                  " live address=" + slot.ConnectedAddress);
                        UpdateMultiSlotInputStatus();
                    }

                    if (await TryEnterAutoSleepAsync(slot, slot.InputSource, cancellationToken))
                    {
                        previouslyLive = false;
                        continue;
                    }

                    if (AutoOptimizeBle && slot.InputSource.CanOptimizeConnectionCompatibility && !Busy)
                        await OptimizeSlotCompatibilityAsync(slot, cancellationToken);
                    ReportReconnectRecovery(slot.Index);
                    await WaitForReconnectCheckAsync(slot.Index, cancellationToken);
                    continue;
                }

                if (TryPauseStartupAutomationAfterTimeout("slot_" + slot.Index, slot))
                {
                    break;
                }

                if (slot.InputSource.IsRunning)
                {
                    previouslyLive = false;
                    TimeSpan staleAge = slot.InputSource.TryGetLatest(out _, out TimeSpan measuredAge)
                        ? measuredAge
                        : TimeSpan.MaxValue;
                    NotifyPro2Offline(
                        slot.Index,
                        slot.InputSource.CreateInputTimeoutDisconnectSignal(staleAge),
                        autoReconnectContinues: true);
                    AppendLog("[SLOT_MULTI] slot=" + slot.Index +
                              " input stale age_ms=" +
                              (staleAge == TimeSpan.MaxValue ? "unknown" : staleAge.TotalMilliseconds.ToString("F0")) +
                              "; recycling BLE session.");
                    slot.Status = "输入中断，正在清理旧 BLE 连接并自动重连。";
                    await bleOperationGate.WaitAsync(cancellationToken);
                    try
                    {
                        await slot.InputSource.StopAsync();
                    }
                    finally
                    {
                        bleOperationGate.Release();
                    }
                    slot.RefreshFromSource();
                    UpdateMultiSlotInputStatus();
                }

                attempt++;
                previouslyLive = false;
                slot.Status = "自动连接第 " + attempt + " 次：扫描未被占用的 Pro2。";
                AppendLog("[SLOT_MULTI] slot=" + slot.Index + " attempt=" + attempt + " begin.");
                try
                {
                    await TryConnectPro2SlotOnceAsync(slot, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedAttemptStreak++;
                    TimeSpan retryDelay = ViewModelPolicies.Pro2ReconnectDelayForAttempt(failedAttemptStreak);
                    slot.Status = "连接异常：" + ViewModelPolicies.FirstLine(ex.Message) +
                                  "，" + retryDelay.TotalSeconds.ToString("F1") + " 秒后重试。";
                    AppendLog("[SLOT_MULTI] slot=" + slot.Index +
                              " attempt=" + attempt +
                              " failed retry_delay_ms=" + retryDelay.TotalMilliseconds.ToString("F0") +
                              " error=" + ex);
                    UpdateMultiSlotInputStatus();
                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }

                slot.RefreshFromSource();
                UpdateMultiSlotInputStatus();
                if (!slot.InputSource.IsRunning)
                {
                    failedAttemptStreak++;
                    TimeSpan retryDelay = ViewModelPolicies.Pro2ReconnectDelayForAttempt(failedAttemptStreak);
                    string diagnostic = slot.InputSource.LastScanDiagnostic;
                    slot.Status = string.IsNullOrWhiteSpace(diagnostic)
                        ? "未找到未占用 Pro2，" + retryDelay.TotalSeconds.ToString("F1") + " 秒后继续。"
                        : "未连接：" + diagnostic + " " + retryDelay.TotalSeconds.ToString("F1") + " 秒后继续。";
                    AppendLog("[SLOT_MULTI] slot=" + slot.Index +
                              " attempt=" + attempt +
                              " no_live retry_delay_ms=" + retryDelay.TotalMilliseconds.ToString("F0") +
                              " failed_streak=" + failedAttemptStreak);
                    await Task.Delay(retryDelay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AppendLog("[SLOT_MULTI] slot=" + slot.Index + " auto cancelled.");
        }
        catch (Exception ex)
        {
            slot.Status = "自动重连异常停止：" + ViewModelPolicies.FirstLine(ex.Message);
            AppendLog("[SLOT_MULTI] slot=" + slot.Index + " auto failed: " + ex);
        }
        finally
        {
            if (!shuttingDown && !cancellationToken.IsCancellationRequested)
            {
                slot.AutoReconnectEnabled = false;
            }
            slot.RefreshFromSource();
            UpdateMultiSlotInputStatus();
        }
    }

    private async Task TryConnectPro2SlotOnceAsync(
        Pro2ControllerSlot slot,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<string>(
            line => AppendLog("[PRO2_SLOT " + slot.Index + "] " + line));
        await bleOperationGate.WaitAsync(cancellationToken);
        try
        {
            var excluded = ConnectedPro2AddressesExcept(slot);
            AppendLog("[SLOT_ADDRESS_GUARD] slot=" + slot.Index + " excluded=" + string.Join(",", excluded) + " snapshot=inside_connection_gate");
            IReadOnlySet<string> preferred = slot.Index == 1 ? PrimaryPreferredAddresses(slot) : PreferredAddressSetForSlot(slot);
            bool onlyPreferred = slot.Index == 1 && !string.IsNullOrWhiteSpace(manualReconnectAddress) ||
                startupAutomationActive && preferred.Count > 0;
            if (slot.Index == 1 && manualReconnectTarget != null)
                await slot.InputSource.StartManualAsync(manualReconnectTarget, excluded, ResolveStickCalibration,
                    progress, cancellationToken);
            else await slot.InputSource.StartAsync(
                progress,
                excluded,
                preferred,
                onlyPreferred,
                ResolveStickCalibration,
                cancellationToken);
        }
        finally
        {
            bleOperationGate.Release();
        }

        slot.RefreshFromSource();
        if (slot.InputSource.IsRunning)
        {
            RememberConnectedAddress(slot);
            MarkStartupAutomationConnected("slot_connect_" + slot.Index);
        }
    }

    private IReadOnlySet<string> PreferredAddressSetForSlot(Pro2ControllerSlot slot)
    {
        string address = LastKnownPro2AddressForSlot(slot);
        if (string.IsNullOrWhiteSpace(address))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { address };
    }

    private string LastKnownPro2AddressForSlot(Pro2ControllerSlot slot)
    {
        string[] addresses = AppSettings.NormalizeAddressSlots(userSettings.LastConnectedPro2Addresses);
        int index = slot.Index - 1;
        return index >= 0 && index < addresses.Length
            ? addresses[index]
            : "";
    }

    private void RememberConnectedAddress(Pro2ControllerSlot slot)
    {
        string address = AppSettings.NormalizeBleAddress(slot.InputSource.ConnectedAddress);
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        ApplySavedStickCalibration(slot, address, logChange: true);
        string[] addresses = AppSettings.NormalizeAddressSlots(userSettings.LastConnectedPro2Addresses);
        int index = slot.Index - 1;
        if (index < 0 || index >= addresses.Length ||
            string.Equals(addresses[index], address, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        addresses[index] = address;
        userSettings.LastConnectedPro2Addresses = addresses;
        SaveUserSettings("[PRO2_BLE_ADDR]");
        AppendLog("[PRO2_BLE_ADDR] slot=" + slot.Index + " saved=" + address);
    }

    private StickCalibrationProfile? ResolveStickCalibration(string address)
    {
        return userSettings.TryGetStickCalibration(address, out StickCalibrationProfile profile)
            ? profile
            : null;
    }

    private void ApplySavedStickCalibration(
        Pro2ControllerSlot slot,
        string address,
        bool logChange)
    {
        string normalizedAddress = AppSettings.NormalizeBleAddress(address);
        StickCalibrationProfile? profile = ResolveStickCalibration(normalizedAddress);
        string calibrationKey = ViewModelPolicies.StickCalibrationKey(normalizedAddress, profile);
        bool captureActive = slot.InputSource.IsStickCalibrationCaptureActive;
        if (captureActive)
        {
            return;
        }

        bool alreadyApplied =
            string.Equals(
                slot.AppliedStickCalibrationKey,
                calibrationKey,
                StringComparison.Ordinal);
        string summary;
        if (captureActive || alreadyApplied)
        {
            summary = slot.InputSource.StickCalibrationSummary;
        }
        else if (profile != null || !slot.InputSource.IsRunning)
        {
            summary = slot.InputSource.SetStickCalibration(profile);
            slot.AppliedStickCalibrationKey = calibrationKey;
        }
        else
        {
            summary = slot.InputSource.StickCalibrationSummary;
            slot.AppliedStickCalibrationKey = calibrationKey;
        }
        slot.StickCalibrationStatus = profile == null
            ? "摇杆校准：未保存，使用中心自动学习 + 固定 ±1600 回退。"
            : profile.RangeCalibrated
                ? "摇杆校准：零位 + 完整行程已加载。"
                : "摇杆校准：零位已加载，尚未完成完整行程。";
        if (logChange && !captureActive && !alreadyApplied)
        {
            AppendLog("[PRO2_STICK_CAL] slot=" + slot.Index +
                      " address=" +
                      (normalizedAddress.Length == 0 ? "unknown" : normalizedAddress) +
                      " source=" + (profile == null ? "fixed_1600_fallback" : "saved_profile") +
                      " " + summary);
        }
    }

    private void MarkStartupAutomationConnected(string context)
    {
        if (!startupAutomationActive)
        {
            return;
        }

        startupAutomationActive = false;
        startupAutomationPaused = false;
        startupAutomationDeadlineUtc = null;
        AppendLog("[STARTUP_AUTO] connected context=" + context + " guard_continues=1");
        OnPropertyChanged(nameof(StartupAutomationSummary));
    }

    private bool TryPauseStartupAutomationAfterTimeout(string context, Pro2ControllerSlot? currentSlot)
    {
        if (!startupAutomationActive || !startupAutomationDeadlineUtc.HasValue ||
            DateTimeOffset.UtcNow < startupAutomationDeadlineUtc.Value)
        {
            return false;
        }

        startupAutomationActive = false;
        startupAutomationPaused = true;
        startupAutomationDeadlineUtc = null;
        AutoReconnectEnabled = false;
        foreach (Pro2ControllerSlot slot in pro2Slots)
        {
            slot.AutoReconnectEnabled = false;
            slot.Status = "开机自动连接 5 分钟内未 live，已暂停；请手动操作。";
            if (!ReferenceEquals(slot, currentSlot))
            {
                slot.AutoReconnectCts?.Cancel();
            }
        }
        if (currentSlot != null)
        {
            autoReconnectCts?.Cancel();
        }

        InputStatus = "开机自动连接已暂停：5 分钟内没有连接到 Pro2。";
        Status = "开机自动连接已暂停，避免 Windows BLE 长时间反复建立连接；请唤醒手柄后手动点击进入游戏。";
        AppendLog("[STARTUP_AUTO] paused timeout context=" + context + " deadline_seconds=300");
        if (!startupAutomationNoticeSent)
        {
            startupAutomationNoticeSent = true;
            RequestUserNotification(
                "开机自动连接已暂停",
                "5 分钟内没有连接到 " + SelectedModeLabel +
                " 的 Pro2。已暂停自动检索，避免 Windows BLE 长时间反复建立连接；请手动点击模式或进入游戏恢复。");
        }
        RaiseConnectionStateChanged();
        OnPropertyChanged(nameof(StartupAutomationSummary));
        return true;
    }

    private async Task CancelStartupAutomationForManualOperationAsync(string operation)
    {
        if (!startupAutomationActive && !startupAutomationPaused)
        {
            return;
        }

        startupAutomationActive = false;
        startupAutomationPaused = false;
        startupAutomationDeadlineUtc = null;
        AppendLog("[STARTUP_AUTO] cancelled_by_manual operation=" + operation);
        await StopAutoReconnectAsync();
        Status = "已取消开机自动连接，转为手动操作：" + operation;
        OnPropertyChanged(nameof(StartupAutomationSummary));
    }

    private void RequestUserNotification(string title, string message)
    {
        UserNotificationRequested?.Invoke(title, message);
    }

    private void OnPro2DisconnectDetected(
        int slotIndex,
        Pro2BleDisconnectSignal signal)
    {
        // Wake the guard on the native event thread; UI dispatch latency must
        // not add a full 750ms polling interval before cleanup/re-discovery.
        int index = Math.Clamp(slotIndex - 1, 0, reconnectWake.Length - 1);
        Interlocked.CompareExchange(ref reconnectObservedTicks[index], Stopwatch.GetTimestamp(), 0);
        reconnectWake[index].Notify();
        void Notify()
        {
            if (shuttingDown)
            {
                return;
            }

            Pro2ControllerSlot slot = pro2Slots[Math.Clamp(slotIndex - 1, 0, pro2Slots.Length - 1)];
            bool reconnecting = slotIndex == 1
                ? AutoReconnectEnabled || slot.AutoReconnectEnabled
                : slot.AutoReconnectEnabled;
            NotifyPro2Offline(slotIndex, signal, reconnecting);
        }

        if (uiContext != null)
        {
            uiContext.Post(_ => Notify(), null);
        }
        else
        {
            Notify();
        }
    }

    private void NotifyPro2Offline(
        int slotIndex,
        Pro2BleDisconnectSignal signal,
        bool autoReconnectContinues)
    {
        int noticeIndex = Math.Clamp(slotIndex - 1, 0, lastDisconnectNoticeSequence.Length - 1);
        if (autoReconnectContinues)
            Interlocked.CompareExchange(ref reconnectObservedTicks[noticeIndex], Stopwatch.GetTimestamp(), 0);
        if (signal.ConnectionSequence > 0 &&
            lastDisconnectNoticeSequence[noticeIndex] == signal.ConnectionSequence)
        {
            AppendLog("[OFFLINE_NOTICE] duplicate_suppressed=1 slot=" +
                      slotIndex + " " + signal.TelemetryValue);
            return;
        }

        lastDisconnectNoticeSequence[noticeIndex] = signal.ConnectionSequence;

        // 普通离线（手柄关机、休眠、拔线）只在界面状态里显示，不打断用户。
        // 只有链路异常才提示，而且一分钟内最多一条：插拔线时链路会连续抖动，
        // 逐次弹窗只会刷屏。
        AppendLog("[OFFLINE_NOTICE] slot=" + slotIndex +
                  " abnormal=" + signal.IsAbnormal + " " + signal.TelemetryValue +
                  " reason=\"" + DescribePro2DisconnectReason(signal) + "\"");
        if (!signal.IsAbnormal)
        {
            return;
        }

        long nowTicks = Stopwatch.GetTimestamp();
        long previous = Interlocked.Read(ref lastOfflineNoticeTicks);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, nowTicks).TotalSeconds < 60)
        {
            AppendLog("[OFFLINE_NOTICE] throttled=1");
            return;
        }
        Interlocked.Exchange(ref lastOfflineNoticeTicks, nowTicks);

        RequestUserNotification(
            "手柄连接中断",
            DescribePro2DisconnectReason(signal) + "\n" +
            (autoReconnectContinues ? "正在尝试重新连接。" : "可以在主界面点「连接手柄」重新连接。"));
    }

    /// <summary>
    /// 把链路层的错误码翻成用户能直接照做的一句话。
    /// 不出现 GATT、FD2、资源占用代码这类只有开发者才看得懂的说法。
    /// </summary>
    private static string DescribePro2DisconnectReason(
        Pro2BleDisconnectSignal signal)
    {
        if (signal.Detector == "fd2_input_timeout")
        {
            return "已经两秒没有收到手柄数据，多半是手柄休眠、没电，或者蓝牙信号被打断了。";
        }

        return signal.BluetoothErrorCode switch
        {
            "RadioNotAvailable" =>
                "电脑的蓝牙被关掉了，或者蓝牙适配器掉线了。",
            "ResourceInUse" =>
                "手柄的蓝牙通道被别的程序占用了。",
            "OtherError" =>
                "蓝牙连接异常中断。",
            "DisabledByPolicy" =>
                "系统策略禁止了蓝牙访问。",
            "NotSupported" or "TransportNotSupported" =>
                "当前蓝牙适配器不支持手柄需要的连接方式。",
            "DisabledByUser" =>
                "蓝牙已被关闭。",
            "ConsentRequired" =>
                "系统需要重新允许蓝牙访问。",
            "DeviceNotConnected" =>
                "手柄已断开，可能是关机、没电，或者离开了蓝牙范围。",
            _ =>
                "手柄已断开。可能是关机、没电，或者蓝牙信号中断。"
        };
    }

    private HashSet<string> ConnectedPro2AddressesExcept(Pro2ControllerSlot slot)
    {
        return SlotAddressPolicy.Exclusions(slot.Index, pro2Slots.Select(other => new SlotAddressInfo(
            other.Index, other.Enabled, LastKnownPro2AddressForSlot(other), other.InputSource.ConnectedAddress,
            other.InputSource.IsTransportConnected || other.InputSource.IsRunning)));
    }

    private async Task ScanPro2SlotAsync(
        Pro2ControllerSlot? slot,
        CancellationToken cancellationToken)
    {
        if (slot == null)
        {
            return;
        }

        var progress = new Progress<string>(
            line => AppendLog("[PRO2_SLOT " + slot.Index + "] " + line));
        IReadOnlyList<string> found =
            await slot.InputSource.ScanAsync(progress, TimeSpan.FromSeconds(8), cancellationToken);
        slot.Status = found.Count == 0
            ? "扫描完成：未发现 Pro2。"
            : "扫描完成：候选 " + found.Count + " 个，点击连接会自动避开已占用地址。";
        slot.RefreshFromSource();
        UpdateMultiSlotInputStatus();
    }

    private async Task DisconnectPro2SlotAsync(
        Pro2ControllerSlot? slot,
        CancellationToken cancellationToken)
    {
        if (slot == null)
        {
            return;
        }

        await slot.StopAutoReconnectAsync();
        await bleOperationGate.WaitAsync(cancellationToken);
        try
        {
            await slot.InputSource.StopAsync();
        }
        finally
        {
            bleOperationGate.Release();
        }

        slot.Status = "已断开真实 Pro2；虚拟设备仍保持在线。";
        slot.RefreshFromSource();
        UpdateMultiSlotInputStatus();
    }

    private async Task CalibrateStickCenterSlotAsync(
        Pro2ControllerSlot? slot,
        CancellationToken cancellationToken)
    {
        // 第一页的新连接管线：直接对真实输入帧采样，不再依赖旧的 BLE 槽位。
        Pro2InputSession? liveLink = linkService.InputSession;
        if (liveLink is not null && liveLink.HasFrames)
        {
            await CalibrateLinkStickAsync(liveLink, rangeMode: false, cancellationToken);
            return;
        }

        if (!TryGetLiveCalibrationTarget(
                slot,
                out Pro2ControllerSlot liveSlot,
                out string address))
        {
            return;
        }

        RequestUserNotification(
            "摇杆零位校准",
            "采样现在开始。请把手柄平放并完全松开左右摇杆，保持两秒，不要触碰摇杆或按键。");
        string started = liveSlot.InputSource.StartManualStickCenterCalibration();
        AppendLog("[PRO2_STICK_CAL] slot=" + liveSlot.Index +
                  " address=" + address + " " + started);
        liveSlot.Status = "摇杆零位校准中：请完全松开左右摇杆，保持两秒。";
        liveSlot.StickCalibrationStatus = "摇杆校准：正在采集零位...";
        Status = liveSlot.Name + " 正在采集摇杆零位。";
        await WaitForStickCalibrationAsync(
            liveSlot,
            "零位",
            2,
            cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);

        StickCalibrationResult result =
            liveSlot.InputSource.CompleteManualStickCenterCalibration();
        FinishStickCalibration(liveSlot, address, result, "零位");
    }

    private async Task CalibrateStickRangeSlotAsync(
        Pro2ControllerSlot? slot,
        CancellationToken cancellationToken)
    {
        Pro2InputSession? liveLink = linkService.InputSession;
        if (liveLink is not null && liveLink.HasFrames)
        {
            await CalibrateLinkStickAsync(liveLink, rangeMode: true, cancellationToken);
            return;
        }

        if (!TryGetLiveCalibrationTarget(
                slot,
                out Pro2ControllerSlot liveSlot,
                out string address))
        {
            return;
        }
        if (!liveSlot.InputSource.StickCalibrationProfile.CenterCalibrated)
        {
            liveSlot.StickCalibrationStatus = "摇杆校准：请先执行“零位 2 秒”。";
            Status = liveSlot.Name + " 尚未完成零位校准，请先点击“零位 2 秒”。";
            AppendLog("[PRO2_STICK_CAL] slot=" + liveSlot.Index +
                      " address=" + address +
                      " status=rejected mode=range reason=center_calibration_required");
            return;
        }

        string started = liveSlot.InputSource.StartManualStickRangeCalibration();
        if (!started.Contains("status=started", StringComparison.Ordinal))
        {
            liveSlot.StickCalibrationStatus = "摇杆校准：完整行程启动失败。";
            Status = "完整行程校准未启动：" + started;
            AppendLog("[PRO2_STICK_CAL] slot=" + liveSlot.Index +
                      " address=" + address + " " + started);
            return;
        }

        RequestUserNotification(
            "摇杆完整行程校准",
            "采样现在开始，共八秒。请把左右摇杆分别贴着外圈缓慢转满至少三圈，确保上、下、左、右都推到底，最后松开摇杆。");
        AppendLog("[PRO2_STICK_CAL] slot=" + liveSlot.Index +
                  " address=" + address + " " + started);
        liveSlot.Status = "完整行程校准中：八秒内将左右摇杆沿外圈各转满至少三圈。";
        liveSlot.StickCalibrationStatus = "摇杆校准：正在采集四轴八方向端点...";
        Status = liveSlot.Name + " 正在采集完整摇杆行程。";
        await WaitForStickCalibrationAsync(
            liveSlot,
            "完整行程",
            8,
            cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);

        StickCalibrationResult result =
            liveSlot.InputSource.CompleteManualStickRangeCalibration();
        FinishStickCalibration(liveSlot, address, result, "完整行程");
    }

    /// <summary>第一页新连接管线：直接对真实手柄输入帧做零位/行程采样。</summary>
    private async Task CalibrateLinkStickAsync(
        Pro2InputSession session,
        bool rangeMode,
        CancellationToken cancellationToken)
    {
        string label = rangeMode ? "完整行程" : "零位";
        string key = Pro2GyroBiasStore.CurrentKey;
        int seconds = rangeMode ? 8 : 2;
        session.StartStickSampling(rangeMode);
        RequestUserNotification(
            "摇杆" + label + "校准",
            rangeMode
                ? "采样开始，共八秒。请把左右摇杆分别贴着外圈缓慢转满至少三圈，四个方向都要推到底。"
                : "采样开始，共两秒。请完全松开左右摇杆并保持不动。");
        AppendLog("[PRO2_STICK_CAL] link_key=" + key + " mode=" + label + " started");
        Status = "正在采集摇杆" + label + "，请按提示操作手柄。";
        for (int remaining = seconds; remaining > 0; remaining--)
        {
            Status = "摇杆" + label + "校准采集中，剩余 " + remaining + " 秒。";
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        StickCalibrationResult result = session.CompleteStickSampling(rangeMode);
        if (result.Success && result.Profile is not null)
        {
            string address = Pro2GyroBiasStore.StickCalibrationKey(key);
            userSettings.SetStickCalibration(address, result.Profile);
            SaveUserSettings("[PRO2_STICK_CAL]");
            RaiseStickCalibrationChanged();
            Status = "摇杆" + label + "校准完成，已保存到配置页并立即生效。";
            RequestUserNotification("摇杆" + label + "校准完成", result.Message + " 下次连接会自动加载。");
        }
        else
        {
            Status = "摇杆" + label + "校准失败：" + result.Message;
            RequestUserNotification("摇杆" + label + "校准未完成", result.Message);
        }
        AppendLog("[PRO2_STICK_CAL] link_key=" + key + " mode=" + label +
                  " success=" + result.Success + " " + result.Message);
    }

    private async Task WaitForStickCalibrationAsync(
        Pro2ControllerSlot slot,
        string label,
        int seconds,
        CancellationToken cancellationToken)
    {
        for (int remaining = seconds; remaining > 0; remaining--)
        {
            slot.StickCalibrationStatus =
                "摇杆校准：" + label + "采集中，剩余 " + remaining + " 秒...";
            Status = slot.Name + " " + label + "校准采集中，剩余 " + remaining + " 秒。";
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private bool TryGetLiveCalibrationTarget(
        Pro2ControllerSlot? slot,
        out Pro2ControllerSlot liveSlot,
        out string address)
    {
        if (slot == null || !slot.InputSource.IsRunning)
        {
            liveSlot = slot ?? pro2Slots[0];
            address = "";
            Status = "请先连接需要校准的真实 Pro2 手柄。";
            if (slot != null)
            {
                slot.StickCalibrationStatus = "摇杆校准：没有 live 手柄，无法采样。";
                AppendLog("[PRO2_STICK_CAL] slot=" + slot.Index +
                          " status=rejected reason=no_live_source");
            }
            return false;
        }

        address = AppSettings.NormalizeBleAddress(slot.InputSource.ConnectedAddress);
        if (address.Length == 0)
        {
            liveSlot = slot;
            Status = "当前 Pro2 没有可保存的 BLE 地址，无法建立独立校准档案。";
            slot.StickCalibrationStatus = "摇杆校准：BLE 地址不可用。";
            AppendLog("[PRO2_STICK_CAL] slot=" + slot.Index +
                      " status=rejected reason=missing_ble_address");
            return false;
        }

        liveSlot = slot;
        return true;
    }

    private void FinishStickCalibration(
        Pro2ControllerSlot slot,
        string expectedAddress,
        StickCalibrationResult result,
        string label)
    {
        string currentAddress =
            AppSettings.NormalizeBleAddress(slot.InputSource.ConnectedAddress);
        bool sameLiveController =
            slot.InputSource.IsRunning &&
            string.Equals(expectedAddress, currentAddress, StringComparison.OrdinalIgnoreCase);
        if (!sameLiveController)
        {
            StickCalibrationProfile? savedProfile =
                ResolveStickCalibration(currentAddress.Length > 0
                    ? currentAddress
                    : expectedAddress);
            slot.InputSource.SetStickCalibration(savedProfile);
            slot.StickCalibrationStatus = "摇杆校准：手柄在采样过程中离线，未保存。";
            slot.Status = "校准中断：真实 Pro2 已离线或连接对象发生变化。";
            Status = slot.Name + " " + label + "校准中断，旧配置保持不变。";
            AppendLog("[PRO2_STICK_CAL] slot=" + slot.Index +
                      " address=" + expectedAddress +
                      " status=rejected mode=" + label +
                      " reason=controller_changed_or_offline result=\"" +
                      result.Message + "\"");
            RequestUserNotification(
                label + "校准中断",
                "真实 Pro2 在采样期间离线或连接对象发生变化，旧配置没有被覆盖。");
            return;
        }
        if (!result.Success || result.Profile == null)
        {
            slot.StickCalibrationStatus = "摇杆校准：" + label + "采样不完整，旧配置未覆盖。";
            slot.Status = label + "校准失败，请按提示重试。";
            Status = slot.Name + " " + label + "校准未通过，旧配置保持不变。";
            AppendLog("[PRO2_STICK_CAL] slot=" + slot.Index +
                      " address=" + expectedAddress + " " + result.Message);
            RequestUserNotification(
                label + "校准未完成",
                "采样没有通过，旧配置没有被覆盖。\n\n" +
                ViewModelPolicies.StickCalibrationFailureText(result.Message));
            return;
        }

        userSettings.SetStickCalibration(expectedAddress, result.Profile);
        SaveUserSettings("[PRO2_STICK_CAL]");
        RaiseStickCalibrationChanged();
        string applied = slot.InputSource.SetStickCalibration(result.Profile);
        slot.AppliedStickCalibrationKey =
            ViewModelPolicies.StickCalibrationKey(expectedAddress, result.Profile);
        slot.StickCalibrationStatus = result.Profile.RangeCalibrated
            ? "摇杆校准：零位 + 完整行程已保存，按当前手柄地址自动加载。"
            : "摇杆校准：零位已保存，请继续执行完整行程校准。";
        slot.Status = label + "校准成功，当前连接已立即应用。";
        Status = slot.Name + " " + label + "校准完成。";
        AppendLog("[PRO2_STICK_CAL] slot=" + slot.Index +
                  " address=" + expectedAddress + " " + result.Message +
                  " apply=\"" + applied + "\"");
        RequestUserNotification(
            label + "校准完成",
            label + "校准已保存并立即应用到当前手柄。\n\n" +
            result.Profile.Summary);
    }

    private void UpdateMultiSlotInputStatus()
    {
        OnPropertyChanged(nameof(MultiControllerSummary));
        if (!activeMode.HasValue || !ViewModelPolicies.IsMultiSlotMode(activeMode.Value))
        {
            RaiseConnectionStateChanged();
            return;
        }

        int virtualCount = pro2Slots.Count(slot => slot.VirtualDeviceRunning);
        int liveCount = pro2Slots.Count(slot => slot.InputSource.IsRunning);
        InputStatus = ActiveProfile.Label + " 多手柄：虚拟实例 " + virtualCount +
                      " 个，真实 Pro2 BLE live " + liveCount +
                      " 个。启用的 Slot 会独立扫描、独立回传震动。";
        Status = InputStatus;
        RaiseConnectionStateChanged();
    }

    private async Task StopAutoReconnectAsync()
    {
        CancellationTokenSource? reconnectCts = autoReconnectCts;
        Task? reconnectTask = autoReconnectTask;
        autoReconnectCts = null;
        autoReconnectTask = null;
        AutoReconnectEnabled = false;
        if (reconnectCts != null)
        {
            reconnectCts.Cancel();
        }
        if (reconnectTask != null)
        {
            try
            {
                await reconnectTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        reconnectCts?.Dispose();

        foreach (Pro2ControllerSlot slot in pro2Slots)
        {
            await slot.StopAutoReconnectAsync();
            slot.RefreshFromSource();
        }
    }

    private async Task StartAsync(ViiperDeviceProfile profile, CancellationToken cancellationToken)
    {
        if (Running)
        {
            return;
        }

        try
        {
            // 新连接层就绪时用它的输入（它解析的是同一批原始报告）；
            // 旧 BLE 源只在旧的多手柄流程里才用到。
            IGamepadInputSource activeInput = LinkService.InputSession ?? (IGamepadInputSource)inputSource;
            IGamepadOutputSink activeOutput = activeInput as IGamepadOutputSink ?? inputSource;
            bool inputLive = activeInput.IsRunning &&
                             activeInput.TryGetLatest(out _, out TimeSpan inputAge) &&
                             inputAge <= TimeSpan.FromMilliseconds(500);
            Running = true;
            Status = "正在启动 " + profile.Label + " 虚拟手柄...";
            OutputRateOption pushRate = SelectedPushRateOption;
            ImuResampleOption gyro = ImuResampleOption.Default;
            GyroAxisInversion gyroAxisInversion = default;
            Ps5ImuMappingOption ps5ImuMapping = Ps5ImuMappingOption.Default;
            Ps5OutputImuTuning ps5OutputImuTuning = SelectedPs5OutputImuTuning;
            ProfessionalImuOptions professionalImuOptions = profile.IsProfessionalImuTest
                ? ProfessionalImuOptions.ForTestModes(
                    ps5OutputImuTuning,
                    professionalInvertGyroPitch,
                    professionalInvertGyroYaw,
                    professionalInvertGyroRoll)
                : ProfessionalImuOptions.Default;
            string professionalTelemetry = professionalImuOptions.Enabled
                ? professionalImuOptions.TelemetryValue
                : "disabled";
            ViiperDeviceProfile runtimeProfile = profile with
            {
                SendInterval = pushRate.Interval,
                SourcePaced = pushRate.SourcePaced
            };
            AppendLog("[START] mode=" + runtimeProfile.Label +
                      " type=" + runtimeProfile.DeviceType +
                      " cadence=" + ViewModelPolicies.PushCadenceTelemetry(pushRate) +
                      " interval_ms=" + pushRate.Interval.TotalMilliseconds.ToString("F1") +
                      " gyro_mode=" + gyro.Label +
                      " ps5_imu_map=" + ps5ImuMapping.Mapping.TelemetryValue +
                      " ps5_output_imu=" + ps5OutputImuTuning.TelemetryValue +
                      " gyro_axis_inv=" + gyroAxisInversion.TelemetryValue +
                      " professional_imu=" + professionalTelemetry +
                      " backend=" + SelectedBackendOption.Label +
                      " flush=immediate");
            if (runtimeProfile.Mode == ViiperVirtualMode.DualSenseLike ||
                runtimeProfile.Mode == ViiperVirtualMode.DualSenseProfessionalImuTest)
            {
                AppendDualSenseAudioEndpointHint();
            }
            else if (runtimeProfile.Mode == ViiperVirtualMode.Pro1)
            {
                AppendLog("[PRO1_IDENTITY] type=nspro vid=0x057e pid=0x2009 product=\"Pro Controller\" paddles=software_mapping feedback=switch1_hd_4byte_per_side source=physical_fd2_only imu=latest_sample_held_in_three_protocol_slots");
            }
            if (runtimeProfile.Mode == ViiperVirtualMode.DualSenseProfessionalImuTest)
            {
                AppendLog("[PRO_IMU] PS5 test mode uses DualSense haptic identity, so ordinary PS5 HD audio-to-rumble path is reused.");
            }
            if (runtimeProfile.Mode == ViiperVirtualMode.XboxProfessionalImuTest)
            {
                AppendLog("[PRO_IMU] Xbox test mode does not claim native gyro; XboxOutputMode=Off by default, IMU is diagnostic CSV/log only.");
            }
            if (runtimeProfile.IsProfessionalImuTest)
            {
                AppendLog("[PRO_IMU] Professional IMU mode entered output_mode=" + runtimeProfile.Label +
                          " sample_handling=" + professionalImuOptions.OutputSampleMode +
                          " source_rate=current_ble" +
                          " virtual_report_rate=" + professionalImuOptions.OutputReportRateMode +
                          " ProjectAccel=+X,+Z,-Y ProjectGyro=+X,+Z,-Y" +
                          " GyroScale=" + ps5OutputImuTuning.GyroScalePitch.ToString("0.###") +
                          "," + ps5OutputImuTuning.GyroScaleYaw.ToString("0.###") +
                          "," + ps5OutputImuTuning.GyroScaleRoll.ToString("0.###") +
                          " DualSenseGyroRawPerDps=" + ProfessionalImuConverter.DualSenseGyroRawPerDps.ToString("0.###") +
                          " output_gyro_invert_pitch=" + professionalInvertGyroPitch.ToString().ToLowerInvariant() +
                          " output_gyro_invert_yaw=" + professionalInvertGyroYaw.ToString().ToLowerInvariant() +
                          " output_gyro_invert_roll=" + professionalInvertGyroRoll.ToString().ToLowerInvariant() +
                          " bias_status=NotCalibrated bias_source=none" +
                          " professional_gyro_uncalibrated_behavior=" + professionalImuOptions.ProfessionalGyroUncalibratedBehavior +
                          " output_gyro_muted_until_calibrated=true" +
                          " integral_state=Disabled integral_running=false" +
                          " hid_audit_builder=" + DualSenseProfessionalHidLayout.BuilderName +
                          " hid_audit_report_id=" + DualSenseProfessionalHidLayout.ReportIdLabel +
                          " hid_audit_report_len=" + DualSenseProfessionalHidLayout.ReportLength +
                          " hid_audit_gyro_offsets=" + DualSenseProfessionalHidLayout.GyroXOffset +
                          "," + DualSenseProfessionalHidLayout.GyroYOffset +
                          "," + DualSenseProfessionalHidLayout.GyroZOffset +
                          " hid_audit_accel_offsets=" + DualSenseProfessionalHidLayout.AccelXOffset +
                          "," + DualSenseProfessionalHidLayout.AccelYOffset +
                          "," + DualSenseProfessionalHidLayout.AccelZOffset +
                          " legacy_ps5_mapper_after_professional=false");
            }
            ResolveExperimentalBackendSelection(runtimeProfile);
            // 新连接层只有一个真实手柄：始终走单实例会话，
            // 旧的 4 槽部署只在旧的多手柄流程里用。
            if (ViewModelPolicies.IsMultiSlotMode(runtimeProfile.Mode) && LinkService.InputSession is null)
            {
                await StartMultiSlotSessionsAsync(runtimeProfile, cancellationToken);
                SetActiveMode(runtimeProfile.Mode);
                RefreshActiveGyroSensitivity();
                int runningSlots = pro2Slots.Count(slot => slot.VirtualDeviceRunning);
                if (HasPs5VirtualOutput &&
                    AudioEndpointGuardEnabled)
                {
                    await ApplyDualSenseAudioGuardAsync(cancellationToken);
                }
                DiagnoseSteamControllerCache(runtimeProfile);
                Status = runtimeProfile.Label + " 多手柄模式已部署 " + runningSlots +
                         " 个独立 VIIPER 实例。点击“连接 Pro2 · 进入游戏”后，每个启用 Slot 会独立寻找真实手柄。";
                return;
            }

            var progress = new DirectProgress<string>(AppendLog);
            OutputPadSession? createdSession = null;
            var faultProgress = new Progress<Exception>(
                ex =>
                {
                    if (createdSession != null)
                    {
                        _ = RecoverFaultedSessionAsync(createdSession, ex);
                    }
                });
            RefreshMappingRuntime();
            createdSession = new OutputPadSession(
                new OutputBridgeClient(Host, ParsePort()),
                runtimeProfile,
                progress,
                activeInput,
                activeOutput,
                faultProgress,
                gyro.Mode,
                gyroAxisInversion,
                ps5ImuMapping.Mapping,
                ps5OutputImuTuning,
                professionalImuOptions,
                professionalHidAuditController,
                new Progress<ProfessionalImuUiSnapshot>(UpdateProfessionalImuSnapshot),
                userSettings.RearButtons);
            session = createdSession;
            await createdSession.StartAsync(cancellationToken);
            SetActiveMode(runtimeProfile.Mode);
            RefreshActiveGyroSensitivity();
            if ((runtimeProfile.Mode == ViiperVirtualMode.DualSenseLike ||
                 runtimeProfile.Mode == ViiperVirtualMode.DualSenseProfessionalImuTest) &&
                AudioEndpointGuardEnabled)
            {
                await ApplyDualSenseAudioGuardAsync(cancellationToken);
            }
            DiagnoseSteamControllerCache(runtimeProfile);
            Status = runtimeProfile.Label + " 虚拟手柄已创建。" +
                (inputLive ? "输入已经接通。" : "正在等待手柄输入。");
        }
        catch (Exception ex)
        {
            string failure = "启动失败：" + ExplainStartFailure(ex);
            AppendLog("ERROR start: " + ex);
            await StopSessionAsync(updateStatus: false);
            await CleanupVirtualDeviceResidueAsync(
                "start_failed_" + profile.Mode,
                cancellationToken,
                includePnpDump: false);
            Status = failure;
        }
    }

    private void ResolveExperimentalBackendSelection(ViiperDeviceProfile profile)
    {
        VirtualBackendOption backend = SelectedBackendOption;
        if (backend.Mode == VirtualBackendMode.ViiperServer)
        {
            AppendLog("[BACKEND] active=viiper_server reason=stable_three_mode_haptic_path");
            return;
        }

        string libPath = FindLibViiperCandidate() ?? "";
        string reason = backend.Mode switch
        {
            VirtualBackendMode.LibViiperExperimental when string.IsNullOrWhiteSpace(libPath) =>
                "libVIIPER.dll not found in app/runtime folders",
            VirtualBackendMode.LibViiperExperimental =>
                "libVIIPER.dll found at " + libPath + " but V6.2 has not bound DualSense/NS2Pro three-mode entrypoints yet",
            VirtualBackendMode.EmbeddedUsbipExperimental =>
                "C# embedded USBIP server is scaffolded as the V6.2 research target, but the release build keeps VIIPER for PS5 HD haptic safety",
            _ => "unknown experimental backend"
        };
        AppendLog("[BACKEND] requested=" + backend.Label +
                  " mode=" + backend.Mode +
                  " profile=" + profile.DeviceType +
                  " active=viiper_server fallback_reason=\"" + reason + "\"");
    }

    private async Task<bool> StartSingleSlotSessionAsync(Pro2ControllerSlot slot, ViiperDeviceProfile runtimeProfile, CancellationToken cancellationToken)
    {
        if (slot.Session != null && slot.VirtualDeviceRunning) return true;
        cancellationToken.ThrowIfCancellationRequested();
        slot.Session = null;
        slot.VirtualDeviceRunning = false;
        slot.Status = "正在创建独立 " + runtimeProfile.Label + " 虚拟设备...";

        ViiperDeviceProfile slotProfile = runtimeProfile with
        {
            DeviceSpecificSerialNumber =
                ViiperDeviceProfile.SlotSerialNumber(runtimeProfile.Mode, slot.Index)
        };
        var progress = new DirectProgress<string>(
            line => AppendLog("[SLOT " + slot.Index + "] " + line));
        var faultProgress = new Progress<Exception>(
            ex =>
            {
                slot.Status = "VIIPER 数据流异常：" + ViewModelPolicies.FirstLine(ex.Message);
                AppendLog("[SLOT " + slot.Index + "] ERROR stream: " + ex);
            });
        RefreshMappingRuntime();
        var createdSession = new OutputPadSession(
            new OutputBridgeClient(Host, ParsePort()),
            slotProfile,
            progress,
            slot.InputSource,
            slot.InputSource,
            faultProgress,
            ImuResampleOption.Default.Mode,
            default,
            Ps5ImuMappingOption.Default.Mapping,
            SelectedPs5OutputImuTuning,
            ProfessionalImuOptions.Default,
            professionalHidAuditController,
            new Progress<ProfessionalImuUiSnapshot>(UpdateProfessionalImuSnapshot),
            userSettings.RearButtons);

        try
        {
            await createdSession.StartAsync(cancellationToken);
            slot.Session = createdSession;
            slot.VirtualDeviceRunning = true;
            slot.Status = "虚拟 " + runtimeProfile.Label + " 已创建，等待真实手柄连接。";
            slot.RefreshFromSource();

            AppendLog("[SLOT_MULTI] slot=" + slot.Index +
                      " virtual_ready=1 mode=" + slotProfile.Mode + " serial=" + slotProfile.DeviceSpecificSerialNumber +
                      " device_type=" + slotProfile.DeviceType);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { await createdSession.DisposeAsync(); throw; }
        catch (Exception ex)
        {
            await createdSession.DisposeAsync();
            slot.Session = null;
            slot.VirtualDeviceRunning = false;
            slot.Status = "虚拟设备创建失败：" + ViewModelPolicies.FirstLine(ex.Message);
            AppendLog("[SLOT_MULTI] slot=" + slot.Index + " virtual_failed: " + ex);
            return false;
        }
    }

    private async Task StartMultiSlotSessionsAsync(
        ViiperDeviceProfile runtimeProfile,
        CancellationToken cancellationToken)
    {
        Pro2ControllerSlot[] enabledSlots = pro2Slots.Where(slot => slot.Enabled).ToArray();
        if (enabledSlots.Length == 0)
        {
            pro2Slots[0].Enabled = true;
            enabledSlots = [pro2Slots[0]];
            AppendLog("[SLOT_MULTI] no enabled slot; slot=1 enabled automatically.");
        }

        int started = 0;
        foreach (Pro2ControllerSlot slot in enabledSlots)
            if (await StartSingleSlotSessionAsync(slot, RuntimeProfileForSlot(slot), cancellationToken)) started++;

        foreach (Pro2ControllerSlot slot in pro2Slots.Where(slot => !slot.Enabled))
        {
            slot.VirtualDeviceRunning = false;
            slot.Status = "未启用，不创建 Steam 控制器实例。";
            slot.RefreshFromSource();
        }

        if (started == 0)
        {
            throw new InvalidOperationException(runtimeProfile.Label + " 多手柄模式没有任何 Slot 成功创建虚拟设备。");
        }

        AppendLog("[SLOT_MULTI] mode=" + runtimeProfile.Mode +
                  " virtual_instances=" + started +
                  " enabled_slots=" + string.Join(",", enabledSlots.Select(slot => slot.Index)) +
                  " isolation=per_slot_ble_source_and_rumble_sink");
    }

    private void AppendDualSenseAudioEndpointHint()
    {
        AppendLog("[AUDIO_HINT] PS5 mode exposes a DualSense audio/haptic endpoint for HD vibration. " +
                  "Do not set \"DualSense Wireless Controller\" as the Windows default speaker/headset. " +
                  "If desktop/game audio stutters or disappears, set the default playback device back to the real speakers/headset; " +
                  "the DualSense endpoint should be used by games only for controller haptic audio. The app will now guard defaults automatically.");
    }

    private async Task ApplyDualSenseAudioGuardAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(900, cancellationToken);
            RunAudioEndpointGuard();
            StartBuiltInAudioGuard();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog("[AUDIO_GUARD] warning: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private Task FixAudioDefaultsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunAudioEndpointGuard();
        Status = "已检查 Windows 默认播放/通信/麦克风，DualSense 不会作为默认音频设备。";
        return Task.CompletedTask;
    }

    private async Task RunWin10BleDiagnosticAsync(CancellationToken cancellationToken)
    {
        Pro2ControllerSlot? slot = pro2Slots.FirstOrDefault(
            candidate => candidate.InputSource.IsRunning);
        if (slot == null)
        {
            throw new InvalidOperationException(
                "没有已连接的真实 Pro2。先完成 BLE 连接，再运行 15 秒诊断。");
        }

        Status = "正在采集 Slot " + slot.Index +
                 " 的真实 FD2 Notify，持续 15 秒；请保持手柄唤醒。";
        var progress = new Progress<string>(
            line => AppendLog("[SLOT " + slot.Index + "] " + line));
        Win10BleDiagnosticReport report =
            await slot.InputSource.RunWin10BleDiagnosticAsync(
                TimeSpan.FromSeconds(15),
                progress,
                cancellationToken);
        (string jsonPath, string textPath) =
            await Win10BleDiagnostic.SaveAsync(report, cancellationToken);
        AppendLog("[BLE_TURBO_REPORT] json=\"" + jsonPath +
                  "\" text=\"" + textPath + "\"");
        Status = report.MeasurementValid ? "BLE Turbo 诊断完成：FD2=" +
                 report.Fd2NotifyHz.ToString("F2") + "Hz，P50=" +
                 report.Fd2NotifyGaps.P50Ms.ToString("F2") + "ms，判定=" +
                 report.Verdict + "。报告：" + jsonPath :
                 "BLE 诊断未通过：本轮连接或输入不连续。报告：" + jsonPath;
    }

    private async Task RunBleTurboConfiguratorAsync(
        bool restore,
        CancellationToken cancellationToken,
        bool experimentalConsent = false)
    {
        try { await RunBleTurboConfiguratorCoreAsync(restore, cancellationToken, experimentalConsent); }
        catch (Exception ex)
        {
            RecordWin10ConfigurationAttempt(restore ? "restore" : "apply_" + SelectedWin10BlePreset.Id,
                "流程未完成；类型=" + ex.GetType().Name + "；HRESULT=0x" + ex.HResult.ToString("X8") +
                (ex is System.ComponentModel.Win32Exception native ? "；Win32Error=" + native.NativeErrorCode : "") +
                "；不能据此认定参数已生效或达到 125Hz。");
            throw;
        }
    }

    private async Task RunBleTurboConfiguratorCoreAsync(
        bool restore, CancellationToken cancellationToken, bool experimentalConsent)
    {
        BlePresetDefinition preset = SelectedWin10BlePreset;
        if (!restore)
        {
            await RefreshEnvironmentInfoAsync();
        }
        ConnectionPlan plan = EnvironmentRouteInspector.Current.Plan;
        IReadOnlyList<string> configurationArguments = TurboConsentText.Arguments(restore, plan, preset, experimentalConsent);
        // A global trial can be authorized without assigning this BLE link to
        // a default radio. The elevated tool chooses no restart target unless
        // it can identify one unique present USB adapter.
        if (!restore)
            AppendLog("[BLE_TURBO_CONFIG] route=" + plan.Route + " experimental=" +
                TurboConsentText.IsExperimental(plan) + " adapter_count=" +
                (environmentSnapshot?.Adapters.Count.ToString() ?? "unknown") +
                " global_write_explicitly_authorized=true link_adapter_ownership_unproven=true");
        await StopAsync();
        // This explicit, confirmed operation will restart the adapter. Release
        // all owned BLE sessions first so stale owners cannot block reconnect.
        foreach (Pro2ControllerSlot slot in pro2Slots)
            await slot.InputSource.StopAsync();
        string executable = Environment.ProcessPath ?? "";
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new InvalidOperationException("无法定位当前程序，不能启动管理员配置进程。 ");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (string argument in configurationArguments) startInfo.ArgumentList.Add(argument);

        AppendLog("[BLE_TURBO_CONFIG] action=" + (restore ? "restore_stack" : "apply_stack_" + preset.Id) +
                  " elevation=requested");
        DateTimeOffset configurationRequestedAt = DateTimeOffset.UtcNow;
        RecordWin10ConfigurationAttempt(restore ? "restore" : "apply_" + preset.Id, "正在申请管理员配置进程；尚未确认写入或重启完成。");
        using Process? process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("管理员配置进程没有启动。 ");
        }
        await process.WaitForExitAsync(cancellationToken);
        RecordWin10ConfigurationAttempt("child_exit", "exit=" + process.ExitCode + "；进程成功退出不等于实际输入提速。");
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                (restore ? "恢复" : "启用") + "操作未完成；管理员工具 exit=" + process.ExitCode + "。 ");
        }

        AppendLog("[BLE_TURBO_CONFIG] action=" + (restore ? "restore_stack" : "apply_stack_" + preset.Id) +
                  " result=success");
        EnvironmentRouteInspector.Refresh();
        OnPropertyChanged(nameof(CompatibilityTitle));
        OnPropertyChanged(nameof(CompatibilityGuidance));
        OnPropertyChanged(nameof(CanConfigureWin10Stack));
        await RefreshEnvironmentInfoAsync();
        RefreshLowRateDiagnosis();
        ConfigOperationResult completion = ConfigOperationEvidence.Read(
            restore ? "restore" : "apply", restore ? "original" : preset.Id, configurationRequestedAt);
        bool restartPending = !completion.Known || !completion.RestartSucceeded;
        RecordWin10ConfigurationAttempt("completion", completion.Code + "；结果已核实=" + completion.Known +
            "；适配器重启已核实=" + completion.RestartSucceeded + "；真实 FD2 仍需实测。");
        AppendLog("[BLE_TURBO_CONFIG] completion=" + completion.Code + " known=" + completion.Known +
            " restart_verified=" + completion.RestartSucceeded + " automatic_retest_allowed=" + (!restartPending));
        Status = restore ? "蓝牙参数已恢复；如热重启未成功，请正常重启 Windows。"
            : restartPending ? "Win10 " + preset.Title + " 参数已保存，但适配器重启未完成或结果未核实；请查看日志，正常重启 Windows 后实测。"
            : "Win10 " + preset.Title + " 参数已保存；正在自动重连并测真实 FD2，达到 60Hz 可游玩。";
        if (!restore && !restartPending)
        {
            AppendLog("[BLE_TURBO_CONFIG] reconnect_and_measure=true preset=" + preset.Id + " write_success_is_not_rate_success=true");
            await EnterGameAsync(cancellationToken);
        }
    }

    private async Task DumpControllerEnumerationAsync(CancellationToken cancellationToken)
    {
        var client = new OutputBridgeClient(Host, ParsePort());
        IReadOnlyList<string> lines =
            await ControllerTopologyInspector.CaptureSnapshotAsync(
                client,
                sessionLog.FilePath,
                cancellationToken);
        foreach (string line in lines)
        {
            AppendLog(line);
        }
        Status = "设备枚举诊断已写入日志。重点看 [VIIPER_DUMP]、[USBIP_PORT]、[PNP_HID]。";
    }

    private async Task CleanupStaleVirtualDevicesAsync(CancellationToken cancellationToken)
    {
        if (!ViewModelPolicies.IsLoopbackHost(Host))
        {
            throw new InvalidOperationException("清理残留虚拟设备只允许本地 VIIPER（localhost/127.0.0.1），避免误删远端 USBIP bus。");
        }

        await StopAutoReconnectAsync();
        await StopSessionAsync(updateStatus: false);
        var client = new OutputBridgeClient(Host, ParsePort());
        IReadOnlyList<string> lines =
            await ControllerTopologyInspector.PurgeStaleVirtualDevicesAsync(
                client,
                cancellationToken);
        foreach (string line in lines)
        {
            AppendLog(line);
        }
        Status = "虚拟设备已按 USBIP detach → VIIPER remove 顺序清理。Steam 内存中的旧 If_Hid 需使用“刷新 Steam 缓存”完成一次性收尾。";
    }

    private async Task RefreshSteamControllerCacheAsync(CancellationToken cancellationToken)
    {
        SteamCacheVerdict before = SteamHidCacheInspector.InspectRecentIfHid(
            ActiveProfile.ExpectedVid,
            ActiveProfile.ExpectedPid,
            TimeSpan.FromMinutes(30));
        AppendLog("[STEAM_CACHE_REFRESH] preflight steam_running=" + before.SteamRunning.ToString().ToLowerInvariant() +
                  " stale_if_hid=" + before.PotentialStaleIfHid.ToString().ToLowerInvariant() +
                  " detail=" + before.Detail);
        if (!before.SteamRunning)
        {
            Status = "Steam 当前没有运行，不存在进程内控制器缓存需要刷新。";
            return;
        }

        ViiperDeviceProfile restoreProfile = activeMode.HasValue
            ? ViewModelPolicies.ProfileFor(activeMode.Value)
            : SelectedProfile;
        bool restorePrimaryAutoReconnect = AutoReconnectEnabled && !ViewModelPolicies.IsMultiSlotMode(restoreProfile.Mode);
        bool restoreSlotAutoReconnect =
            ViewModelPolicies.IsMultiSlotMode(restoreProfile.Mode) &&
            (AutoReconnectEnabled || pro2Slots.Any(slot => slot.AutoReconnectEnabled));

        Status = "正在正常拔出虚拟手柄并请求 Steam 退出，程序不会强制结束 Steam 进程。";
        await StopAutoReconnectAsync();
        await StopSessionAsync(updateStatus: false);
        await CleanupVirtualDeviceResidueAsync(
            "steam_controller_cache_refresh",
            cancellationToken,
            includePnpDump: false);

        SteamRestartOutcome refresh =
            await SteamHidCacheInspector.RefreshSteamProcessAsync(cancellationToken);
        AppendLog("[STEAM_CACHE_REFRESH] success=" + refresh.Success.ToString().ToLowerInvariant() +
                  " steam_was_running=" + refresh.SteamWasRunning.ToString().ToLowerInvariant() +
                  " detail=" + refresh.Detail +
                  " exe=\"" + (refresh.SteamExePath ?? "") + "\"");
        if (!refresh.Success)
        {
            await RestoreAfterSteamCacheRefreshAsync(
                restoreProfile,
                restorePrimaryAutoReconnect,
                restoreSlotAutoReconnect,
                cancellationToken);
            Status = "Steam 没有在 20 秒内正常退出，程序未强制结束它；当前虚拟手柄已恢复。请先退出正在运行的 Steam 游戏，再重试刷新。";
            return;
        }

        steamGhostNoticeSent = false;
        await Task.Delay(1500, cancellationToken);
        await RestoreAfterSteamCacheRefreshAsync(
            restoreProfile,
            restorePrimaryAutoReconnect,
            restoreSlotAutoReconnect,
            cancellationToken);

        Status = restoreProfile.Label + " 已在新的 Steam 进程中恢复，旧 If_Hid/SDL 槽位不再复用。";
    }

    private async Task RestoreAfterSteamCacheRefreshAsync(
        ViiperDeviceProfile restoreProfile,
        bool restorePrimaryAutoReconnect,
        bool restoreSlotAutoReconnect,
        CancellationToken cancellationToken)
    {
        await EnsureViiperReadyAsync(cancellationToken);
        await StartAsync(restoreProfile, cancellationToken);
        if (restoreSlotAutoReconnect && Running)
        {
            await StartEnabledSlotsAutoReconnectAsync(cancellationToken);
        }
        else if (restorePrimaryAutoReconnect && Running)
        {
            StartAutoReconnect();
        }
    }

    private void DiagnoseSteamControllerCache(ViiperDeviceProfile profile)
    {
        SteamCacheVerdict analysis = SteamHidCacheInspector.InspectRecentIfHid(
            profile.ExpectedVid,
            profile.ExpectedPid,
            TimeSpan.FromMinutes(30));
        AppendLog("[STEAM_CACHE] steam_running=" + analysis.SteamRunning.ToString().ToLowerInvariant() +
                  " stale_if_hid=" + analysis.PotentialStaleIfHid.ToString().ToLowerInvariant() +
                  " detail=" + analysis.Detail +
                  " controller_log=\"" + (analysis.ControllerLogPath ?? "") + "\"");
        if (!analysis.PotentialStaleIfHid || steamGhostNoticeSent)
        {
            return;
        }

        steamGhostNoticeSent = true;
        RequestUserNotification(
            "Steam 里还留着旧手柄记录",
            "如果游戏里看到重复手柄或按键错位，完全退出 Steam 再打开即可刷新控制器列表。");
    }

    private async Task CleanupVirtualDeviceResidueAsync(
        string reason,
        CancellationToken cancellationToken,
        bool includePnpDump)
    {
        if (!ViewModelPolicies.IsLoopbackHost(Host))
        {
            AppendLog("[VIRTUAL_DEVICE_GUARD] skipped reason=" + reason + " host=" + Host);
            return;
        }

        try
        {
            AppendLog("[VIRTUAL_DEVICE_GUARD] begin reason=" + reason);
            var client = new OutputBridgeClient(Host, ParsePort());
            IReadOnlyList<string> lines =
                await ControllerTopologyInspector.PurgeStaleVirtualDevicesAsync(
                    client,
                    cancellationToken,
                    includePnpDump);
            foreach (string line in lines)
            {
                AppendLog(line);
            }

            await Task.Delay(900, cancellationToken);
            AppendLog("[VIRTUAL_DEVICE_GUARD] complete reason=" + reason + " settle_ms=900");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog("[VIRTUAL_DEVICE_GUARD] warning reason=" + reason + " " +
                      ex.GetType().Name + ": " + ViewModelPolicies.FirstLine(ex.Message));
        }
    }

    private async Task ExportDiagnosticsLogAsync(CancellationToken cancellationToken)
    {
        var client = new OutputBridgeClient(Host, ParsePort());
        IReadOnlyList<string> dump =
            await ControllerTopologyInspector.CaptureSnapshotAsync(
                client,
                sessionLog.FilePath,
                cancellationToken);
        string directory = AppPaths.LogsDirectory;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            "diagnostics_v" + SystemBluetoothDiagnostics.CurrentAppVersion.Replace('.', '_') + "_" +
            DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        var builder = new StringBuilder();
        builder.AppendLine("# Pro2+ v" + SystemBluetoothDiagnostics.CurrentAppVersion + " Win10 / Win11 input diagnostics export");
        await RefreshEnvironmentInfoAsync();
        builder.AppendLine(EnvironmentReportText);
        builder.AppendLine("firmware=" + CompactFirmware + " headset=" + CompactHeadset + " " + CompactFirmwareHint);
        builder.AppendLine("[BLE_COMPATIBILITY] " + EnvironmentRouteInspector.Current.Telemetry);
        foreach (string line in await BleEnvironmentDiagnostics.CaptureAsync(cancellationToken))
        {
            builder.AppendLine(line);
            AppendLog(line);
        }
        builder.AppendLine(sessionLog.RecentText);
        builder.AppendLine();
        foreach (string line in dump)
        {
            builder.AppendLine(line);
        }
        builder.AppendLine();
        builder.AppendLine("# UI log snapshot");
        builder.Append(LogText);
        await File.WriteAllTextAsync(
            path,
            builder.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        AppendLog("[DIAG_EXPORT] path=\"" + path + "\"");
        Status = "诊断包已导出：" + path;
    }

    private void RunAudioEndpointGuard()
    {
        if (previewOnly) return;
        foreach (string line in AudioDefaultGuard.EnsureControllerIsNotDefault())
        {
            AppendLog(line);
        }
    }

    private static string? FindLibViiperCandidate()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "libVIIPER.dll"),
            Path.Combine(baseDir, "runtime", "libVIIPER.dll"),
            Path.Combine(baseDir, "tools", "viiper", "libVIIPER.dll"),
            Path.Combine(Environment.CurrentDirectory, "tools", "viiper", "haptic-src", "dist", "libVIIPER", "libVIIPER.dll")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task StopAsync()
    {
        enterGamePending = false;
        await StopAutoReconnectAsync();
        await StopSessionAsync(updateStatus: true);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await CleanupVirtualDeviceResidueAsync(
            "manual_stop",
            cleanup.Token,
            includePnpDump: false);
    }

    private async Task StopSessionAsync(bool updateStatus)
    {
        await StopMultiSlotSessionsAsync();
        OutputPadSession? active = session;
        session = null;
        if (active != null)
        {
            await active.DisposeAsync();
        }
        Running = false;
        SetActiveMode(null);
        if (updateStatus)
        {
            Status = "已停止虚拟设备。当前角色仍保持选中，可随时重新部署。";
        }
    }

    private async Task StopMultiSlotSessionsAsync()
    {
        foreach (Pro2ControllerSlot slot in pro2Slots)
        {
            OutputPadSession? active = slot.Session;
            slot.Session = null;
            if (active != null)
            {
                try
                {
                    await active.DisposeAsync();
                }
                catch (Exception ex)
                {
                    AppendLog("[PRO2_SLOT " + slot.Index + "] session cleanup warning: " + ex.Message);
                }
            }

            slot.VirtualDeviceRunning = false;
            slot.RefreshFromSource();
        }
    }

    private async Task RecoverFaultedSessionAsync(OutputPadSession failedSession, Exception error)
    {
        if (shuttingDown)
        {
            return;
        }

        await operationGate.WaitAsync();
        Busy = true;
        try
        {
            if (!ReferenceEquals(session, failedSession))
            {
                return;
            }

            ViiperDeviceProfile restartProfile = failedSession.Profile;
            bool keepAutoReconnect = AutoReconnectEnabled;
            if (keepAutoReconnect)
            {
                await StopAutoReconnectAsync();
            }
            session = null;
            await failedSession.DisposeAsync();
            Running = false;
            SetActiveMode(null);
            Status = "VIIPER 数据流异常，正在自动恢复 " + restartProfile.Label + "...";
            AppendLog("ERROR session stream: " + error);

            try
            {
                await EnsureViiperReadyAsync(lifetimeCts.Token);
                await CleanupVirtualDeviceResidueAsync(
                    "session_recovery_" + restartProfile.Mode,
                    lifetimeCts.Token,
                    includePnpDump: false);
                await StartAsync(restartProfile, lifetimeCts.Token);
                if (Running && activeMode == restartProfile.Mode)
                {
                    Status = restartProfile.Label + " 已从 VIIPER 数据流异常中自动恢复。";
                    AppendLog("[VIIPER_RECOVERY] restarted mode=" + restartProfile.Label);
                    if (keepAutoReconnect)
                    {
                        StartAutoReconnect();
                    }
                }
                else
                {
                    Status = "VIIPER 数据流异常后自动恢复未完成，请查看日志并重试当前角色。";
                    AppendLog("[VIIPER_RECOVERY] restart did not reach running state.");
                }
            }
            catch (Exception restartError)
            {
                Status = "VIIPER 数据流异常，自动恢复失败：" + ViewModelPolicies.FirstLine(restartError.Message);
                AppendLog("ERROR session recovery: " + restartError);
            }
        }
        catch (Exception cleanupError)
        {
            Running = false;
            SetActiveMode(null);
            Status = "VIIPER 数据流异常，清理失败：" + ViewModelPolicies.FirstLine(cleanupError.Message);
            AppendLog("ERROR session cleanup: " + cleanupError);
        }
        finally
        {
            Busy = false;
            operationGate.Release();
        }
    }

    public async Task ShutdownAsync()
    {
        if (previewOnly)
        {
            lifetimeCts.Cancel();
            await sessionLog.DisposeAsync();
            return;
        }
        if (shuttingDown)
        {
            return;
        }

        shuttingDown = true;
        lifetimeCts.Cancel();
        await operationGate.WaitAsync();
        Busy = true;
        try
        {
            try
            {
                await StopAutoReconnectAsync();
            }
            catch (Exception ex)
            {
                AppendLog("[SHUTDOWN] auto reconnect cleanup warning: " + ex.Message);
            }

            try
            {
                await StopAsync();
            }
            catch (Exception ex)
            {
                AppendLog("[SHUTDOWN] virtual device cleanup warning: " + ex.Message);
            }

            try
            {
                foreach (Pro2BleInputSource source in pro2Slots
                             .Select(slot => slot.InputSource)
                             .Distinct())
                {
                    await source.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                AppendLog("[SHUTDOWN] Pro2 BLE cleanup warning: " + ex.Message);
            }

            if (viiperProcess != null)
            {
                try
                {
                    if (!viiperProcess.HasExited)
                    {
                        viiperProcess.Kill(entireProcessTree: true);
                        await viiperProcess.WaitForExitAsync();
                        AppendLog("[VIIPER_SERVER] stopped local server.");
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("[VIIPER_SERVER] stop warning: " + ex.Message);
                }
                finally
                {
                    viiperProcess.Dispose();
                    viiperProcess = null;
                    localViiperApiPort = null;
                }
            }
            AppendLog("[SHUTDOWN] complete.");
        }
        finally
        {
            Busy = false;
            operationGate.Release();
            bleOperationGate.Dispose();
            lifetimeCts.Dispose();
            await sessionLog.DisposeAsync();
        }
    }

    private static string? FindLocalViiperExe()
    {
        // A packaged build must use the server it was tested with. Looking up
        // the repository copy first made release EXEs silently run stale code.
        string? embedded = ExtractEmbeddedViiperRuntime();
        if (!string.IsNullOrWhiteSpace(embedded))
        {
            return embedded;
        }

        string relative = Path.Combine(
            "tools",
            "viiper",
            "haptic-v0.8.0",
            "viiper-haptic-pro1.exe");
        string? cursor = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(cursor); i++)
        {
            string candidate = Path.GetFullPath(Path.Combine(cursor, relative));
            if (File.Exists(candidate))
            {
                return candidate;
            }
            cursor = Directory.GetParent(cursor)?.FullName;
        }

        string cwdCandidate = Path.GetFullPath(relative);
        if (File.Exists(cwdCandidate))
        {
            return cwdCandidate;
        }

        return null;
    }

    private static string? ExtractEmbeddedViiperRuntime()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pro2Plus",
            "embedded",
            "pro2-assistant-v" + SystemBluetoothDiagnostics.CurrentAppVersion,
            "viiper",
            "haptic-v0.8.0");
        Directory.CreateDirectory(root);
        string exe = Path.Combine(root, "viiper-haptic.exe");
        string licenses = Path.Combine(root, "LICENSE.txt");

        if (!ExtractResourceIfAvailable(
                assembly,
                "Embedded.viiper.haptic.exe",
                exe))
        {
            return null;
        }
        ExtractResourceIfAvailable(
            assembly,
            "Embedded.viiper.haptic.license",
            licenses);
        return exe;
    }

    private static bool ExtractResourceIfAvailable(Assembly assembly, string resourceName, string destination)
    {
        using Stream? source = assembly.GetManifestResourceStream(resourceName);
        if (source == null)
        {
            return false;
        }

        byte[] embeddedHash = SHA256.HashData(source);
        source.Position = 0;
        if (File.Exists(destination))
        {
            using FileStream existing = File.OpenRead(destination);
            byte[] existingHash = SHA256.HashData(existing);
            if (CryptographicOperations.FixedTimeEquals(embeddedHash, existingHash))
            {
                return true;
            }
        }

        string temp = destination + ".tmp";
        using (FileStream output = File.Create(temp))
        {
            source.CopyTo(output);
        }
        File.Move(temp, destination, overwrite: true);
        return true;
    }

    private int ParsePort()
    {
        if (!int.TryParse(Port, out int parsed) || parsed is < 1 or > 65535)
        {
            throw new InvalidOperationException("VIIPER Port 必须是 1 到 65535 之间的整数。");
        }

        return parsed;
    }

    private async Task RunExclusiveAsync(
        string operation,
        Func<CancellationToken, Task> action,
        bool isStartupAutomationOperation = false)
    {
        if (shuttingDown)
        {
            return;
        }

        if (!await operationGate.WaitAsync(0))
        {
            Status = "已有操作正在进行，请等待当前步骤完成。";
            AppendLog("[BUSY] rejected operation=" + operation);
            return;
        }

        Busy = true;
        try
        {
            if (!isStartupAutomationOperation)
            {
                await CancelStartupAutomationForManualOperationAsync(operation);
            }
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
            currentOperationCts = operationCts;
            await action(operationCts.Token);
        }
        catch (OperationCanceledException) when (
            lifetimeCts.IsCancellationRequested || currentOperationCts?.IsCancellationRequested == true)
        {
            Status = "已取消当前连接操作。";
            AppendLog("[CANCELLED] operation=" + operation);
        }
        catch (Exception ex)
        {
            Status = operation + "失败：" + ViewModelPolicies.FirstLine(ex.Message);
            AppendLog("ERROR operation " + operation + ": " + ex);
        }
        finally
        {
            currentOperationCts = null;
            Busy = false;
            operationGate.Release();
        }
    }

    private void DisposeExitedViiperProcess()
    {
        if (viiperProcess is not { HasExited: true })
        {
            return;
        }

        AppendLog("[VIIPER_SERVER] previous process exited code=" + viiperProcess.ExitCode);
        viiperProcess.Dispose();
        viiperProcess = null;
        localViiperApiPort = null;
    }

    private static bool IsLoopbackTcpPortAvailable(int port, out string detail)
    {
        if (!ViewModelPolicies.IsValidTcpPort(port))
        {
            detail = "invalid_port";
            return false;
        }

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            detail = "free";
            return true;
        }
        catch (SocketException ex)
        {
            detail = "busy socket_error=" + ex.SocketErrorCode + " message=\"" + ViewModelPolicies.SanitizeForLog(ex.Message) + "\"";
            return false;
        }
        catch (Exception ex)
        {
            detail = "probe_error=" + ex.GetType().Name + " message=\"" + ViewModelPolicies.SanitizeForLog(ex.Message) + "\"";
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private async Task<string?> TryPingViiperOnPortAsync(
        int apiPort,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await PingCoreAsync(apiPort, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadTailText(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "";
            }

            return string.Join(
                Environment.NewLine,
                File.ReadLines(path)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .TakeLast(maxLines));
        }
        catch (Exception ex)
        {
            return "read_tail_failed " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    private sealed record ViiperStartupFailure(
        bool PortConflict,
        string Summary,
        string UserMessage);

    private sealed class ProcessOutputTail
    {
        private readonly int maxLines;
        private readonly Queue<string> lines = new();
        private readonly object gate = new();

        public ProcessOutputTail(int maxLines)
        {
            this.maxLines = Math.Max(4, maxLines);
        }

        public void Attach(Process process)
        {
            process.OutputDataReceived += (_, e) => Add("stdout", e.Data);
            process.ErrorDataReceived += (_, e) => Add("stderr", e.Data);
            try
            {
                process.BeginOutputReadLine();
            }
            catch (Exception ex)
            {
                Add("stdout_capture", ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Add("stderr_capture", ex.GetType().Name + ": " + ex.Message);
            }
        }

        public string Snapshot()
        {
            lock (gate)
            {
                return string.Join(Environment.NewLine, lines);
            }
        }

        private void Add(string stream, string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (gate)
            {
                lines.Enqueue(stream + ": " + line);
                while (lines.Count > maxLines)
                {
                    lines.Dequeue();
                }
            }
        }
    }

    private ViiperDeviceProfile SelectedProfile => ViewModelPolicies.ProfileFor(selectedMode);
    private ViiperDeviceProfile ActiveProfile => ViewModelPolicies.ProfileFor(activeMode ?? selectedMode);
    private OutputRateOption SelectedPushRateOption =>
        OutputRateOption.FromLabel(selectedPushRateLabel);
    private VirtualBackendOption SelectedBackendOption =>
        VirtualBackendOption.FromLabel(selectedBackendLabel);
    private StickProcessingOption SelectedStickProcessingOption =>
        StickProcessingOption.FromLabel(selectedStickProcessingLabel);
    private Ps5OutputImuTuning SelectedPs5OutputImuTuning =>
        Ps5OutputImuTuning.Default;

    private Pro2ControllerSlot? SlotFromParameter(object? parameter)
    {
        if (parameter is Pro2ControllerSlot slot)
        {
            return slot;
        }

        if (parameter is int index)
        {
            return pro2Slots.FirstOrDefault(candidate => candidate.Index == index);
        }

        if (parameter is string text &&
            int.TryParse(text, out int parsed))
        {
            return pro2Slots.FirstOrDefault(candidate => candidate.Index == parsed);
        }

        return null;
    }

    private void SelectMode(ViiperVirtualMode mode)
    {
        if (ViewModelPolicies.IsMultiSlotMode(mode) && pro2Slots[0].SelectedMode != mode)
            pro2Slots[0].SelectedMode = mode;
        if (selectedMode == mode)
        {
            return;
        }

        selectedMode = mode;
        userSettings.SelectedModeKey = ViewModelPolicies.ModeKey(mode);
        SaveUserSettings("[MODE_SELECT]");
        OnPropertyChanged(nameof(IsDualSenseSelected));
        OnPropertyChanged(nameof(IsPro1Selected));
        OnPropertyChanged(nameof(IsDualSenseProfessionalImuSelected));
        OnPropertyChanged(nameof(IsPro2Selected));
        OnPropertyChanged(nameof(IsXboxSelected));
        OnPropertyChanged(nameof(IsKeyMouseSelected));
        OnPropertyChanged(nameof(IsXboxProfessionalImuSelected));
        OnPropertyChanged(nameof(SelectedModeLabel));
        OnPropertyChanged(nameof(SelectedHeroName));
        OnPropertyChanged(nameof(SelectedModeSubtitle));
        OnPropertyChanged(nameof(ModeHeadline));
        OnPropertyChanged(nameof(FooterModeText));
        RefreshMappingRuntime();
    }

    private void SetActiveMode(ViiperVirtualMode? mode)
    {
        if (!HasPs5VirtualOutput && mode is not (ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.DualSenseProfessionalImuTest))
            StopBuiltInAudioGuard();
        activeMode = mode;
        OnPropertyChanged(nameof(IsDualSenseActive));
        OnPropertyChanged(nameof(IsPro1Active));
        OnPropertyChanged(nameof(IsDualSenseProfessionalImuActive));
        OnPropertyChanged(nameof(IsPro2Active));
        OnPropertyChanged(nameof(IsXboxActive));
        OnPropertyChanged(nameof(IsXboxProfessionalImuActive));
        OnPropertyChanged(nameof(ModeHeadline));
        OnPropertyChanged(nameof(FooterModeText));
    }

    private void RaiseConnectionStateChanged()
    {
        OnPropertyChanged(nameof(IsInputConnected));
        OnPropertyChanged(nameof(BleButtonText));
        OnPropertyChanged(nameof(BleStateText));
        OnPropertyChanged(nameof(CanManualBleControl));
        RefreshCompactPresentation();
    }

    private void ClearLog()
    {
        lock (logGate) log.Clear();
        OnPropertyChanged(nameof(LogText));
    }

    private void AppendLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var persisted = new StringBuilder();
        foreach (string line in text.Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            string prefix = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] ";
            persisted.Append(prefix).AppendLine(line);

            // 连接相关行同时进「连接诊断日志」：用户回传时不用在整份界面日志里翻。
            if (ViewModelPolicies.IsConnectionLogLine(line)) ConnectionDiag.Raw(line);
        }
        lock (logGate)
        {
            log.Append(persisted);
            if (log.Length > MaxUiLogCharacters)
            {
                log.Remove(0, log.Length - TrimmedUiLogCharacters);
                log.Insert(0, "[UI LOG TRIMMED]\r\n");
            }
            logDirty = true;
        }
        sessionLog.Write(persisted.ToString());
    }

    /// <summary>只把连接链路相关的行送进专项诊断日志，避免每次 UI 刷新都刷屏。</summary>
    private void SaveUserSettings(string logPrefix)
    {
        try
        {
            if (!previewOnly) userSettings.Save();
        }
        catch (Exception ex)
        {
            AppendLog(logPrefix + " settings save warning: " + ex.Message);
        }
    }

    private static string ExplainStartFailure(Exception ex)
    {
        string first = ViewModelPolicies.FirstLine(ex.Message);
        if (first.Contains("usbip", StringComparison.OrdinalIgnoreCase) &&
            first.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "VIIPER 找不到 usbip.exe。请点击“安装/修复 usbip-win2”，使用 EXE 内置的正式安装器，完成后重新启动本地 VIIPER。" +
                   UsbipPortableInstallHint;
        }

        if (first.Contains("attach", StringComparison.OrdinalIgnoreCase) ||
            first.Contains("device handler", StringComparison.OrdinalIgnoreCase))
        {
            return "USBIP 虚拟设备挂载失败。若刚安装/修复 USBIP，请先重启 Windows；重启后仍失败再点击“安装/修复 USBIP”。原始错误：" + first;
        }

        if (first.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            first.Contains("连接被拒绝", StringComparison.OrdinalIgnoreCase))
        {
            return "VIIPER 服务未响应。程序会自动启动内置 VIIPER；若仍失败，请展开系统控制台查看 VIIPER 日志。原始错误：" + first;
        }

        return first;
    }

    private void RefreshRuntimeReadiness()
    {
        UsbipRuntime? usbip = FindConfiguredUsbip();
        UpdateUsbipLocation(usbip);
        if (usbip != null)
        {
            RuntimeReadinessText = "USBIP 程序已发现，启动角色时会自动检查内核驱动；VIIPER 已内置，无需另行安装。";
            return;
        }

        UsbipInstaller? installer = UsbipRuntimeLocator.FindBundledInstaller();
        RuntimeReadinessText = installer != null
            ? "未找到 USBIP 程序。已安装可在设置中选择 usbip.exe；首次使用可运行内置安装器。"
            : "未找到 USBIP 程序及内置安装器。已安装可手动选择 usbip.exe，否则请获取完整 EXE。";
    }

    private async Task RefreshRuntimeReadinessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            UsbipRuntime? usbip = FindConfiguredUsbip();
            UpdateUsbipLocation(usbip);
            if (usbip == null)
            {
                UsbipInstaller? installer = UsbipRuntimeLocator.FindBundledInstaller();
                RuntimeReadinessText = installer != null
                    ? "VIIPER 已内置；未找到 usbip.exe。已安装可在此选择文件；未安装可使用内置安装器。"
                    : "VIIPER 已内置；未找到 usbip.exe 或内置安装器。已安装可在此选择文件。";
                AppendLog("[RUNTIME_DIAG] viiper=embedded usbip=executable_not_found installation_state=unknown configured_exe=\"" +
                    userSettings.UsbipExecutablePath + "\" installer=" + (installer != null ? "embedded" : "missing"));
                return;
            }

            RuntimeReadinessText = "正在验证 USBIP 内核驱动；VIIPER 已内置，无需另行安装...";
            UsbipProbeResult probe = await UsbipRuntimeLocator.ProbeAsync(usbip, cancellationToken);
            if (probe.Ready)
            {
                RuntimeReadinessText = "运行环境已就绪：VIIPER 已内置，USBIP 内核驱动可用。无需额外下载或安装 VIIPER。";
                AppendLog("[RUNTIME_DIAG] viiper=embedded usbip=ready source=" + usbip.Source + " exe=\"" + usbip.ExePath + "\"");
                return;
            }

            RuntimeReadinessText = "已找到 USBIP 程序，但运行 / 驱动检测未通过。请查看日志；若安装器提示需要重启，请先重启 Windows。";
            AppendLog("[RUNTIME_DIAG] viiper=embedded usbip=executable_found_probe_failed source=" + usbip.Source +
                " exe=\"" + usbip.ExePath + "\" detail=" + probe.Detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeReadinessText = "VIIPER 已内置；USBIP 状态检测失败：" + ViewModelPolicies.FirstLine(ex.Message);
            AppendLog("[RUNTIME_DIAG] failed " + ex.Message);
        }
    }

    public event Action<string, string>? UserNotificationRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


