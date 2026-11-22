using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pro2Plus.Mapping;

namespace Pro2Plus;

public sealed class OutputPadSession : IAsyncDisposable
{
    private const ulong PerformanceLogIntervalFrames = 1000;
    private string feedbackSource = "等待游戏反馈";
    public string FeedbackSource => Volatile.Read(ref feedbackSource);
    private string nativeHidDiagnostic = "";
    public string NativeHidDiagnostic => Volatile.Read(ref nativeHidDiagnostic);
    private long feedbackQueued;
    public long FeedbackQueued => Interlocked.Read(ref feedbackQueued);
    private readonly OutputBridgeClient client;
    private readonly ViiperDeviceProfile profile;
    private readonly IProgress<string> progress;
    private readonly IProgress<Exception>? faultProgress;
    // 插线抢过蓝牙时连接层会换掉 InputSession，运行中的会话必须热替换，
    // 否则会一直读已 Stop 的旧会话（回报率 0、摇杆冻结、旧状态外吐）。
    private volatile IGamepadInputSource? inputSource;
    private volatile IGamepadOutputSink? outputSink;
    private readonly RearKeyAutomation rearButtonMapper = new();
    private readonly MotionSampleResolver motionValidityPolicy = new();
    private RearKeyConfiguration rearButtonConfiguration = new();
    private int rearCancelRequested;
    private readonly IProgress<ProfessionalImuUiSnapshot>? professionalImuUiProgress;
    private readonly CancellationTokenSource cts = new();
    private readonly SemaphoreSlim streamRecoveryGate = new(1, 1);
    private readonly ImuResampleMode gyroMode;
    private readonly GyroAxisInversion gyroAxisInversion;
    private readonly Ps5ImuMapping ps5ImuMapping;
    private readonly Ps5OutputImuTuning ps5OutputImuTuning;
    private readonly ProfessionalImuOptions professionalImuOptions;
    private readonly ProfessionalHidAuditController professionalHidAuditController;
    /// <summary>最后一次推送给虚拟手柄的输出快照（映射后的按键 + 摇杆位置，用于映射页实时试验）。</summary>
    // 输入报文复用缓冲：每帧写进同一块内存再交给流，热路径不产生托管分配。
    // 只有在 WriteAsync 完成后才进入下一帧，socket 写入已经把数据交给内核。
    private readonly byte[] inputFrame = new byte[PadFrameEncoder.MaxFrameSize];

    // 分配速率观测：每秒统计一次本线程的托管分配量，写进速率日志。
    private long allocSampleBytes = GC.GetAllocatedBytesForCurrentThread();
    private long allocSampleTicks = Stopwatch.GetTimestamp();
    private readonly object outputSnapshotGate = new();
    private OutputPadSnapshot outputSnapshot = new();
    private long outputButtonsAtTicks;
    public OutputPadSnapshot ReadOutputSnapshot()
    {
        lock (outputSnapshotGate) return outputSnapshot;
    }
    /// <summary>距最后输出帧的毫秒数，超过阈值说明会话已停或无输入。</summary>
    public double OutputButtonsAgeMs
    {
        get
        {
            long ticks = Volatile.Read(ref outputButtonsAtTicks);
            return ticks == 0 ? double.MaxValue : Stopwatch.GetElapsedTime(ticks).TotalMilliseconds;
        }
    }

    private readonly GyroStick? gyroStick;
    private readonly GyroConfig gyroConfig;
    private readonly string gyroNote = "";
    private bool gyroToggleWasPressed;
    private bool gyroLastOn = true;
    private bool gyroZeroAnnounced;
    private string lastGyroBiasKey = "";
    /// <summary>切换模式后的中立输出保护期起点。</summary>
    private long sessionStartTicks;
    private long gyroDiagMs;
    private uint diagLastRaw;
    private long diagLastMs;
    private long pktDiagMs;
    private GamepadButtons diagLastButtons;
    private readonly object streamSync = new();
    private BridgeDeviceStream? stream;
    private BridgeDeviceInfo? device;
    // 键鼠模式额外持有的设备：键盘复用 device/stream，鼠标单独一份。
    private BridgeDeviceStream? keyMouseMouseStream;
    private BridgeDeviceInfo? keyMouseMouseDevice;
    private readonly KeyMouseMapper keyMouseMapper;
    private bool keyMouseNeutralSent;
    private ProfessionalImuRuntime? professionalImuRuntime;
    private uint busId;
    private bool createdBus;
    private Task? inputTask;
    private Task? feedbackTask;
    private int faultReported;
    private const int StreamRecoveryAttempts = 5;
    private ulong usbipAttachCount;
    private ulong UsbipDetachCount { get; set; }
    private ulong DeviceRecreateCount { get; set; }
    private ulong viiperStreamDisconnectCount;
    private ulong viiperStreamRecoveryCount;
    private ulong SteamDeviceHashChangeCount { get; set; }
    private string lastViiperError = "";
    private string lastUsbipLifecycleEvent = "none";

    public OutputPadSession(
        OutputBridgeClient client,
        ViiperDeviceProfile profile,
        IProgress<string> progress,
        IGamepadInputSource? inputSource = null,
        IGamepadOutputSink? outputSink = null,
        IProgress<Exception>? faultProgress = null,
        ImuResampleMode gyroMode = ImuResampleMode.Hold250Hz,
        GyroAxisInversion gyroAxisInversion = default,
        Ps5ImuMapping? ps5ImuMapping = null,
        Ps5OutputImuTuning? ps5OutputImuTuning = null,
        ProfessionalImuOptions? professionalImuOptions = null,
        ProfessionalHidAuditController? professionalHidAuditController = null,
        IProgress<ProfessionalImuUiSnapshot>? professionalImuUiProgress = null,
        RearKeyConfiguration? rearButtons = null)
    {
        this.client = client;
        this.profile = profile;
        this.progress = progress;
        this.inputSource = inputSource;
        this.outputSink = outputSink ?? inputSource as IGamepadOutputSink;
        this.faultProgress = faultProgress;
        this.gyroMode = gyroMode;
        this.gyroAxisInversion = gyroAxisInversion;
        this.ps5ImuMapping = ps5ImuMapping ?? Ps5ImuMappingOption.Default.Mapping;
        this.ps5OutputImuTuning = (ps5OutputImuTuning ?? Ps5OutputImuTuning.Default).Normalize();
        this.professionalImuOptions = professionalImuOptions ?? ProfessionalImuOptions.Default;
        this.professionalHidAuditController = professionalHidAuditController ?? new ProfessionalHidAuditController();
        this.professionalImuUiProgress = professionalImuUiProgress;
        rearButtonConfiguration = (rearButtons ?? new()).Normalize();

        // 陀螺仪调教：参数从 exe 同目录的 gyro.json 读，缺省用内置定版参数。
        gyroConfig = GyroConfig.LoadOrDefault(out string gyroNote);
        gyroStick = gyroConfig.Gyro.Enabled ? new GyroStick(gyroConfig) : null;
        gyroLastOn = gyroConfig.Gyro.StartEnabled;
        keyMouseMapper = new KeyMouseMapper(gyroConfig.KeyMouse);
        this.gyroNote = gyroNote;
        lastGyroBiasKey = Pro2GyroBiasStore.CurrentKey;
        Pro2GyroBiasStore.DeviceKeyChanged += OnGyroBiasDeviceChanged;
    }

    private void OnGyroBiasDeviceChanged(string key)
    {
        lastGyroBiasKey = key;
        gyroStick?.ReloadSavedBias();
        gyroZeroAnnounced = false;
        Pro2GyroBiasProfile saved = Pro2GyroBiasStore.Load();
        progress.Report("[GYRO_BIAS] 链路切换，已加载 " + key + " 的零偏档案：" + saved.Summary);
    }

    /// <summary>
    /// 界面改了键鼠映射直接调用：立即生效，有线/无线都走同一个映射器。
    /// </summary>
    public void ApplyKeyMouse(KeyMouseSection section)
    {
        if (section == null) return;
        gyroConfig.KeyMouse = section;
        keyMouseMapper.ApplyOptions(section);
    }

    /// <summary>
    /// 界面改了陀螺灵敏度倍率直接调用：手柄模式和键鼠模式同步生效。
    /// 实际满转速度 = full_speed_dps / 倍率，两种模式的输出都按它归一化。
    /// </summary>
    public void ApplySensitivity(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) return;
        gyroConfig.Gyro.SensitivityScale = Math.Clamp(scale, 0.25, 4.0);
    }

    /// <summary>
    /// 设置页「陀螺仪映射高级设置」改动后即时推给运行中的会话。
    /// gyroStick 持有同一个 gyroConfig 引用，改字段即生效，无需重建会话。
    /// </summary>
    public void ApplyGyroTuning(GyroSection gyro)
    {
        if (gyro == null) return;
        GyroSection target = gyroConfig.Gyro;
        target.FullSpeedDps = gyro.FullSpeedDps;
        target.ScaleYaw = gyro.ScaleYaw;
        target.ScalePitch = gyro.ScalePitch;
        target.ScalePitchDown = gyro.ScalePitchDown;
        target.RightStickDeadzone = gyro.RightStickDeadzone;
        target.GyroToggleButton = gyro.GyroToggleButton;
        target.SensitivityScale = gyro.SensitivityScale;
    }

    public void SetRearButtons(RearKeyConfiguration configuration)
    {
        var next = configuration.Normalize();
        var previous = Interlocked.Exchange(ref rearButtonConfiguration, next);
        if (!RearKeyAutomation.ProfilesEquivalent(previous.ForMode(profile.Mode), next.ForMode(profile.Mode)))
            CancelRearButtons();
    }
    public void CancelRearButtons() => Interlocked.Or(ref rearCancelRequested, 1);

    public ViiperDeviceProfile Profile => profile;

    public bool HasProfessionalImuRuntime => professionalImuRuntime != null;

    public string StartGyroBiasCalibration()
    {
        string result = professionalImuRuntime?.StartGyroBiasCalibration() ??
                        "Professional IMU runtime is not active.";
        ReportProfessionalSnapshot();
        return result;
    }

    public string ResetGyroBias()
    {
        string result = professionalImuRuntime?.ResetGyroBias() ??
                        "Professional IMU runtime is not active.";
        ReportProfessionalSnapshot();
        return result;
    }

    public string ResetProfessionalIntegral()
    {
        string result = professionalImuRuntime?.ResetIntegral() ??
                        "Professional IMU runtime is not active.";
        ReportProfessionalSnapshot();
        return result;
    }

    public string SetProfessionalGyroInversion(bool pitch, bool yaw, bool roll)
    {
        string result = professionalImuRuntime?.SetOutputGyroInversion(pitch, yaw, roll) ??
                        "Professional IMU runtime is not active; inversion will apply on next Professional IMU session.";
        ReportProfessionalSnapshot();
        return result;
    }

    public string StartNinetyDegreeTest(ProfessionalImuTestAxis axis)
    {
        string result = professionalImuRuntime?.StartNinetyDegreeTest(axis) ??
                        "Professional IMU runtime is not active.";
        ReportProfessionalSnapshot();
        return result;
    }

    public string StopNinetyDegreeTest()
    {
        string result = professionalImuRuntime?.StopNinetyDegreeTest() ??
                        "Professional IMU runtime is not active.";
        ReportProfessionalSnapshot();
        return result;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        sessionStartTicks = Stopwatch.GetTimestamp();
        motionValidityPolicy.Reset();
        string ping = await client.PingAsync(cancellationToken);
        progress.Report("[VIIPER] ping " + ping);

        busId = await client.BusCreateAsync(cancellationToken);
        createdBus = true;
        progress.Report("[VIIPER] created dedicated bus " + busId);

        // 键鼠模式不创建游戏手柄，改为单个 bus 上挂 HID 键盘 + HID 鼠标。
        if (profile.Mode == ViiperVirtualMode.KeyMouse)
        {
            await StartKeyMouseAsync(cancellationToken);
            return;
        }

        var deviceSpecific = profile.DeviceSpecificArguments();
        device = await client.AddDeviceAsync(
            busId,
            profile.DeviceType,
            deviceSpecific,
            cancellationToken);
        usbipAttachCount = 1;
        lastUsbipLifecycleEvent = "initial_auto_attach";
        progress.Report($"[VIIPER] added {profile.Label} device bus={device.BusId} dev={device.DevId} vid={device.Vid} pid={device.Pid}");
        if (deviceSpecific.Count > 0)
        {
            progress.Report("[VIIPER_DEVICE_SPECIFIC] profile=\"" + profile.Label +
                            "\" serial_number=\"" + profile.DeviceSpecificSerialNumber + "\"" +
                            " source_paced=" + profile.SourcePaced +
                            " usb_motion_policy=" + (profile.Mode == ViiperVirtualMode.Pro2 && profile.SourcePaced
                                ? "4ms_zero_order_hold_source_repeats_counted" : "source_paced_or_explicit_legacy") +
                            " physical_input_rate_scope=fd2_notify_only");
        }
        bool identityOk = profile.MatchesIdentity(device);
        progress.Report(
            "[VIIPER_IDENTITY] profile=\"" + profile.Label +
            "\" expected_type=" + profile.DeviceType +
            " expected_vid=0x" + profile.ExpectedVid +
            " expected_pid=0x" + profile.ExpectedPid +
            " actual_type=" + device.Type +
            " actual_vid=" + device.Vid +
            " actual_pid=" + device.Pid +
            " result=" + (identityOk ? "ok" : "mismatch"));
        if (!identityOk)
        {
            throw new InvalidOperationException(
                "VIIPER returned a virtual device identity that does not match the selected mode. " +
                "profile=" + profile.Label +
                " expected=" + profile.DeviceType + "/0x" + profile.ExpectedVid + ":0x" + profile.ExpectedPid +
                " actual=" + device.Type + "/" + device.Vid + ":" + device.Pid +
                ". Please use 清理残留虚拟设备 and retry.");
        }

        UsbipAttachRepairOutcome singletonResult =
            await ControllerTopologyInspector.EnsureSingleAttachmentAsync(
                device,
                client.Host,
                client.Port,
                cancellationToken);
        foreach (string line in singletonResult.Lines)
        {
            progress.Report(line);
        }
        UsbipDetachCount += (ulong)singletonResult.DetachedPortCount;
        usbipAttachCount += (ulong)singletonResult.AttachCount;
        if (singletonResult.DetachedPortCount > 0 || singletonResult.AttachCount > 0)
        {
            lastUsbipLifecycleEvent = "singleton_repair";
        }
        if (singletonResult.FinalPortCount != 1)
        {
            throw new InvalidOperationException(
                "USBIP virtual-controller attachment validation failed. Expected exactly one port, actual=" +
                singletonResult.FinalPortCount + ". Please retry after using 清理残留虚拟设备.");
        }

        SetStream(await client.OpenStreamAsync(device.BusId, device.DevId, cancellationToken));
        if (inputSource is IGamepadSessionInputSource sessionInputSource)
        {
            int discardedFrames = sessionInputSource.PrepareForConsumerSession();
            progress.Report(
                "[INPUT_SESSION] sequential backlog reset before consumer start" +
                " discarded_frames=" + discardedFrames +
                " policy=no_cross_session_replay");
        }
        progress.Report(inputSource is { IsRunning: true }
            ? "[VIIPER] stream connected; feeding Pro2 BLE input."
            : "[VIIPER] stream connected; feeding neutral input until Pro2 BLE source is connected.");
        progress.Report(outputSink is { IsOutputReady: true }
            ? "[VIIPER] Pro2 output writeback is enabled for rumble."
            : "[VIIPER] Pro2 output writeback is not ready; host rumble will be logged only.");
        if (profile.IsPs5Family)
            progress.Report("[PS5_MOTION_VALIDITY] invalid_accel=session_last_valid_gravity" +
                " no_cache_gravity=0,8192,0 invalid_gyro=zero cache=session_local" +
                " cache_reset=start_stop source_state_mutation=false");
        if (gyroStick != null)
        {
            progress.Report("[GYRO] " + gyroNote);
            progress.Report("[GYRO] 陀螺仪调教已就绪：full_speed=" + gyroStick.Config.Gyro.FullSpeedDps +
                "dps scale_yaw=" + gyroStick.Config.Gyro.ScaleYaw +
                " scale_pitch=" + gyroStick.Config.Gyro.ScalePitch +
                " deadzone=" + gyroStick.Config.Gyro.RightStickDeadzone +
                " 开关键=" + gyroStick.Config.Gyro.GyroToggleButton +
                " 当前=" + (gyroStick.GyroOn ? "开" : "关"));
        }
        if (profile.IsProfessionalImuTest && professionalImuOptions.Enabled)
        {
            professionalImuRuntime = new ProfessionalImuRuntime(professionalImuOptions, profile.Mode.ToString(), progress);
            progress.Report("[PRO_IMU] enabled " + professionalImuOptions.TelemetryValue +
                            " csv=\"" + professionalImuRuntime.CsvPath + "\"");
            professionalImuUiProgress?.Report(professionalImuRuntime.GetSnapshot());
        }

        inputTask = Task.Run(() => RunLoopAsync("input", InputLoopAsync, cts.Token));
        feedbackTask = Task.Run(() => RunLoopAsync("feedback", FeedbackLoopAsync, cts.Token));
        if(profile.UsesDualSenseHaptics)
        {
            CancellationToken diagnosticToken=cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1200,diagnosticToken).ConfigureAwait(false);
                    string diagnostic=Ps5NativeHidDiagnostics.Capture();
                    if(diagnosticToken.IsCancellationRequested) return;
                    Volatile.Write(ref nativeHidDiagnostic,diagnostic);
                    progress.Report("[PS5_NATIVE_HID_CAPS] " + diagnostic.Replace('\n',' '));
                }
                catch(OperationCanceledException) { }
            });
        }
    }

    private void ReportProfessionalSnapshot()
    {
        if (professionalImuRuntime != null)
        {
            professionalImuUiProgress?.Report(professionalImuRuntime.GetSnapshot());
        }
    }

    public async Task StopAsync()
    {
        cts.Cancel();
        // 键鼠模式先发一帧「全键松开 + 鼠标不动」，否则切模式或退出后按键会卡在按下状态。
        if (profile.Mode == ViiperVirtualMode.KeyMouse)
        {
            await SendKeyMouseNeutralAsync();
        }
        BridgeDeviceStream? activeStream = ExchangeStream(null);
        if (activeStream != null)
        {
            // 手柄模式也要先推几帧中立再销毁：否则最后一帧的按键/摇杆状态
            // 会留在主机侧，切换模式时表现为乱按（键鼠模式已有单独处理）。
            if (profile.Mode != ViiperVirtualMode.KeyMouse)
            {
                try
                {
                    ReadOnlyMemory<byte> neutral = MapInputPacket(null);
                    for (int i = 0; i < 3; i++)
                    {
                        await activeStream.WriteAsync(neutral, CancellationToken.None).ConfigureAwait(false);
                        await Task.Delay(16).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // 设备可能已经不在了，中立帧尽力而为。
                }
            }
            await activeStream.DisposeAsync();
        }

        await DrainTaskAsync(inputTask);
        await DrainTaskAsync(feedbackTask);
        rearButtonMapper.Reset();
        motionValidityPolicy.Reset();
        // Stop the physical actuators only after the feedback loop has drained,
        // so no late game frame can overwrite this neutral during mode switch.
        if (outputSink?.IsOutputReady == true)
        {
            var stop = Pro2OutputPacketMapper.BuildOrdinaryPacket(0, 0, "session-stop");
            outputSink.TryWriteOutputReport(stop.Report, out _);
        }
        professionalImuRuntime?.Dispose();
        professionalImuRuntime = null;

        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (device != null)
        {
            try
            {
                IReadOnlyList<string> detachLines =
                    await ControllerTopologyInspector.DetachOrderlyAsync(
                        device,
                        client.Host,
                        client.Port,
                        cleanup.Token);
                foreach (string line in detachLines)
                {
                    progress.Report(line);
                }
                if (detachLines.Any(line => line.Contains("confirmed_absent=true", StringComparison.Ordinal)))
                {
                    UsbipDetachCount++;
                    lastUsbipLifecycleEvent = "session_stop_orderly_usbip_detach";
                }
            }
            catch (Exception ex)
            {
                progress.Report("[USBIP_ORDERLY_DETACH] warning=" + ex.Message);
            }

            try
            {
                await client.RemoveDeviceAsync(device.BusId, device.DevId, cleanup.Token);
                if (lastUsbipLifecycleEvent != "session_stop_orderly_usbip_detach")
                {
                    lastUsbipLifecycleEvent = "session_stop_remove_device_fallback";
                }
                progress.Report("[VIIPER] removed device " + device.DevId);
            }
            catch (Exception ex)
            {
                progress.Report("[VIIPER] device cleanup warning: " + ex.Message);
            }
            device = null;
        }

        // 键鼠模式第二个设备（鼠标）：先关流再移除。
        if (keyMouseMouseStream != null)
        {
            try
            {
                await keyMouseMouseStream.DisposeAsync();
            }
            catch (Exception ex)
            {
                progress.Report("[VIIPER] mouse stream cleanup warning: " + ex.Message);
            }
            keyMouseMouseStream = null;
        }
        if (keyMouseMouseDevice != null)
        {
            try
            {
                await client.RemoveDeviceAsync(keyMouseMouseDevice.BusId, keyMouseMouseDevice.DevId, cleanup.Token);
                progress.Report("[VIIPER] removed mouse device " + keyMouseMouseDevice.DevId);
            }
            catch (Exception ex)
            {
                progress.Report("[VIIPER] mouse device cleanup warning: " + ex.Message);
            }
            keyMouseMouseDevice = null;
        }

        if (createdBus)
        {
            try
            {
                await client.RemoveBusAsync(busId, cleanup.Token);
                progress.Report("[VIIPER] removed bus " + busId);
            }
            catch (Exception ex)
            {
                progress.Report("[VIIPER] bus cleanup warning: " + ex.Message);
            }
        }
    }

    // ================= 键鼠模式 =================

    private async Task StartKeyMouseAsync(CancellationToken cancellationToken)
    {
        // VIIPER 对「已创建但没有客户端连接」的设备 5 秒后自动回收，
        // 所以每加一个设备必须立刻开流，不能先建两个再一起连。
        BridgeDeviceInfo keyboard = await client.AddDeviceAsync(busId, "keyboard", null, cancellationToken);
        BridgeDeviceStream keyboardStream =
            await client.OpenStreamAsync(keyboard.BusId, keyboard.DevId, cancellationToken);
        device = keyboard;
        SetStream(keyboardStream);
        progress.Report("[KEYMOUSE] keyboard bus=" + keyboard.BusId + " dev=" + keyboard.DevId +
                        " vid=" + keyboard.Vid + " pid=" + keyboard.Pid);

        BridgeDeviceInfo mouse = await client.AddDeviceAsync(busId, "mouse", null, cancellationToken);
        keyMouseMouseStream = await client.OpenStreamAsync(mouse.BusId, mouse.DevId, cancellationToken);
        keyMouseMouseDevice = mouse;
        progress.Report("[KEYMOUSE] mouse    bus=" + mouse.BusId + " dev=" + mouse.DevId +
                        " vid=" + mouse.Vid + " pid=" + mouse.Pid);


        KeyMouseSection options = keyMouseMapper.Options;
        progress.Report("[KEYMOUSE] 映射就绪：" +
                        "左摇杆=WASD · 右摇杆=鼠标转身 · 陀螺=鼠标瞄准 · " +
                        "A=Ctrl B=空格 X=1/2/3(单/双/长按) Y=R/F(轻/长按) · " +
                        "L1=C R1=E L1+R1=X R3=Q ＋=Esc · " +
                        "十字上=G 十字下=4 十字右=B · 左扳机=鼠标右键 右扳机=鼠标左键 · 左背键=Shift 右背键=Ctrl");
        progress.Report("[KEYMOUSE] 速度：摇杆 " + options.StickTurnSpeed.ToString("F0") +
                        " 计数/秒 · 陀螺 " + options.GyroAimSpeed.ToString("F0") +
                        " 计数/秒 · Y轴 x" + options.MouseYScale.ToString("F2") +
                        " · 鼠标报告 " + options.MouseReportMs + "ms");
        if (gyroStick != null)
        {
            progress.Report("[GYRO] " + gyroNote + " 陀螺当前=" + (gyroStick.GyroOn ? "开" : "关"));
        }

        keyMouseMapper.Reset();
        keyMouseNeutralSent = false;
        inputTask = Task.Run(() => RunLoopAsync("keymouse", KeyMouseLoopAsync, cts.Token));
    }

    /// <summary>
    /// 键鼠主循环。与手柄循环不同：没有虚拟手柄帧率概念，只有「键盘有变化才发」和
    /// 「鼠标按固定间隔发位移」。即使没有新的 FD2 帧，也继续用最后一帧状态积分，
    /// 这样摇杆保持推住时鼠标会持续移动。
    /// </summary>
    private async Task KeyMouseLoopAsync(CancellationToken cancellationToken)
    {
        BridgeDeviceStream? keyboard = stream;
        BridgeDeviceStream? mouse = keyMouseMouseStream;
        if (keyboard == null || mouse == null)
        {
            progress.Report("[KEYMOUSE] 设备流未就绪，键鼠循环退出。");
            return;
        }

        var clock = Stopwatch.StartNew();
        // 默认 Task.Delay 粒度约 15.6ms（≈64Hz），鼠标会发顿；
        // 提到 1ms 定时器精度后循环约 250~600Hz，鼠标报告间隔才有意义。
        using TimerResolutionScope timerResolution = TimerResolutionScope.Begin(true);
        GamepadState? lastState = null;
        byte lastButtons = 0;
        ulong frames = 0;
        long lastLogMs = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            GamepadState? fresh = null;
            if (inputSource is IGamepadRealtimeInputSource realtime)
            {
                if (realtime.TryGetNewest(out GamepadState newest, out _, out _)) fresh = newest;
            }
            else if (inputSource is IGamepadSequentialInputSource sequential)
            {
                if (sequential.TryGetNext(out GamepadState next, out _)) fresh = next;
            }
            else if (inputSource != null && inputSource.TryGetLatest(out GamepadState latest, out _))
            {
                fresh = latest;
            }

            if (fresh != null) lastState = fresh;

            if (lastState != null)
            {
                long nowTicks = Stopwatch.GetTimestamp();
                long nowMs = (long)clock.Elapsed.TotalMilliseconds;

                // 物理右摇杆必须在陀螺写入之前取；Apply 返回的是叠加后的克隆。
                double physicalRx = KeyMouseMapper.AxisToUnit(lastState.Rx);
                double physicalRy = KeyMouseMapper.AxisToUnit(lastState.Ry);
                double physicalLx = KeyMouseMapper.AxisToUnit(lastState.Lx);
                double physicalLy = KeyMouseMapper.AxisToUnit(lastState.Ly);

                bool toggleEdge = false;
                if (gyroStick != null)
                {
                    bool togglePressed = gyroStick.IsTogglePressed(lastState);
                    toggleEdge = togglePressed && !gyroToggleWasPressed;
                    gyroToggleWasPressed = togglePressed;
                }

                GamepadState processed = gyroStick?.Apply(lastState, nowTicks, toggleEdge) ?? lastState;
                double gyroX = gyroStick?.LastGyroUnitX ?? 0.0;
                double gyroY = gyroStick?.LastGyroUnitY ?? 0.0;

                keyMouseMapper.Update(processed, gyroX, gyroY, physicalRx, physicalRy, nowMs);

                if (keyMouseMapper.ConsumeKeyboardChanged())
                {
                    await keyboard.WriteAsync(keyMouseMapper.BuildKeyboardReport(), cancellationToken)
                        .ConfigureAwait(false);
                }

                (int dx, int dy) = keyMouseMapper.TakeMouseDelta(nowMs, out bool due);
                // 滚轮只在真的要发报告时取，否则没到间隔就被清掉，切枪会丢格。
                int wheel = due ? keyMouseMapper.TakeWheel() : 0;
                if (due)
                {
                    // 到点就发，哪怕位移是 0：固定节奏的包比「有位移才发」稳得多，
                    // 低速转视角时不会出现「停一下再跳一格」。
                    await mouse.WriteAsync(
                            KeyMouseMapper.BuildMouseReport(keyMouseMapper.MouseButtons, dx, dy, wheel),
                            cancellationToken)
                        .ConfigureAwait(false);
                    lastButtons = keyMouseMapper.MouseButtons;
                }

                frames++;
                if (nowMs - lastLogMs >= 5000)
                {
                    lastLogMs = nowMs;
                    Connection.LinkDebug.Write(
                        "KBM physicalR=(" + physicalRx.ToString("F5") + "," +
                        physicalRy.ToString("F5") + ")" +
                        " physicalL=(" + physicalLx.ToString("F5") + "," +
                        physicalLy.ToString("F5") + ")" +
                        " gyroU=(" + gyroX.ToString("F5") + "," + gyroY.ToString("F5") + ")" +
                        " stateR=(" + processed.Rx + "," + processed.Ry + ")" +
                        " stateL=(" + processed.Lx + "," + processed.Ly + ")" +
                        " " + keyMouseMapper.MouseTelemetry +
                        " mode=" + (gyroStick?.GyroOn == true ? "gyro-on" : "gyro-off"));
                    progress.Report("[KEYMOUSE] 帧=" + frames +
                                    " 陀螺=" + (gyroStick?.GyroOn == true ? "开" : "关") +
                                    " 摇杆L=(" + physicalLx.ToString("F2") + "," + physicalLy.ToString("F2") + ")" +
                                    " 摇杆R=(" + physicalRx.ToString("F2") + "," + physicalRy.ToString("F2") + ")" +
                                    " 陀螺U=(" + gyroX.ToString("F2") + "," + gyroY.ToString("F2") + ")" +
                                    " 键数=" + keyMouseMapper.PressedKeyCount +
                                    " 原值=(" + lastState.Lx + "," + lastState.Ly + "," +
                                    lastState.Rx + "," + lastState.Ry + ")");
                }
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendKeyMouseNeutralAsync()
    {
        if (keyMouseNeutralSent) return;
        keyMouseNeutralSent = true;
        try
        {
            BridgeDeviceStream? keyboard = stream;
            if (keyboard != null)
            {
                // 键盘中性帧：[modifiers=0][keyCount=0]，变长包只有 2 字节。
                await keyboard.WriteAsync(new byte[2], CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // 尽力而为：设备可能已经断开。
        }

        try
        {
            if (keyMouseMouseStream != null)
            {
                await keyMouseMouseStream
                    .WriteAsync(KeyMouseMapper.BuildMouseReport(0, 0, 0, 0), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    private async Task InputLoopAsync(CancellationToken cancellationToken)
    {
        bool sourceLatestScheduling =
            profile.SourcePaced &&
            inputSource is IGamepadRealtimeInputSource;
        IGamepadSignaledInputSource? signaledSource = sourceLatestScheduling
            ? inputSource as IGamepadSignaledInputSource : null;
        using TimerResolutionScope timerResolution =
            TimerResolutionScope.Begin(signaledSource == null);
        TimeSpan schedulerInterval = sourceLatestScheduling
            ? TimeSpan.FromMilliseconds(1)
            : profile.SendInterval;
        using var timer = signaledSource == null
            ? new HighResolutionPeriodicTimer(schedulerInterval) : null;
        var rateWatch = Stopwatch.StartNew();
        double pushTargetHz = 1.0 / profile.SendInterval.TotalSeconds;
        double effectivePushTargetHz = pushTargetHz;
        long lastVirtualWriteTicks = 0;
        ulong frames = 0;
        ulong lastRateFrames = 0;
        var inputRateWindow = new UpdateRateWindow(
            inputSource != null && inputSource.TryGetLatest(out var rateBaseline, out _)
                ? rateBaseline.Updates : 0);
        uint lastSeenUpdates = 0;
        long lastRateTicks = 0;
        long lastLoopTicks = 0;
        uint? lastPushedUpdate = null;
        uint lastMotionUpdate = 0;
        GamepadState? filteredMotionState = null;
        ulong statePushCount = 0;
        ulong stateReuseCount = 0;
        ulong currentSameStatePushes = 0;
        ulong sameStatePushMax = 0;
        ulong age0To20 = 0;
        ulong age20To33 = 0;
        ulong age33To50 = 0;
        ulong age50To100 = 0;
        ulong ageOver100 = 0;
        ulong realtimeSupersededTotal = 0;
        ulong realtimeFreshWrites = 0;
        bool sourceNeutralPublished = false;
        bool sourceGapHoldLogged = false;
        GamepadState? pendingRearRelease = null;
        async Task<bool> FlushRearReleaseAsync(BridgeDeviceStream current)
        {
            if (pendingRearRelease == null) return true;
            try
            {
                // An exceptional control-release frame, not a new measured FD2
                // input. Do not add it to frames/Hz/IMU telemetry counters.
                await current.WriteAsync(MapInputPacket(pendingRearRelease), cancellationToken).ConfigureAwait(false);
                pendingRearRelease = null;
                progress.Report("[REAR_SAFETY_RELEASE] mapped_controls_only=true physical_keys_preserved=true counted_as_fd2=false");
                return true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Retain the release until the reopened stream accepts it.
                if (await RecoverStreamAsync("rear_release", current, ex, cancellationToken).ConfigureAwait(false)) return false;
                throw;
            }
        }
        var intervalSourceAgeSamples = new List<double>((int)PerformanceLogIntervalFrames);
        var intervalLoopGapSamples = new List<double>((int)PerformanceLogIntervalFrames);
        var intervalWriteSamplesMs = new List<double>((int)PerformanceLogIntervalFrames);
        double intervalWriteMsSum = 0;
        double intervalWriteMaxMs = 0;
        ulong intervalWriteSamples = 0;
        ulong intervalWriteOver2Ms = 0;
        ulong intervalWriteOver8Ms = 0;
        double intervalLoopGapMaxMs = 0;
        ulong intervalLoopGapOver8Ms = 0;
        ulong intervalLoopGapOver16Ms = 0;
        double intervalSourceAgeMaxMs = 0;
        string lastSource = "";
        bool inputRateDegraded = false;
        string lastProfessionalImuTelemetry = "";
        string lastProfessionalHidAuditTelemetry = "";
        long lastProfessionalHidAuditLogTicks = 0;
        var imuTelemetry = new VirtualImuTelemetryStats();
        string timerLine =
            "[VIIPER_TIMER] requested_ms=" + (signaledSource == null ? "1" : "0") +
            " active=" + timerResolution.IsActive +
            " result=" + timerResolution.Result +
            " backend=" + (timer?.Backend ?? "notify_signal") +
            " scheduler=" + (signaledSource != null ? "source_notify_edges" :
                sourceLatestScheduling ? "source_latest_1ms" : "fixed_latest") +
            " scheduler_interval_ms=" + (signaledSource != null ? "event" :
                schedulerInterval.TotalMilliseconds.ToString("F1")) +
            " push_target_hz=" + (sourceLatestScheduling ? "ble_source" : pushTargetHz.ToString("F1")) +
            " report_rate_mode=" + professionalImuOptions.OutputReportRateMode +
            " gyro_mode=" + OutputFrameDiagnostics.GyroModeLabel(gyroMode) +
            " gyro_axis_inv=" + gyroAxisInversion.TelemetryValue;
        if (profile.IsPs5Family)
        {
            timerLine +=
                " ps5_imu_map=" + ps5ImuMapping.TelemetryValue +
                " ps5_output_imu=" + ps5OutputImuTuning.TelemetryValue;
        }
        progress.Report(timerLine);
        while (!cancellationToken.IsCancellationRequested &&
               (signaledSource != null
                   ? await WaitForSourceAsync(signaledSource, cancellationToken).ConfigureAwait(false)
                   : timer!.WaitForNextTick(cancellationToken)))
        {
            long loopTicks = Stopwatch.GetTimestamp();
            if (lastLoopTicks != 0)
            {
                double loopGapMs = OutputFrameDiagnostics.TicksToMilliseconds(loopTicks - lastLoopTicks);
                intervalLoopGapMaxMs = Math.Max(intervalLoopGapMaxMs, loopGapMs);
                if (loopGapMs >= 8)
                {
                    intervalLoopGapOver8Ms++;
                }
                if (loopGapMs >= 16)
                {
                    intervalLoopGapOver16Ms++;
                }
                intervalLoopGapSamples.Add(loopGapMs);
            }
            lastLoopTicks = loopTicks;

            double sourceRateHz = inputSource is IGamepadInputRateSource rateSource
                ? rateSource.CurrentParsedRateHz
                : 0;
            effectivePushTargetHz = sourceLatestScheduling
                ? sourceRateHz
                : professionalImuOptions.AutoReduceVirtualReportRate &&
                professionalImuOptions.OutputReportRateMode == OutputReportRateMode.AutoByBleSourceRate
                    ? VirtualReportRateGovernor.SelectAutoRateHz(sourceRateHz, pushTargetHz)
                    : pushTargetHz;

            BridgeDeviceStream? current = GetStream();
            if (current == null)
            {
                // The feedback loop may temporarily clear the stream while
                // reopening it. Do not silently terminate the input consumer.
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                continue;
            }
            int rearCancel = Interlocked.Exchange(ref rearCancelRequested, 0);
            if (rearCancel != 0 &&
                rearButtonMapper.TryCancelOutput(out var requestedRelease, forceRelease: (rearCancel & 2) != 0))
                pendingRearRelease = requestedRelease;
            if (!await FlushRearReleaseAsync(current).ConfigureAwait(false)) continue;

            ReadOnlyMemory<byte> packet;
            string source;
            GamepadState? packetSourceState = null;
            ProfessionalImuFrame professionalFrame = default;
            GamepadState state = GamepadState.Neutral();
            TimeSpan age = TimeSpan.MaxValue;
            bool hasInput;
            if (sourceLatestScheduling &&
                inputSource is IGamepadRealtimeInputSource realtimeLatest)
            {
                hasInput = realtimeLatest.TryGetNewest(
                    out state,
                    out age,
                    out int supersededCount);
                realtimeSupersededTotal += (ulong)Math.Max(0, supersededCount);
                if (!hasInput &&
                    lastVirtualWriteTicks == 0 &&
                    inputSource != null &&
                    inputSource.TryGetLatest(out GamepadState initialLatest, out TimeSpan initialAge))
                {
                    state = initialLatest;
                    age = initialAge;
                    hasInput = true;
                }
                if (!hasInput && lastVirtualWriteTicks != 0)
                {
                    bool sourceIsRunning = inputSource?.IsRunning == true;
                    if (rearButtonMapper.HasPendingAction && (!sourceIsRunning ||
                        (inputSource!.TryGetLatest(out _, out TimeSpan rearInputAge) && rearInputAge > TimeSpan.FromMilliseconds(100))))
                    {
                        if (rearButtonMapper.TryCancelOutput(out var staleRearRelease)) pendingRearRelease = staleRearRelease;
                        progress.Report("[REAR_SAFETY_CANCEL] reason=input_gap_or_disconnect rearm=release_then_press");
                        if (!await FlushRearReleaseAsync(current).ConfigureAwait(false)) continue;
                    }
                    if (!OutputFrameDiagnostics.ShouldPublishDisconnectNeutral(
                            sourceIsRunning,
                            sourceNeutralPublished))
                    {
                        if (sourceIsRunning &&
                            !sourceGapHoldLogged &&
                            inputSource!.TryGetLatest(out _, out TimeSpan latestAge) &&
                            latestAge > TimeSpan.FromMilliseconds(100))
                        {
                            progress.Report(
                                "[INPUT_GAP_HOLD] source_age_ms=" +
                                latestAge.TotalMilliseconds.ToString("F1") +
                                " controls=hold_latest neutral_injected=false");
                            sourceGapHoldLogged = true;
                        }
                        continue;
                    }
                }

                if (hasInput)
                {
                    realtimeFreshWrites++;
                    sourceNeutralPublished = false;
                    sourceGapHoldLogged = false;
                }
                else
                {
                    sourceNeutralPublished = true;
                    sourceGapHoldLogged = false;
                    progress.Report(
                        "[INPUT_DISCONNECT_NEUTRAL] source_running=false sent_once=true");
                }
            }
            else
            {
                if (inputSource is IGamepadRealtimeInputSource latestSource &&
                    latestSource.TryGetNewest(
                        out state,
                        out age,
                        out int supersededCount))
                {
                    hasInput = true;
                    realtimeSupersededTotal += (ulong)Math.Max(0, supersededCount);
                }
                else
                {
                    hasInput = inputSource != null &&
                        inputSource.TryGetLatest(out state, out age);
                }
            }
            if (hasInput)
            {
                lastSeenUpdates = state.Updates;
                bool reusedState = lastPushedUpdate.HasValue &&
                                   state.Updates == lastPushedUpdate.Value;
                statePushCount++;
                if (reusedState)
                {
                    stateReuseCount++;
                    currentSameStatePushes++;
                }
                else
                {
                    currentSameStatePushes = 1;
                    lastPushedUpdate = state.Updates;
                }
                sameStatePushMax = Math.Max(sameStatePushMax, currentSameStatePushes);

                if (age < TimeSpan.FromMinutes(1))
                {
                    double ageMs = age.TotalMilliseconds;
                    intervalSourceAgeMaxMs = Math.Max(intervalSourceAgeMaxMs, ageMs);
                    intervalSourceAgeSamples.Add(ageMs);
                    OutputFrameDiagnostics.CountAgeBucket(
                        ageMs,
                        ref age0To20,
                        ref age20To33,
                        ref age33To50,
                        ref age50To100,
                        ref ageOver100);
                }
                GamepadState continuous =
                    FrameFreshnessPolicy.Resolve(state, age, out source);
                if (profile.IsProfessionalImuTest && professionalImuRuntime != null)
                {
                    professionalFrame = professionalImuRuntime.Process(state, age.TotalMilliseconds);
                    lastProfessionalImuTelemetry = professionalFrame.Telemetry;
                    if (professionalFrame.UiSnapshot != null)
                    {
                        professionalImuUiProgress?.Report(professionalFrame.UiSnapshot);
                    }
                }
                continuous = ApplyGyroMode(
                    continuous,
                    state.Updates,
                    effectivePushTargetHz,
                    ref lastMotionUpdate,
                    ref filteredMotionState);
                if (gyroStick != null)
                {
                    bool togglePressed = gyroStick.IsTogglePressed(continuous);
                    bool toggleEdge = togglePressed && !gyroToggleWasPressed;
                    gyroToggleWasPressed = togglePressed;

                    continuous = gyroStick.Apply(continuous, Stopwatch.GetTimestamp(), toggleEdge);

                    if (gyroStick.GyroOn != gyroLastOn)
                    {
                        gyroLastOn = gyroStick.GyroOn;
                        progress.Report(gyroStick.GyroOn
                            ? "[GYRO] 右摇杆陀螺映射开启（C键）；原生物体感不受影响"
                            : "[GYRO] 右摇杆陀螺映射关闭（C键，回到物理右摇杆）；Pro1/PS5 原生陀螺仪仍持续输出");
                    }
                    if (gyroStick.ZeroReady && !gyroZeroAnnounced)
                    {
                        gyroZeroAnnounced = true;
                        progress.Report("[GYRO] 零偏" +
                            (gyroStick.BiasFromSaved ? "已从设置页加载，无需重新校准" : "校准完成并已保存") +
                            " source=" + gyroStick.BiasSource +
                            " yaw=" + gyroStick.BiasYawDeg.ToString("0.00") + " pitch=" +
                            gyroStick.BiasPitchDeg.ToString("0.00") + " deg/s");
                    }
                    // 陀螺健康度遥测：5 秒一行，判断「没数据」还是「有数据但输出为 0」。
                    if (Environment.TickCount64 - gyroDiagMs >= 5000)
                    {
                        gyroDiagMs = Environment.TickCount64;
                        // 双保险：事件漏掉时也能在下一轮诊断切换到新手柄的档案。
                        if (!string.Equals(lastGyroBiasKey, Pro2GyroBiasStore.CurrentKey, StringComparison.Ordinal))
                            OnGyroBiasDeviceChanged(Pro2GyroBiasStore.CurrentKey);
                        // 两条陀螺链路彼此独立，遥测分开标注：
                        //   A 原生 = state.GyroX/Y/Z → Pro1/PS5 原生 IMU，始终在线，不受 C 键影响；
                        //   B 右摇杆 = SwitchRawImuSamples → GyroStick → state.Rx/Ry，由 C 键开关。
                        // 落到 link-debug，方便直接对比有线/蓝牙的陀螺→右摇杆残差。
                    Connection.LinkDebug.Write(
                        "gyro on=" + gyroStick.GyroOn +
                        " zero=" + gyroStick.ZeroReady +
                        " raw_yaw=" + gyroStick.LastYaw.ToString("F1") +
                        " raw_pitch=" + gyroStick.LastPitch.ToString("F1") +
                        " out=(" + gyroStick.LastYawOut.ToString("F2") + "," +
                        gyroStick.LastPitchOut.ToString("F2") + ")" +
                        " stick=(" + gyroStick.LastRx.ToString("F2") + "," +
                        gyroStick.LastRy.ToString("F2") + ")" +
                        " imu=" + state.SwitchRawImuSamples.Length +
                        " src=" + source);
                    progress.Report("[GYRO] A原生=" + (state.GyroValid ? "在线" : "无数据") +
                            " A零偏灵敏度=" + Pro2GyroBiasStore.ActiveGyroSensitivity.ToString("0.00") +
                            " A三轴=(" + state.GyroX + "," + state.GyroY + "," + state.GyroZ + ")" +
                            " | B右摇杆(C键)=" + (gyroStick.GyroOn ? "开" : "关") +
                            " B零点=" + (gyroStick.ZeroReady ? "就绪" : "学习中") +
                            " B原始yaw=" + gyroStick.LastYaw.ToString("F1") +
                            " B原始pitch=" + gyroStick.LastPitch.ToString("F1") +
                            " B滤波后=(" + gyroStick.LastYawOut.ToString("F2") + "," +
                            gyroStick.LastPitchOut.ToString("F2") + ")" +
                            " B右摇杆=(" + gyroStick.LastRx.ToString("F2") + "," +
                            gyroStick.LastRy.ToString("F2") + ")" +
                            " IMU样本=" + state.SwitchRawImuSamples.Length +
                            " 加速raw=(" + state.AccelX + "," + state.AccelY + "," + state.AccelZ + ")" +
                            " 重力=(" + gyroStick.GravityVector.X.ToString("F2") + "," +
                            gyroStick.GravityVector.Y.ToString("F2") + "," +
                            gyroStick.GravityVector.Z.ToString("F2") + ")" +
                            " 来源=" + source);
                    }
                }
                GamepadState physicalControls = continuous;
                if (inputSource?.IsRunning == true && age <= TimeSpan.FromMilliseconds(100))
                    continuous = rearButtonMapper.Apply(continuous, profile.Mode,
                        Volatile.Read(ref rearButtonConfiguration), loopTicks);
                else
                    rearButtonMapper.Reset();

                // 用户映射：物理键 → 最终输出键。放在背键改写之后、出包之前，
                // 这样模型图读到的 continuous 就是「最终输出」，高亮天然跟着映射走。
                if (profile.Mode != ViiperVirtualMode.KeyMouse)
                    continuous = PadMappingEngine.Apply(continuous);

                packetSourceState = continuous;
                // 映射页手柄模型图读取「推送给虚拟手柄的最终输出」来实时点亮按键。
                lock (outputSnapshotGate)
                {
                    outputSnapshot = new OutputPadSnapshot(
                        (ulong)continuous.Buttons, continuous.Lx, continuous.Ly, continuous.Rx, continuous.Ry,
                        continuous.L2, continuous.R2);
                }
                Volatile.Write(ref outputButtonsAtTicks, Stopwatch.GetTimestamp());
                // 诊断：按键位变化时打一行，用于定位 L3 等按键是「没解析出来」
                // 还是「解析出来了但没发出去」。只在组合变化时写，不刷屏。
                if ((continuous.RawButtons != diagLastRaw || continuous.Buttons != diagLastButtons)
                    && Environment.TickCount64 - diagLastMs >= 120)
                {
                    diagLastRaw = continuous.RawButtons;
                    diagLastButtons = continuous.Buttons;
                    diagLastMs = Environment.TickCount64;
                    progress.Report("[BTN] 原始=0x" + continuous.RawButtons.ToString("X8")
                        + " 内部位=0x" + ((ulong)continuous.Buttons).ToString("X")
                        + "  (L3=0x400 R3=0x800 L1=0x10 R1=0x20 背键:PL=0x80000 PR=0x40000 C=0x100000)");
                }
                packet = MapInputPacket(
                    continuous,
                    professionalFrame.DualSenseRaw,
                    ReferenceEquals(physicalControls, continuous) ? professionalFrame.XboxState :
                        RearKeyAutomation.MergeProfessionalControls(continuous, professionalFrame.XboxState));

                // 模式切换保护：新虚拟设备出现的前 350ms 强制输出中立。
                // 旧会话最后一帧的按键/摇杆/触摸状态若直接透过去，主机会把它
                // 当成新设备的输入（实测调出过放大镜和 Steam 屏幕键盘）。
                if (Stopwatch.GetElapsedTime(sessionStartTicks).TotalMilliseconds < 350)
                {
                    packet = MapInputPacket(null);
                    source = "neutral_switch_guard";
                }

                // 诊断：按下 L3 时把真正发出去的包头字节打出来，判断是「组包丢了」还是「后端丢了」。
                if ((continuous.Buttons & GamepadButtons.LeftStick) != 0
                    && Environment.TickCount64 - pktDiagMs >= 250)
                {
                    pktDiagMs = Environment.TickCount64;
                    progress.Report("[PKT] 模式=" + profile.Mode + " 长度=" + packet.Length
                        + " 前8字节=" + OutputFrameDiagnostics.FormatHead(packet.Span)
                        + " (Xbox: L3 应在 [0:4] 出现 40 00 00 00；PS5: 应在 [4:8] 出现 00 40 00 00)");
                }
            }
            else
            {
                rearButtonMapper.Reset();
                currentSameStatePushes = 0;
                packet = MapInputPacket(null);
                source = "neutral";
            }

            if (!sourceLatestScheduling &&
                OutputFrameDiagnostics.ShouldSkipVirtualWrite(loopTicks, lastVirtualWriteTicks, effectivePushTargetHz, pushTargetHz))
            {
                continue;
            }

            if (profile.Mode == ViiperVirtualMode.DualSenseProfessionalImuTest &&
                professionalImuRuntime != null &&
                packetSourceState is { GyroValid: true, AccelValid: true } &&
                professionalFrame.DualSenseRaw is { } professionalDualSenseRaw)
            {
                ProfessionalHidAuditControlState auditControl =
                    professionalHidAuditController.Snapshot(loopTicks, out string? auditEvent);
                if (!string.IsNullOrWhiteSpace(auditEvent))
                {
                    progress.Report(auditEvent);
                }

                ProfessionalHidAuditSnapshot audit =
                    ProfessionalHidReportAuditor.ApplyAndAudit(
                        inputFrame,
                        professionalDualSenseRaw,
                        professionalFrame.OutputPhysical,
                        auditControl);
                professionalImuRuntime.CommitFinalHidAudit(audit);
                lastProfessionalHidAuditTelemetry = audit.Summary;

                bool auditMismatch = audit.Result is
                    ProfessionalHidAuditResult.MISMATCH_GYRO or
                    ProfessionalHidAuditResult.MISMATCH_ACCEL or
                    ProfessionalHidAuditResult.REPORT_TOO_SHORT or
                    ProfessionalHidAuditResult.OFFSET_UNKNOWN or
                    ProfessionalHidAuditResult.WRONG_BUILDER;
                long auditElapsedTicks = rateWatch.ElapsedTicks;
                bool auditDue =
                    auditMismatch ||
                    frames % PerformanceLogIntervalFrames == 0 ||
                    auditElapsedTicks - lastProfessionalHidAuditLogTicks >= Stopwatch.Frequency * 5L;
                if (auditDue)
                {
                    lastProfessionalHidAuditLogTicks = auditElapsedTicks;
                    progress.Report("[PRO_IMU_AUDIT] " + audit.Summary);
                    if (auditMismatch)
                    {
                        progress.Report("[PRO_IMU_AUDIT_ERROR] final HID gyro mismatch " + audit.Detail);
                    }
                    else if (audit.Result == ProfessionalHidAuditResult.OK)
                    {
                        progress.Report("[PRO_IMU_AUDIT_OK] final HID gyro matches selected output. " + audit.Summary);
                    }
                }
            }

            try
            {
                long writeStartTicks = Stopwatch.GetTimestamp();
                await current.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
                lastVirtualWriteTicks = Stopwatch.GetTimestamp();
                imuTelemetry.Record(profile, packet.Span, packetSourceState);
                double writeMs = OutputFrameDiagnostics.TicksToMilliseconds(lastVirtualWriteTicks - writeStartTicks);
                intervalWriteSamples++;
                intervalWriteMsSum += writeMs;
                intervalWriteSamplesMs.Add(writeMs);
                intervalWriteMaxMs = Math.Max(intervalWriteMaxMs, writeMs);
                if (writeMs >= 2)
                {
                    intervalWriteOver2Ms++;
                }
                if (writeMs >= 8)
                {
                    intervalWriteOver8Ms++;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (await RecoverStreamAsync("input", current, ex, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                throw;
            }
            frames++;
            long elapsedRateTicks = rateWatch.ElapsedTicks;
            bool rateDue =
                frames % PerformanceLogIntervalFrames == 0 ||
                (frames > lastRateFrames &&
                 elapsedRateTicks - lastRateTicks >= Stopwatch.Frequency * 10L);
            string sourceFamily = OutputFrameDiagnostics.SourceFamily(source);
            bool sourceChanged = sourceFamily != lastSource;
            if (rateDue || sourceChanged)
            {
                lastSource = sourceFamily;
                string rateSummary =
                    "target_hz=" + (sourceLatestScheduling ? sourceRateHz : effectivePushTargetHz).ToString("F1") +
                    " configured_target_hz=" + (sourceLatestScheduling ? "source" : pushTargetHz.ToString("F1")) +
                    " source_rate_hz=" + sourceRateHz.ToString("F1") +
                    " report_rate_mode=" + professionalImuOptions.OutputReportRateMode;
                if (rateDue)
                {
                    long nowTicks = rateWatch.ElapsedTicks;
                    double elapsedSeconds = (nowTicks - lastRateTicks) / (double)Stopwatch.Frequency;
                    double actualHz = elapsedSeconds > 0
                        ? (frames - lastRateFrames) / elapsedSeconds
                        : 0;
                    if (inputSource is IGamepadRuntimeTelemetrySink telemetrySink)
                    {
                        telemetrySink.ReportOutputPushRate(actualHz);
                    }
                    double bleHz = inputRateWindow.Measure(lastSeenUpdates, elapsedSeconds);
                    double stateReuseRatio = statePushCount == 0
                        ? 0
                        : stateReuseCount / (double)statePushCount;
                    if (SoftwareOnlyMode.IsEnabled &&
                        stateReuseRatio > SoftwareOnlyMode.MaximumRepeatedStateRatio)
                    {
                        throw new InvalidOperationException(
                            "纯软件真实输入转发校验失败（不允许补帧）：source_hz=" +
                            sourceRateHz.ToString("F1") +
                            " output_hz=" + actualHz.ToString("F1") +
                            " repeated_state_ratio=" + stateReuseRatio.ToString("F3"));
                    }
                    bool degraded = SoftwareOnlyMode.IsEnabled &&
                        (bleHz < SoftwareOnlyMode.MinimumAcceptedHz ||
                         actualHz < SoftwareOnlyMode.MinimumAcceptedHz);
                    if (degraded != inputRateDegraded)
                    {
                        inputRateDegraded = degraded;
                        progress.Report(degraded
                            ? "[VIIPER_RATE_DEGRADED] real input/output below 125Hz target; compatible gameplay keeps fresh-only output alive, no synthetic frames."
                            : "[VIIPER_RATE_RECOVERED] real input/output returned above acceptance threshold; same virtual device retained.");
                    }
                    lastRateTicks = nowTicks;
                    lastRateFrames = frames;
                    rateSummary +=
                        " actual_hz=" + actualHz.ToString("F1") +
                        " ble_hz=" + bleHz.ToString("F1") +
                        " viiper_push_hz=" + actualHz.ToString("F1") +
                        " scheduler=" + (sourceLatestScheduling ? "source_latest_1ms" : "fixed_latest") +
                        (sourceLatestScheduling
                            ? " host_poll_cap_hz="
                            : " host_hid_target_hz=") +
                        pushTargetHz.ToString("F1") +
                        " realtime_fresh_writes=" + realtimeFreshWrites +
                        " realtime_superseded_total=" + realtimeSupersededTotal +
                        " state_reuse_count=" + stateReuseCount +
                        " state_reuse_ratio=" + stateReuseRatio.ToString("F3") +
                        " same_state_push_max=" + sameStatePushMax +
                        (profile.IsPs5Family ? " " + rearButtonMapper.TouchMetrics : "") +
                        " source_age_bucket=0_20:" + age0To20 +
                        ",20_33:" + age20To33 +
                        ",33_50:" + age33To50 +
                        ",50_100:" + age50To100 +
                        ",gt100:" + ageOver100 +
                        " source_age_bucket_0_20=" + age0To20 +
                        " source_age_bucket_20_33=" + age20To33 +
                        " source_age_bucket_33_50=" + age33To50 +
                        " source_age_bucket_50_100=" + age50To100 +
                        " source_age_bucket_over100=" + ageOver100;
                    double avgWriteMs = intervalWriteSamples == 0
                        ? 0
                        : intervalWriteMsSum / intervalWriteSamples;
                    rateSummary +=
                        " loop_gap_max_ms=" + intervalLoopGapMaxMs.ToString("F2") +
                        " loop_gap_p95_ms=" + OutputFrameDiagnostics.Percentile(intervalLoopGapSamples, 95).ToString("F2") +
                        " loop_gap_p99_ms=" + OutputFrameDiagnostics.Percentile(intervalLoopGapSamples, 99).ToString("F2") +
                        " loop_gap_over8=" + intervalLoopGapOver8Ms +
                        " loop_gap_over16=" + intervalLoopGapOver16Ms +
                        " viiper_write_avg_ms=" + avgWriteMs.ToString("F3") +
                        " viiper_write_max_ms=" + intervalWriteMaxMs.ToString("F3") +
                        " viiper_write_p95_ms=" + OutputFrameDiagnostics.Percentile(intervalWriteSamplesMs, 95).ToString("F3") +
                        " viiper_write_p99_ms=" + OutputFrameDiagnostics.Percentile(intervalWriteSamplesMs, 99).ToString("F3") +
                        " viiper_write_over2=" + intervalWriteOver2Ms +
                        " viiper_write_over8=" + intervalWriteOver8Ms +
                        " source_age_max_ms=" + intervalSourceAgeMaxMs.ToString("F1") +
                        " source_age_p95_ms=" + OutputFrameDiagnostics.Percentile(intervalSourceAgeSamples, 95).ToString("F1") +
                        " source_age_p99_ms=" + OutputFrameDiagnostics.Percentile(intervalSourceAgeSamples, 99).ToString("F1") +
                        " usbip_attach_count=" + usbipAttachCount +
                        " usbip_detach_count=" + UsbipDetachCount +
                        " device_recreate_count=" + DeviceRecreateCount +
                        " viiper_stream_disconnect_count=" + viiperStreamDisconnectCount +
                        " viiper_stream_recovery_count=" + viiperStreamRecoveryCount +
                        " steam_device_hash_change_count=" + SteamDeviceHashChangeCount +
                        " last_viiper_error=\"" + SanitizeLogValue(lastViiperError) + "\"" +
                        " last_usbip_lifecycle_event=" + lastUsbipLifecycleEvent +
                        " gyro_axis_inv=" + gyroAxisInversion.TelemetryValue +
                        " loop_alloc_kb_s=" + TakeAllocRateKbPerSecond().ToString("F1");
                    if (profile.IsPs5Family)
                    {
                        string ps5ImuPipe = imuTelemetry.Ps5PipelineSummary();
                        rateSummary += " ps5_imu_map=" + ps5ImuMapping.TelemetryValue;
                        rateSummary += " " + imuTelemetry.SummaryAndMaybeReset(rateDue);
                        if (rateDue && !string.IsNullOrWhiteSpace(ps5ImuPipe))
                        {
                            progress.Report("[PS5_IMU_PIPE] " + ps5ImuPipe);
                        }
                    }
                    else if (profile.Mode == ViiperVirtualMode.Pro2)
                    {
                        rateSummary += " " + imuTelemetry.SummaryAndMaybeReset(rateDue);
                    }
                    if (profile.IsProfessionalImuTest)
                    {
                        rateSummary += " " + SanitizeLogValue(lastProfessionalImuTelemetry);
                        if (!string.IsNullOrWhiteSpace(lastProfessionalHidAuditTelemetry))
                        {
                            rateSummary += " hid_audit=\"" + SanitizeLogValue(lastProfessionalHidAuditTelemetry) + "\"";
                        }
                    }
                    intervalWriteMsSum = 0;
                    intervalWriteMaxMs = 0;
                    intervalWriteSamples = 0;
                    intervalWriteOver2Ms = 0;
                    intervalWriteOver8Ms = 0;
                    intervalLoopGapMaxMs = 0;
                    intervalLoopGapOver8Ms = 0;
                    intervalLoopGapOver16Ms = 0;
                    intervalLoopGapSamples.Clear();
                    intervalSourceAgeMaxMs = 0;
                    intervalSourceAgeSamples.Clear();
                    intervalWriteSamplesMs.Clear();
                    statePushCount = 0;
                    stateReuseCount = 0;
                    sameStatePushMax = currentSameStatePushes;
                    age0To20 = 0;
                    age20To33 = 0;
                    age33To50 = 0;
                    age50To100 = 0;
                    ageOver100 = 0;
                }

                string inputMetrics = inputSource is IGamepadInputMetricsSource metrics
                    ? " " + metrics.MetricsSummary
                    : "";
                progress.Report("[VIIPER] fed " + frames + " " + source + " " + profile.Label +
                                " frames " + rateSummary + inputMetrics);
            }
        }
    }

    private ReadOnlyMemory<byte> MapInputPacket(
        GamepadState? source,
        DualSenseImuRawSample? professionalDualSenseImu = null,
        GamepadState? professionalXboxState = null)
    {
        GamepadState? output = source;
        if (profile.IsPs5Family)
        {
            output = motionValidityPolicy.Resolve(source);
            // An independently cached/diagnostic raw sample must not bypass
            // the invalid-gyro stop or session-local gravity policy.
            if (source == null || !source.GyroValid || !source.AccelValid)
                professionalDualSenseImu = null;
        }
        int length = VirtualPadPackets.WriteFrame(
            inputFrame, profile, output, gyroAxisInversion, ps5ImuMapping, ps5OutputImuTuning,
            professionalDualSenseImu, professionalXboxState);
        return inputFrame.AsMemory(0, length);
    }

    /// <summary>本线程每秒托管分配量（KB/s）；采样间隔不足 200 ms 时返回 0，不污染日志。</summary>
    private double TakeAllocRateKbPerSecond()
    {
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long now = Stopwatch.GetTimestamp();
        long deltaBytes = bytes - allocSampleBytes;
        long deltaTicks = now - allocSampleTicks;
        allocSampleBytes = bytes;
        allocSampleTicks = now;
        if (deltaTicks <= 0)
        {
            return 0;
        }
        double seconds = (double)deltaTicks / Stopwatch.Frequency;
        return seconds < 0.2 ? 0 : deltaBytes / 1024.0 / seconds;
    }

    private GamepadState ApplyGyroMode(
        GamepadState state,
        uint sourceUpdate,
        double pushTargetHz,
        ref uint lastMotionUpdate,
        ref GamepadState? filteredMotionState)
    {
        switch (gyroMode)
        {
            case ImuResampleMode.Hold250Hz:
                lastMotionUpdate = sourceUpdate;
                return state;
            case ImuResampleMode.Source60Hz:
                if (sourceUpdate == lastMotionUpdate)
                {
                    return MotionFrameTransform.WithoutMotion(state);
                }
                lastMotionUpdate = sourceUpdate;
                return state;
            case ImuResampleMode.Scaled250Hz:
                lastMotionUpdate = sourceUpdate;
                return MotionFrameTransform.ScaleGyro(
                    state,
                    Math.Clamp(66.7 / Math.Max(pushTargetHz, 1.0), 0.25, 1.0));
            case ImuResampleMode.Filtered250Hz:
                lastMotionUpdate = sourceUpdate;
                filteredMotionState = MotionFrameTransform.FilterMotion(filteredMotionState, state);
                return MotionFrameTransform.CopyMotion(state, filteredMotionState);
            default:
                lastMotionUpdate = sourceUpdate;
                return state;
        }
    }

    private static async Task<bool> WaitForSourceAsync(
        IGamepadSignaledInputSource source, CancellationToken token)
    {
        // Idle checks release stalled rear macros. They never create fresh input
        // or generate a timer-driven repeat stream; active delivery is event driven.
        await source.WaitForInputAsync(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
        return !token.IsCancellationRequested;
    }

    private async Task FeedbackLoopAsync(CancellationToken cancellationToken)
    {
        DualSenseHapticRumbleScheduler? dualSenseScheduler =
            profile.UsesDualSenseHaptics
                ? new DualSenseHapticRumbleScheduler(QualityFeatureFlags.HapticsEnabled)
                : null;
        ulong frames = 0;
        progress.Report("[QUALITY_PIPELINE] gyro_quality=" + QualityFeatureFlags.GyroEnabled +
            " haptics_quality=" + QualityFeatureFlags.HapticsEnabled +
            " native_pcm_supported=" + profile.UsesDualSenseHaptics +
            " input_rate_path=unchanged native_pro2=payload_preserving_ordered" +
            " pcm_idle_guard=independent_120ms guard_check_max_wait_ms=40" +
            " host_sensor_enable=not_observable_in_feedback_stream");
        ulong writes = 0;
        ulong failures = 0;
        Dictionary<byte, string> lastFeedbackSummaryByKind = new();
        Dictionary<byte, ulong> feedbackFramesByKind = new();
        string lastOutputState = "";
        HashSet<string> reportedSkipReasons = new(StringComparer.Ordinal);
        Task<byte[]>? pendingRead = null;
        long lastHapticTelemetry = Stopwatch.GetTimestamp();
        void QueueFeedbackStop(string source, bool idleOnly)
        {
            Pro2OutputPacket stop = default!;
            bool needed = dualSenseScheduler != null && (idleOnly
                ? dualSenseScheduler.TryStopIdle(out stop)
                : dualSenseScheduler.TryStop(source, out stop));
            if (!needed) return;
            Volatile.Write(ref feedbackSource, "触觉音频通道 · 待信号");
            bool queued = outputSink?.IsOutputReady == true &&
                outputSink.TryWriteOutputPacket(stop, out _);
            if (queued) Interlocked.Increment(ref feedbackQueued);
            progress.Report("[HAPTIC_STOP] source=" + stop.Source + " queued=" + queued +
                " " + dualSenseScheduler!.StateTelemetry);
        }
        try
        {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Check the PCM clock on every pass, even while LED/HID traffic is
            // continuous. Unrelated host messages must not postpone PCM stop.
            QueueFeedbackStop("dualsense-hd-idle-stop", idleOnly: true);
            if (dualSenseScheduler != null &&
                Stopwatch.GetElapsedTime(lastHapticTelemetry).TotalSeconds >= 5)
            {
                lastHapticTelemetry = Stopwatch.GetTimestamp();
                progress.Report("[PS5_FEEDBACK_STATE] " + dualSenseScheduler.StateTelemetry);
            }
            BridgeDeviceStream? current = GetStream();
            if (current == null)
            {
                return;
            }

            byte[] feedback;
            try
            {
                pendingRead ??= current.ReadExactAsync(profile.FeedbackSize, cancellationToken);
                if (dualSenseScheduler != null)
                {
                    try
                    {
                        feedback = await pendingRead.WaitAsync(
                            TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Do not cancel or replace the outstanding read: otherwise
                        // the next frame could be consumed by an abandoned task.
                        QueueFeedbackStop("dualsense-hd-idle-stop", idleOnly: true);
                        continue;
                    }
                }
                else
                    feedback = await pendingRead.ConfigureAwait(false);
                pendingRead = null;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                pendingRead = null;
                QueueFeedbackStop("dualsense-feedback-stream-lost-stop", idleOnly: false);
                if (await RecoverStreamAsync("feedback", current, ex, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                throw;
            }
            frames++;
            Pro2OutputPacket packet;
            string reason;
            bool mapped;
            string feedbackSummary;
            if (dualSenseScheduler != null)
            {
                mapped = dualSenseScheduler.TryProcess(
                    feedback,
                    out packet,
                    out feedbackSummary,
                    out reason);
                Volatile.Write(ref feedbackSource,
                    dualSenseScheduler.CompatibilityMode ? "普通震动 · 游戏反馈" :
                    dualSenseScheduler.NativeOutputActive ? "原生触觉音频 · 转换中" :
                    dualSenseScheduler.HasNativeAudio ? "触觉音频通道 · 待信号" : "等待原生触觉音频");
            }
            else
            {
                feedbackSummary = VirtualPadPackets.FeedbackSummary(profile, feedback);
                mapped = Pro2OutputPacketMapper.TryMapFeedback(
                    profile,
                    feedback,
                    out packet,
                    out reason);
                if (mapped)
                    Volatile.Write(ref feedbackSource, profile.Mode switch
                    {
                        ViiperVirtualMode.Pro2 => "Pro2 原始震动",
                        ViiperVirtualMode.Pro1 => "Pro1 震动 · 转换中",
                        _ => "普通震动 · 游戏反馈"
                    });
            }
            byte feedbackKind = dualSenseScheduler != null && feedback.Length > 0
                ? feedback[0]
                : (byte)0;
            ulong kindFrames = feedbackFramesByKind.TryGetValue(feedbackKind, out ulong previousKindFrames)
                ? previousKindFrames + 1
                : 1;
            feedbackFramesByKind[feedbackKind] = kindFrames;
            if (dualSenseScheduler != null && QualityFeatureFlags.HapticsEnabled &&
                feedbackKind == (byte)DualSenseHapticFrameKind.AudioPcm && kindFrames % 1000 == 0)
                progress.Report("[HAPTIC_CONVERSION] " + dualSenseScheduler.ConversionTelemetry);
            bool summaryChanged =
                !lastFeedbackSummaryByKind.TryGetValue(feedbackKind, out string? lastFeedbackSummary) ||
                feedbackSummary != lastFeedbackSummary;
            ulong periodicLogFrames = feedbackKind == (byte)DualSenseHapticFrameKind.AudioPcm
                ? 1000UL
                : 100UL;
            if (summaryChanged || kindFrames <= 4 || kindFrames % periodicLogFrames == 0)
            {
                lastFeedbackSummaryByKind[feedbackKind] = feedbackSummary;
                progress.Report("[HOST_OUTPUT] " + feedbackSummary);
            }

            if (!mapped)
            {
                if (!string.IsNullOrWhiteSpace(reason) && reportedSkipReasons.Add(reason))
                {
                    progress.Report("[PRO2_OUTPUT] skipped: " + reason);
                }
                continue;
            }

            if (outputSink == null)
            {
                if (lastOutputState != "no_sink")
                {
                    lastOutputState = "no_sink";
                    progress.Report("[PRO2_OUTPUT] no real Pro2 output sink; source=" + packet.Source);
                }
                continue;
            }

            if (!outputSink.IsOutputReady)
            {
                string state = "not_ready:" + packet.Source;
                if (lastOutputState != state)
                {
                    lastOutputState = state;
                    progress.Report("[PRO2_OUTPUT] real Pro2 output is not ready; source=" +
                                    packet.Source + "; host feedback remains log-only.");
                }
                continue;
            }

            if (outputSink.TryWriteOutputPacket(packet, out string error))
            {
                writes++;
                Interlocked.Increment(ref feedbackQueued);
                string state = packet.Source + "/" + (packet.Active ? "active" : "neutral");
                if (state != lastOutputState || writes <= 4 || writes % 100 == 0)
                {
                    lastOutputState = state;
                    progress.Report("[PRO2_OUTPUT] queued " + state + " count=" + writes);
                    if (!string.IsNullOrEmpty(packet.ConversionTelemetry))
                        progress.Report("[RUMBLE_CONVERSION] source=" + packet.Source + " " + packet.ConversionTelemetry);
                }
            }
            else
            {
                failures++;
                string state = "write_failed:" + error;
                if (state != lastOutputState || failures <= 4 || failures % 20 == 0)
                {
                    lastOutputState = state;
                    progress.Report("[PRO2_OUTPUT] write failed count=" + failures + " error=" + error);
                }
            }
        }
        }
        finally
        {
            // This loop is the sole owner of the scheduler: reset DSP/history
            // before recovery, mode replacement, shutdown or a cancelled read.
            QueueFeedbackStop("dualsense-feedback-loop-stop", idleOnly: false);
        }
    }

    private async Task RunLoopAsync(
        string name,
        Func<CancellationToken, Task> loop,
        CancellationToken cancellationToken)
    {
        try
        {
            await loop(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            progress.Report("[VIIPER] " + name + " loop failed: " + ex.Message);
            cts.Cancel();
            if (Interlocked.Exchange(ref faultReported, 1) == 0)
            {
                faultProgress?.Report(ex);
            }
        }
    }

    private async Task<bool> RecoverStreamAsync(
        string loopName,
        BridgeDeviceStream failedStream,
        Exception error,
        CancellationToken cancellationToken)
    {
        if (cts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        await streamRecoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            BridgeDeviceInfo? currentDevice = device;
            if (currentDevice == null)
            {
                return false;
            }

            BridgeDeviceStream? currentStream = GetStream();
            if (currentStream != null && !ReferenceEquals(currentStream, failedStream))
            {
                progress.Report("[VIIPER] " + loopName + " stream resumed on recovered API stream.");
                return true;
            }

            if (ReferenceEquals(currentStream, failedStream))
            {
                _ = ExchangeStream(null);
            }

            progress.Report("[VIIPER] " + loopName +
                            " stream interrupted: " + error.Message +
                            " Reopening API stream without detaching USB device. SEVERE_LINK_ANOMALY=true");
            viiperStreamDisconnectCount++;
            lastViiperError = error.Message;
            try
            {
                await failedStream.DisposeAsync();
            }
            catch
            {
            }

            Exception? lastError = error;
            for (int attempt = 1; attempt <= StreamRecoveryAttempts; attempt++)
            {
                try
                {
                    BridgeDeviceStream replacement = await client.OpenStreamAsync(
                        currentDevice.BusId,
                        currentDevice.DevId,
                        cancellationToken).ConfigureAwait(false);
                    SetStream(replacement);
                    // The last computed frame might not have reached the USB
                    // device. Force a physical-only release even in a repeat gap.
                    Interlocked.Or(ref rearCancelRequested, 3);
                    viiperStreamRecoveryCount++;
                    lastUsbipLifecycleEvent = "api_stream_reopened_without_detach";
                    progress.Report("[VIIPER] API stream recovered attempt=" + attempt +
                                    " bus=" + currentDevice.BusId +
                                    " dev=" + currentDevice.DevId +
                                    " SEVERE_LINK_ANOMALY=true");
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    lastViiperError = ex.Message;
                    int delayMs = Math.Min(250 * attempt, 1000);
                    progress.Report("[VIIPER] API stream recover retry " + attempt +
                                    "/" + StreamRecoveryAttempts +
                                    " failed: " + ex.Message);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
            }

            progress.Report("[VIIPER] API stream recovery exhausted; session restart required. last=" +
                            (lastError?.Message ?? error.Message));
            lastViiperError = lastError?.Message ?? error.Message;
            return false;
        }
        finally
        {
            streamRecoveryGate.Release();
        }
    }

    private BridgeDeviceStream? GetStream()
    {
        lock (streamSync)
        {
            return stream;
        }
    }

    private void SetStream(BridgeDeviceStream? value)
    {
        lock (streamSync)
        {
            stream = value;
        }
    }

    private BridgeDeviceStream? ExchangeStream(BridgeDeviceStream? value)
    {
        lock (streamSync)
        {
            BridgeDeviceStream? previous = stream;
            stream = value;
            return previous;
        }
    }

    private static async Task DrainTaskAsync(Task? task)
    {
        if (task == null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception)
        {
            // The user-facing log already records stream state; cleanup should not throw.
        }
    }

    private static string SanitizeLogValue(string value)
    {
        return value.Replace("\"", "'", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    /// <summary>校准面板用：陀螺右摇杆零偏是否已学习完成及其数值。</summary>
    public bool GyroBiasReady => gyroStick?.ZeroReady ?? false;
    public string GyroBiasSource => gyroStick?.BiasSource ?? "none";
    public double GyroBiasYawDeg => gyroStick?.BiasYawDeg ?? 0.0;
    public double GyroBiasPitchDeg => gyroStick?.BiasPitchDeg ?? 0.0;
    public double GyroLastYaw => gyroStick?.LastYaw ?? 0.0;
    public double GyroLastPitch => gyroStick?.LastPitch ?? 0.0;

    /// <summary>
    /// 链路切换（蓝牙 ↔ 有线）时由主视图热替换输入源，不重建虚拟设备。
    /// 顺带重载零偏档案并重新开始 350ms 中立保护。
    /// </summary>
    public void SetInputSource(IGamepadInputSource? source, IGamepadOutputSink? sink = null)
    {
        IGamepadInputSource? previous = inputSource;
        if (ReferenceEquals(previous, source)) return;
        inputSource = source;
        outputSink = sink ?? source as IGamepadOutputSink;
        sessionStartTicks = Stopwatch.GetTimestamp();
        gyroStick?.ReloadSavedBias();
        progress.Report("[LINK_OUTPUT] input_source_swapped from=" +
            (previous?.GetType().Name ?? "none") + " to=" + (source?.GetType().Name ?? "none"));
    }

    /// <summary>设置页点「重新校准」时清掉旧档案，当前会话立即重新学习。</summary>
    public void ClearGyroBias()
    {
        gyroStick?.ClearSavedBias();
        progress.Report("[GYRO_BIAS] 已清除保存的零偏，本次连接重新学习。");
    }

    public void ReloadGyroBias() => gyroStick?.ReloadSavedBias();

    public async ValueTask DisposeAsync()
    {
        Pro2GyroBiasStore.DeviceKeyChanged -= OnGyroBiasDeviceChanged;
        // 退出前把静止跟踪收敛后的最新零偏写回设置页。
        gyroStick?.FlushBias();
        await StopAsync();
        streamRecoveryGate.Dispose();
        cts.Dispose();
    }

    private sealed class VirtualImuTelemetryStats
    {
        private ulong samples;
        private MotionSample lastOutput;
        private MotionSample lastSource;
        private MotionSample maxAbsOutput;
        private MotionSample maxAbsSource;

        public void Record(ViiperDeviceProfile profile, ReadOnlySpan<byte> packet, GamepadState? source)
        {
            if (!TryDecodeOutput(profile.Mode, packet, out MotionSample output))
            {
                return;
            }

            samples++;
            lastOutput = output;
            maxAbsOutput = MaxAbs(maxAbsOutput, output);

            if (source != null)
            {
                lastSource = new MotionSample(
                    source.GyroX,
                    source.GyroY,
                    source.GyroZ,
                    source.AccelX,
                    source.AccelY,
                    source.AccelZ);
                maxAbsSource = MaxAbs(maxAbsSource, lastSource);
            }
        }

        public string SummaryAndMaybeReset(bool reset)
        {
            string summary =
                " imu_samples=" + samples +
                " imu_out_gyro_last=" + lastOutput.GyroX + "," + lastOutput.GyroY + "," + lastOutput.GyroZ +
                " imu_out_gyro_max_abs=" + maxAbsOutput.GyroX + "," + maxAbsOutput.GyroY + "," + maxAbsOutput.GyroZ +
                " imu_out_accel_last=" + lastOutput.AccelX + "," + lastOutput.AccelY + "," + lastOutput.AccelZ +
                " imu_out_accel_max_abs=" + maxAbsOutput.AccelX + "," + maxAbsOutput.AccelY + "," + maxAbsOutput.AccelZ +
                " imu_src_gyro_last=" + lastSource.GyroX + "," + lastSource.GyroY + "," + lastSource.GyroZ +
                " imu_src_gyro_max_abs=" + maxAbsSource.GyroX + "," + maxAbsSource.GyroY + "," + maxAbsSource.GyroZ +
                " imu_src_accel_last=" + lastSource.AccelX + "," + lastSource.AccelY + "," + lastSource.AccelZ +
                " imu_src_accel_max_abs=" + maxAbsSource.AccelX + "," + maxAbsSource.AccelY + "," + maxAbsSource.AccelZ;
            if (reset)
            {
                samples = 0;
                lastOutput = default;
                lastSource = default;
                maxAbsOutput = default;
                maxAbsSource = default;
            }
            return summary;
        }

        public string Ps5PipelineSummary()
        {
            if (samples == 0)
            {
                return "";
            }

            double pro2GyroRawPerDps = ProfessionalImuConverter.SwitchGyroRawPerDps;
            double pro2AccelRawPerG = ProfessionalImuConverter.SwitchAccelRawPerG;
            double dsGyroRawPerDps = ProfessionalImuConverter.DualSenseGyroRawPerDps;
            double dsAccelRawPerG = ProfessionalImuConverter.DualSenseAccelRawPerG;
            string rawPro2 =
                "gyro:" + lastSource.GyroX + "," + lastSource.GyroY + "," + lastSource.GyroZ +
                ";accel:" + lastSource.AccelX + "," + lastSource.AccelY + "," + lastSource.AccelZ;
            string physicalPro2 =
                "gyro_dps:" + Format(lastSource.GyroX / pro2GyroRawPerDps) + "," +
                Format(lastSource.GyroY / pro2GyroRawPerDps) + "," +
                Format(lastSource.GyroZ / pro2GyroRawPerDps) +
                ";accel_g:" + Format(lastSource.AccelX / pro2AccelRawPerG) + "," +
                Format(lastSource.AccelY / pro2AccelRawPerG) + "," +
                Format(lastSource.AccelZ / pro2AccelRawPerG);
            string mappedDs =
                "gyro_dps:" + Format(lastOutput.GyroX / dsGyroRawPerDps) + "," +
                Format(lastOutput.GyroY / dsGyroRawPerDps) + "," +
                Format(lastOutput.GyroZ / dsGyroRawPerDps) +
                ";accel_g:" + Format(lastOutput.AccelX / dsAccelRawPerG) + "," +
                Format(lastOutput.AccelY / dsAccelRawPerG) + "," +
                Format(lastOutput.AccelZ / dsAccelRawPerG);
            string dsRaw =
                "gyro:" + lastOutput.GyroX + "," + lastOutput.GyroY + "," + lastOutput.GyroZ +
                ";accel:" + lastOutput.AccelX + "," + lastOutput.AccelY + "," + lastOutput.AccelZ;
            return
                "raw_pro2=" + rawPro2 +
                " physical_pro2=" + physicalPro2 +
                " mapped_ds=" + mappedDs +
                " ds_raw=" + dsRaw +
                " neutral_accel=0,8192,0" +
                " gyro_raw_per_dps=" + Format(dsGyroRawPerDps) +
                " pro2_accel_raw_per_g=" + Format(pro2AccelRawPerG) +
                " ds_accel_raw_per_g=" + Format(dsAccelRawPerG);
        }

        private static bool TryDecodeOutput(
            ViiperVirtualMode mode,
            ReadOnlySpan<byte> packet,
            out MotionSample sample)
        {
            sample = default;
            if (mode is ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.DualSenseProfessionalImuTest)
            {
                if (packet.Length < 33)
                {
                    return false;
                }

                sample = new MotionSample(
                    ReadI16(packet, 21),
                    ReadI16(packet, 23),
                    ReadI16(packet, 25),
                    ReadI16(packet, 27),
                    ReadI16(packet, 29),
                    ReadI16(packet, 31));
                return true;
            }

            if (mode is ViiperVirtualMode.Pro2 or ViiperVirtualMode.Pro1)
            {
                if (packet.Length < 24)
                {
                    return false;
                }

                sample = new MotionSample(
                    ReadI16(packet, 18),
                    ReadI16(packet, 20),
                    ReadI16(packet, 22),
                    ReadI16(packet, 12),
                    ReadI16(packet, 14),
                    ReadI16(packet, 16));
                return true;
            }

            return false;
        }

        private static short ReadI16(ReadOnlySpan<byte> packet, int offset) =>
            BinaryPrimitives.ReadInt16LittleEndian(packet.Slice(offset, 2));

        private static MotionSample MaxAbs(MotionSample current, MotionSample next) =>
            new(
                MaxAbs(current.GyroX, next.GyroX),
                MaxAbs(current.GyroY, next.GyroY),
                MaxAbs(current.GyroZ, next.GyroZ),
                MaxAbs(current.AccelX, next.AccelX),
                MaxAbs(current.AccelY, next.AccelY),
                MaxAbs(current.AccelZ, next.AccelZ));

        private static short MaxAbs(short currentAbs, short value)
        {
            int abs = Math.Abs((int)value);
            return abs > currentAbs ? (short)Math.Min(abs, short.MaxValue) : currentAbs;
        }

        private static string Format(double value) =>
            value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        private readonly record struct MotionSample(
            short GyroX,
            short GyroY,
            short GyroZ,
            short AccelX,
            short AccelY,
            short AccelZ);
    }
}
