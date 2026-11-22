using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus.Connection;

/// <summary>
/// 第一页的连接服务：选路、状态机、把收到的帧交给输入会话。
///
/// 协议细节全部按旧版已经跑通的方案实现，但代码是新的：
///   * USB：Pro2WiredProtocol（SDL3 switch2，本机实测 498.7Hz），打开即跑完整初始化；
///   * BLE：跳过 0x15（蓝牙上提交配对照样会让手柄停推 FD2）、写命令带重试、
///     订阅前先写 FD2 自定义报告率描述符（133Hz），否则回报率一直是 0；
///   * 断线：读循环 1.5 秒无帧判定拔线，状态巡检再用 2.5 秒做第二道保险；
///   * 有线优先：插线自动连，蓝牙扫到一半插线也会立刻切过来。
/// </summary>
public sealed class Pro2ConnectionService : IDisposable
{
    // 手柄必须先长按 2 秒才广播，原来只找 10 秒：手慢一点或软件先起来几秒就必然「未找到手柄」。
    // 给足一分钟，界面实时显示已等秒数，用户不用掐时间。
    private static readonly TimeSpan BleScanTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BleScanSlice = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UsbWatchPeriod = TimeSpan.FromMilliseconds(1200);

    private readonly Pro2WiredLink _wired = new();
    private readonly Pro2BleLink _ble = new();
    private readonly object _gate = new();

    private Pro2LinkSnapshot _snapshot = Pro2LinkSnapshot.Idle;
    private DateTime? _lastConnectedAt;
    private volatile Pro2InputSession? _inputSession;

    private volatile bool _userDisconnected;
    private volatile bool _closingLink;

    private long _rateCount;
    private long _firstReportLogged;
    private int _bleAutoRetries;
    private volatile string? lastFirmwareText;

    private CancellationTokenSource? _connectCts;
    private Task _linkCloseTask = Task.CompletedTask;
    private readonly object _closeTaskGate = new();

    private System.Threading.Timer? _usbWatch;
    private int _usbWatchBusy;
    private int _diagStickTick;
    private long _autoRetryAfterTicks;
    private bool _disposed;

    public Pro2ConnectionService()
    {
        _wired.ReportReceived += OnReport;
        _wired.Closed += OnLinkClosed;
        _ble.ReportReceived += OnReport;
        _ble.Closed += OnLinkClosed;
        _ble.BatteryChanged += OnBatteryChanged;

        // 链路层的诊断日志统一走会话日志，排查蓝牙时直接搜 [BLE]。
        _ble.Log = line =>
        {
            Log?.Invoke(line);
            LinkDebug.Write("BLE " + line);
        };
    }

    public event Action? Changed;

    /// <summary>诊断日志出口，由主视图模型接到会话日志。</summary>
    public Action<string>? Log { get; set; }

    public Pro2LinkSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public DateTime? LastConnectedAt
    {
        get { lock (_gate) return _lastConnectedAt; }
    }

    public bool IsConnected => Snapshot.Phase == Pro2LinkPhase.Ready;

    public double RateHz => Snapshot.RateHz;

    /// <summary>连接就绪后可用：把原始报告转成 GamepadState 给虚拟手柄。</summary>
    public Pro2InputSession? InputSession => _inputSession;

    /// <summary>
    /// 链路切换（插线抢过蓝牙、蓝牙回连）会换掉输入会话对象。
    /// 运行中的虚拟手柄会话必须热替换输入源，否则会一直读已停止的旧会话：
    /// 表现就是回报率 0、摇杆冻结，切换瞬间还会吐出旧状态。
    /// </summary>
    public event Action<Pro2InputSession?>? InputSessionChanged;

    private void RaiseInputSessionChanged()
    {
        try
        {
            InputSessionChanged?.Invoke(_inputSession);
        }
        catch (Exception ex)
        {
            Log?.Invoke("[LINK] input_session_changed_error " + ex.Message);
        }
    }

    /// <summary>
    /// 真的有帧在流动。只在「握手成功且 1 秒内收到过能解析的帧」时为真，
    /// 避免出现「显示已连接、虚拟手柄也建了，但没有输入」的空壳。
    /// </summary>
    public bool HasLiveInput
    {
        get
        {
            Pro2InputSession? session = _inputSession;
            return session is not null &&
                   session.HasFrames &&
                   session.LastFrameAge <= TimeSpan.FromMilliseconds(1000);
        }
    }

    public void StartWatching()
    {
        if (_disposed || _usbWatch is not null) return;
        _usbWatch = new System.Threading.Timer(
            _ => WatchTick(), null, TimeSpan.FromMilliseconds(400), UsbWatchPeriod);
    }

    private void WatchTick()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _usbWatchBusy, 1) != 0) return;

        try
        {
            Pro2LinkSnapshot snapshot = Snapshot;

            // 诊断版：每一拍都记一行，出问题时能看出「哪一拍开始不对劲」。
            if (LinkDebug.Enabled)
            {
                Pro2InputSession? diag = _inputSession;
                string axes = "-";
                if (diag is not null && diag.HasFrames &&
                    diag.TryGetNewest(out GamepadState stick, out _, out _))
                {
                    axes = stick.Lx + "," + stick.Ly + "," + stick.Rx + "," + stick.Ry;
                }

                LinkDebug.Write("TICK phase=" + snapshot.Phase + " kind=" + snapshot.Kind +
                                " rate=" + snapshot.RateHz.ToString("F1") +
                                " sess=" + (diag is null ? "none" : (diag.HasFrames ? "frames" : "idle")) +
                                " age=" + (diag is null ? "-" : diag.LastFrameAge.TotalMilliseconds.ToString("0") + "ms") +
                                " frames=" + Interlocked.Read(ref _rateCount) +
                                " axes=(" + axes + ")");

                // 原始值/零位/输出两秒一行，用于判断零位准不准、有没有残余偏置。
                if (diag is not null && diag.HasFrames && ++_diagStickTick % 5 == 0)
                {
                    LinkDebug.Write("STICK " + diag.StickDiagnostic);
                }
            }

            if (snapshot.Phase is Pro2LinkPhase.Opening or Pro2LinkPhase.Initializing)
            {
                return;
            }

            // 有线已连接：用「多久没收到帧」做第二道断线保险。
            if (snapshot.Phase == Pro2LinkPhase.Ready && snapshot.Kind == Pro2LinkKind.Wired)
            {
                Pro2InputSession? wiredSession = _inputSession;
                if (wiredSession is not null &&
                    wiredSession.HasFrames &&
                    wiredSession.LastFrameAge.TotalMilliseconds > 2500)
                {
                    Log?.Invoke("[LINK] wired_silent age_ms=" +
                                wiredSession.LastFrameAge.TotalMilliseconds.ToString("0"));
                    BeginCloseQuietly();
                    Publish(Pro2LinkKind.None, Pro2LinkPhase.Idle, "已断开",
                        "USB 线已断开，插回线后会自动重新连接");
                }

                return;
            }

            Pro2LinkProbe probe = Pro2WiredLink.Probe();
            LinkDebug.Write("PROBE found=" + probe.Found + " count=" + probe.Count +
                            " detail=\"" + probe.Detail + "\" phase=" + snapshot.Phase +
                            " kind=" + snapshot.Kind);
            if (!probe.Found)
            {
                if (snapshot.Phase == Pro2LinkPhase.Detecting && snapshot.Kind == Pro2LinkKind.None)
                {
                    Log?.Invoke("[LINK] usb_lost " + probe.Detail);
                }

                _userDisconnected = false;
                return;
            }

            if (_userDisconnected) return;
            if (Stopwatch.GetTimestamp() < Interlocked.Read(ref _autoRetryAfterTicks)) return;

            if (snapshot.Phase == Pro2LinkPhase.Ready && snapshot.Kind == Pro2LinkKind.Bluetooth)
            {
                // 诊断版重点：这一行就是「蓝牙好好的却被 USB 抢走」的时刻。
                LinkDebug.Write("PREEMPT ble_by_usb count=" + probe.Count + " detail=\"" + probe.Detail + "\"");
                BeginCloseQuietly();
            }

            Interlocked.Exchange(ref _autoRetryAfterTicks,
                Stopwatch.GetTimestamp() + (long)(5 * Stopwatch.Frequency));

            _connectCts?.Cancel();
            Publish(Pro2LinkKind.None, Pro2LinkPhase.Idle, "未连接", "");
            _ = ConnectAsync();
        }
        catch
        {
            // 探测失败不影响界面。
        }
        finally
        {
            Interlocked.Exchange(ref _usbWatchBusy, 0);
        }
    }

    public async Task ConnectAsync()
    {
        if (_disposed) return;
        LinkDebug.Write("CONNECT begin phase=" + Snapshot.Phase + " kind=" + Snapshot.Kind);

        await WaitLinkClosedAsync().ConfigureAwait(false);

        lock (_gate)
        {
            if (_snapshot.Phase is Pro2LinkPhase.Detecting or Pro2LinkPhase.Opening
                or Pro2LinkPhase.Initializing)
            {
                return;
            }
        }

        _userDisconnected = false;

        _connectCts?.Cancel();
        _connectCts?.Dispose();
        var cts = new CancellationTokenSource();
        _connectCts = cts;

        try
        {
            Publish(Pro2LinkKind.None, Pro2LinkPhase.Detecting,
                "正在查找手柄…", "优先使用 USB 连接");

            Pro2LinkProbe probe = await Task.Run(Pro2WiredLink.Probe, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;

            if (probe.Found)
            {
                await ConnectWiredAsync(cts.Token).ConfigureAwait(false);
                return;
            }

            await ConnectBluetoothAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 被插线或用户操作抢占：由抢占方负责发布新状态。
        }
        catch (Exception ex)
        {
            ConnectionDiag.Failure("connect", "connect_flow_exception", ex);
            Log?.Invoke("[LINK] connect_error " + ex);
            Publish(Pro2LinkKind.None, Pro2LinkPhase.Failed,
                "连接失败", DescribeException(ex));
        }
    }

    private async Task ConnectWiredAsync(CancellationToken token)
    {
        // 必须在打开协议层之前切档：有线 IMU 零偏一学会就按这个键写回，
        // 否则从蓝牙切过来时会把有线零偏写进蓝牙档案。
        Pro2GyroBiasStore.SetDeviceKey(Pro2GyroBiasStore.WiredKey, out Pro2GyroBiasProfile wiredBias);
        Log?.Invoke("[GYRO_BIAS] wired_key=loaded \"" + wiredBias.Summary + "\"");
        Publish(Pro2LinkKind.Wired, Pro2LinkPhase.Opening, "正在连接…", "USB 有线");

        (bool ok, string detail) = await Task.Run(() =>
        {
            bool opened = _wired.Open(out string why);
            return (opened, why);
        }, token).ConfigureAwait(false);

        LinkDebug.Write("WIRED open ok=" + ok + " detail=\"" + detail + "\"");

        if (!ok)
        {
            if (token.IsCancellationRequested) return;
            ConnectionDiag.Failure("usb_connect", detail);
            Publish(Pro2LinkKind.Wired, Pro2LinkPhase.Failed, "USB 连接失败", detail);
            return;
        }

        Publish(Pro2LinkKind.Wired, Pro2LinkPhase.Initializing, "正在初始化…", "USB 有线");
        _wired.StartReading();
        _ = ProbeWiredReportRateAsync(token);

        // 先建会话再验收：只有真的解析出状态才算「能玩」。
        Pro2InputSession session = new(_wired);
        _inputSession?.Stop();
        _inputSession = session;
        RaiseInputSessionChanged();

        bool gotFrame = await WaitForSessionFramesAsync(session, TimeSpan.FromSeconds(2), token)
            .ConfigureAwait(false);
        LinkDebug.Write("WIRED frames=" + gotFrame + " rate=" + _wired.ReportCount);

        if (token.IsCancellationRequested) return;

        if (!gotFrame)
        {
            // 设备能打开但 2 秒里一帧都解析不出来：这多半不是 Pro2（系统里别的手柄 / 别的 HID），
            // 以前这里照样报「已连接」，界面就成了「已连接 + 0Hz」的空壳。
            Log?.Invoke("[LINK] wired_open_but_silent: 收不到手柄帧，按未连接处理");
            ConnectionDiag.Failure("usb_no_frames",
                "usb_open_but_silent timeout=2s hint=pid_mismatch_or_interface_claim");
            session.Stop();
            _inputSession = null;
            RaiseInputSessionChanged();
            try { _wired.Close(); } catch { }
            // 别每 5 秒反复去开同一个设备，歇 30 秒再看
            Interlocked.Exchange(ref _autoRetryAfterTicks,
                Stopwatch.GetTimestamp() + (long)(30 * Stopwatch.Frequency));
            Publish(Pro2LinkKind.None, Pro2LinkPhase.Failed,
                "未连接", "USB 上找到了设备但收不到手柄数据（可能是别的手柄），拔掉后点「重新检测」");
            return;
        }

        MarkConnected("已连接", Pro2LinkKind.Wired, "USB 有线 · 正在接收输入");
    }

    private async Task ConnectBluetoothAsync(CancellationToken token)
    {
        Publish(Pro2LinkKind.None, Pro2LinkPhase.Detecting,
            "正在搜索蓝牙…", "USB 没有检测到手柄，改用蓝牙查找");

        // 手柄被系统连过之后就不再广播，扫描会一直扫不到；
        // 所以先按上次的地址直连，连不上再退回扫描。
        if (BleAddressStore.TryLoad(out Pro2BleCandidate saved))
        {
            Publish(Pro2LinkKind.Bluetooth, Pro2LinkPhase.Opening, "正在连接…",
                saved.Name.Length > 0 ? saved.Name : "上次连接的手柄");
            Log?.Invoke("[LINK] ble_direct_try address=" + saved.Address.ToString("X12"));
            ConnectionDiag.Device("ble_saved", "addr=" + saved.Address.ToString("X12") +
                " type=" + saved.AddressType + " name=\"" + saved.Name + "\"");
            ConnectionDiag.Step("ble_direct_try addr=" + saved.Address.ToString("X12") + " timeout=5s");

            using var directCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            directCts.CancelAfter(TimeSpan.FromSeconds(5));

            (bool directOk, string directDetail) =
                await _ble.OpenAsync(saved, directCts.Token, knownAddress: true).ConfigureAwait(false);
            LinkDebug.Write("BLE direct ok=" + directOk + " detail=\"" + directDetail +
                            "\" addr=" + saved.Address.ToString("X12"));

            if (directOk)
            {
                await CompleteBluetoothConnectAsync(saved, token).ConfigureAwait(false);
                return;
            }

            if (token.IsCancellationRequested) return;

            Log?.Invoke("[LINK] ble_direct_fail " + directDetail);
            ConnectionDiag.Warn("ble_direct_fail addr=" + saved.Address.ToString("X12") +
                " detail=\"" + directDetail + "\"");
            CloseLinkQuietly();
            Publish(Pro2LinkKind.None, Pro2LinkPhase.Detecting,
                "正在搜索蓝牙…", "直连没有响应，改用扫描");
        }

        var found = new List<Pro2BleCandidate>();
        var seen = new HashSet<ulong>();
        Stopwatch elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < BleScanTimeout && !token.IsCancellationRequested)
        {
            List<Pro2BleCandidate> batch =
                await Pro2BleLink.ScanAsync(BleScanSlice, token).ConfigureAwait(false);

            foreach (Pro2BleCandidate candidate in batch)
            {
                if (seen.Add(candidate.Address))
                {
                    found.Add(candidate);
                    ConnectionDiag.Device("ble_candidate",
                        "addr=" + candidate.Address.ToString("X12") +
                        " type=" + candidate.AddressType +
                        " rssi=" + candidate.Rssi +
                        " name=\"" + candidate.Name + "\"");
                }
            }

            if (found.Count > 0) break;

            // 把「还要长按 2 秒」和已等时长写在副标题上，用户不必猜窗口还剩多久。
            Publish(Pro2LinkKind.None, Pro2LinkPhase.Detecting, "正在搜索蓝牙…",
                "长按手柄 2 秒进入配对 · 已等 " + (int)elapsed.Elapsed.TotalSeconds + " 秒");

            if (Pro2WiredLink.Probe().Found)
            {
                await ConnectWiredAsync(token).ConfigureAwait(false);
                return;
            }
        }

        if (token.IsCancellationRequested) return;

        if (found.Count == 0)
        {
            ConnectionDiag.Failure("ble_scan_no_device",
                "ble_scan_timeout 60s hint=pairing_or_adapter");
            Publish(Pro2LinkKind.None, Pro2LinkPhase.Failed, "未找到手柄",
                "插上 USB 线，或让手柄进入蓝牙配对后重试");
            return;
        }

        found.Sort((a, b) => b.Rssi.CompareTo(a.Rssi));
        Pro2BleCandidate target = found[0];
        ConnectionDiag.Device("ble_target",
            "addr=" + target.Address.ToString("X12") + " type=" + target.AddressType +
            " rssi=" + target.Rssi + " name=\"" + target.Name + "\" candidates=" + found.Count);

        Publish(Pro2LinkKind.Bluetooth, Pro2LinkPhase.Opening, "正在连接…",
            target.Name.Length > 0 ? target.Name : "Pro 手柄");

        (bool ok, string detail) = await _ble.OpenAsync(target, token).ConfigureAwait(false);
        LinkDebug.Write("BLE open ok=" + ok + " detail=\"" + detail +
                        "\" addr=" + target.Address.ToString("X12"));
        if (!ok)
        {
            if (token.IsCancellationRequested) return;
            ConnectionDiag.Failure("ble_open", detail + " addr=" + target.Address.ToString("X12"));
            Publish(Pro2LinkKind.Bluetooth, Pro2LinkPhase.Failed, "蓝牙连接失败", detail);
            return;
        }

        await CompleteBluetoothConnectAsync(target, token).ConfigureAwait(false);
    }

    /// <summary>链路已打开：建输入会话、验收帧、记录地址并发布 Ready。</summary>
    private async Task CompleteBluetoothConnectAsync(Pro2BleCandidate target, CancellationToken token)
    {
        BleAddressStore.Save(target);
        Log?.Invoke("[LINK] ble_address_saved address=" + target.Address.ToString("X12"));
        Log?.Invoke("[LINK] ble_report_rate=" + _ble.LastReportRateStatus);
        Publish(Pro2LinkKind.Bluetooth, Pro2LinkPhase.Initializing, "正在初始化…", "蓝牙");

        Pro2InputSession bleSession = new(_ble);
        _inputSession?.Stop();
        _inputSession = bleSession;
        RaiseInputSessionChanged();

        // 旧版的经验：订阅成功不等于有数据，必须等到能解析的帧才算通过。
        bool gotFrame = await WaitForSessionFramesAsync(bleSession, TimeSpan.FromSeconds(6), token)
            .ConfigureAwait(false);
        LinkDebug.Write("BLE frames=" + gotFrame + " rate_status=" + _ble.LastReportRateStatus);
        ConnectionDiag.Frame("ble_first_frame=" + gotFrame +
            " rate_status=" + _ble.LastReportRateStatus +
            " addr=" + target.Address.ToString("X12"));
        if (!gotFrame)
        {
            ConnectionDiag.Warn("ble_open_but_silent timeout=6s " +
                "hint=cccd_or_rate_descriptor_or_fw");
        }

        if (token.IsCancellationRequested) return;

        MarkConnected("已连接", Pro2LinkKind.Bluetooth,
            gotFrame ? "蓝牙 · 正在接收输入" : "蓝牙 · 等待手柄输入",
            "ble:" + target.Address.ToString("X12"));
    }

    /// <summary>
    /// 手柄有时插线后只以 250Hz 推 0x05 报告（正常约 500Hz）。
    /// 连上后实测 1 秒，偏低就补发一次初始化序列让它回到高速模式。
    /// </summary>
    private async Task ProbeWiredReportRateAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), token).ConfigureAwait(false);
            long before = _wired.ReportCount;
            await Task.Delay(TimeSpan.FromMilliseconds(1000), token).ConfigureAwait(false);
            long frames = _wired.ReportCount - before;
            if (frames >= 400)
            {
                Log?.Invoke("[USB] report_rate_ok hz=" + frames);
                return;
            }

            Log?.Invoke("[USB] report_rate_low hz=" + frames + " reapplying_init");
            _wired.ReapplyInit();
            long afterStart = _wired.ReportCount;
            await Task.Delay(TimeSpan.FromMilliseconds(1000), token).ConfigureAwait(false);
            Log?.Invoke("[USB] report_rate_after_reapply hz=" + (_wired.ReportCount - afterStart));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log?.Invoke("[USB] report_rate_probe_error " + ex.Message);
        }
    }

    private void MarkConnected(string title, Pro2LinkKind kind, string detail,
        string deviceKey = "")
    {
        lock (_gate)
        {
            _lastConnectedAt = DateTime.Now;
        }

        string resolvedKey = string.IsNullOrWhiteSpace(deviceKey)
            ? Pro2GyroBiasStore.WiredKey
            : deviceKey;
        Pro2GyroBiasStore.SetDeviceKey(resolvedKey, out Pro2GyroBiasProfile loadedBias);
        Log?.Invoke("[GYRO_BIAS] link_key=" + resolvedKey +
                    " loaded=\"" + loadedBias.Summary + "\"");

        _userDisconnected = false;
        Interlocked.Exchange(ref _bleAutoRetries, 0);
        Publish(kind, Pro2LinkPhase.Ready, title, detail);

        if (kind == Pro2LinkKind.Bluetooth)
        {
            _ = BoostBluetoothRateAsync();
        }
    }

    private static async Task<bool> WaitForSessionFramesAsync(
        Pro2InputSession session, TimeSpan timeout, CancellationToken token)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !token.IsCancellationRequested)
        {
            if (session.HasFrames) return true;
            try { await Task.Delay(30, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        return session.HasFrames;
    }

    private static async Task<bool> WaitForReportsAsync(
        Func<long> count, TimeSpan timeout, CancellationToken token)
    {
        long start = count();
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !token.IsCancellationRequested)
        {
            if (count() > start) return true;
            try { await Task.Delay(30, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        return count() > start;
    }

    private void OnReport(byte[] payload, long ticks)
    {
        Interlocked.Increment(ref _rateCount);

        if (Interlocked.Exchange(ref _firstReportLogged, 1) == 0)
        {
            LinkDebug.Write("FIRST_REPORT kind=" + Snapshot.Kind + " len=" + payload.Length);
            Log?.Invoke("[LINK] first_report kind=" + Snapshot.Kind +
                        " len=" + payload.Length +
                        " head=" + Convert.ToHexString(payload, 0, Math.Min(8, payload.Length)));
            ConnectionDiag.Frame("first_report kind=" + Snapshot.Kind + " len=" + payload.Length +
                " head=" + Convert.ToHexString(payload, 0, Math.Min(16, payload.Length)));
        }

        // 手柄在初始化阶段会回一包固件信息；不同固件版本的连接行为差别很大，
        // 抓到就记一次（内容变化时再记），换设备也能自动更新。
        if (FirmwareInfo.TryDecode(payload, out FirmwareInfo? firmware) && firmware is not null)
        {
            string text = "version=" + firmware.ControllerVersion +
                          " type=0x" + firmware.ControllerType.ToString("X2") +
                          " bt_patch=" + firmware.BluetoothPatchVersion +
                          " dsp=" + (firmware.DspVersion ?? "not_reported");
            if (text != lastFirmwareText)
            {
                lastFirmwareText = text;
                ConnectionDiag.Firmware("controller_report", text);
            }
        }

        _inputSession?.Feed(payload, ticks);
    }

    private void OnBatteryChanged(int level, bool charging)
    {
        lock (_gate)
        {
            // 连接期（Ready 之前）读到的一次电量先存住，MarkConnected 发布 Ready 时再用。
            _pendingBattery = level;
            _pendingCharging = charging;
            if (_snapshot.Phase != Pro2LinkPhase.Ready) return;
            _snapshot = _snapshot with { BatterySteps = level, Charging = charging };
        }

        Changed?.Invoke();
    }

    private void OnLinkClosed(string reason)
    {
        LinkDebug.Write("CLOSED reason=\"" + reason + "\" phase=" + Snapshot.Phase +
                        " kind=" + Snapshot.Kind + " closing=" + _closingLink);
        ConnectionDiag.Warn("link_closed reason=\"" + reason + "\" phase=" + Snapshot.Phase +
                             " kind=" + Snapshot.Kind + " closing=" + _closingLink);
        if (_disposed || _closingLink) return;

        Pro2LinkKind previous = Pro2LinkKind.None;
        lock (_gate)
        {
            if (_snapshot.Phase is Pro2LinkPhase.Idle or Pro2LinkPhase.Failed) return;
            previous = _snapshot.Kind;
        }

        DropInputSession();
        Publish(Pro2LinkKind.None, Pro2LinkPhase.Idle, "已断开",
            previous == Pro2LinkKind.Wired
                ? "USB 线已断开，插回线后会自动重新连接"
                : "蓝牙连接已断开，手柄可能休眠或离开了范围");

        // 蓝牙没有插拔这种天然信号：虚拟手柄重建、适配器抖动都会让链路断一次。
        // 只要不是用户主动断开，就自己重连几次，别让人手点。
        if (previous == Pro2LinkKind.Bluetooth)
        {
            ScheduleBluetoothRetry(reason);
        }
    }

    private void ScheduleBluetoothRetry(string reason)
    {
        if (_userDisconnected || _disposed) return;
        if (Interlocked.Increment(ref _bleAutoRetries) > 3)
        {
            Log?.Invoke("[LINK] ble_retry_giveup reason=\"" + reason + "\"");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500).ConfigureAwait(false);
                if (_disposed || _userDisconnected || IsConnected) return;

                Log?.Invoke("[LINK] ble_retry attempt=" +
                            Volatile.Read(ref _bleAutoRetries) + " reason=\"" + reason + "\"");
                await ConnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log?.Invoke("[LINK] ble_retry_error " + ex.GetType().Name);
            }
        });
    }

    public void RefreshRate()
    {
        long now = Stopwatch.GetTimestamp();
        long count = Interlocked.Read(ref _rateCount);
        double hz = 0;

        if (_rateSampleTicks != 0)
        {
            double seconds = Stopwatch.GetElapsedTime(_rateSampleTicks, now).TotalSeconds;
            if (seconds > 0.05)
            {
                hz = (count - _rateCountAtSample) / seconds;
                _rateCountAtSample = count;
                _rateSampleTicks = now;
            }
            else
            {
                hz = _rateHz;
            }
        }
        else
        {
            _rateSampleTicks = now;
            _rateCountAtSample = count;
        }

        Pro2LinkSnapshot current = Snapshot;
        _rateHz = current.Phase == Pro2LinkPhase.Ready ? Math.Max(0, hz) : 0;

        lock (_gate)
        {
            if (_snapshot.RateHz != _rateHz) _snapshot = _snapshot with { RateHz = _rateHz };
        }

        // 蓝牙每 5 秒打一行遥测：回报率为 0 时能立刻看出是「一帧都没有」
        // 还是「有帧但解析不出来」。
        if (current.Phase == Pro2LinkPhase.Ready && current.Kind == Pro2LinkKind.Bluetooth &&
            Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastBleTelemetryTicks)).TotalSeconds >= 5)
        {
            Interlocked.Exchange(ref _lastBleTelemetryTicks, now);
            Log?.Invoke("[BLE_TELEMETRY] hz=" + _rateHz.ToString("F1") +
                        " fd2_total=" + count +
                        " battery_read=" + _ble.LegacyCount +
                        " rumble_ready=" + _ble.IsRumbleReady +
                        " " + _ble.RumbleSummary +
                        " session=\"" + _ble.SessionStatus + "\"");
        }
    }

    private long _rateSampleTicks;
    private long _rateCountAtSample;
    private double _rateHz;
    private long _lastBleTelemetryTicks;

    // 最近一次读到的电量档位（-1 表示未知）。连接期的读数在 Ready 之前就要落在这里，
    // 否则 Publish 会把它当「非 Ready」丢掉。
    private int _pendingBattery = -1;
    private bool _pendingCharging;

    public void Disconnect()
    {
        _userDisconnected = true;
        _connectCts?.Cancel();

        DropInputSession();
        Publish(Pro2LinkKind.None, Pro2LinkPhase.Idle, "未连接",
            "插上 USB 线，或让手柄进入蓝牙配对");
        BeginCloseQuietly();
    }

    private void BeginCloseQuietly()
    {
        Task task = Task.Run(CloseLinkQuietly);
        lock (_closeTaskGate) _linkCloseTask = task;
    }

    private async Task WaitLinkClosedAsync()
    {
        Task task;
        lock (_closeTaskGate) task = _linkCloseTask;
        if (task.IsCompleted) return;
        try { await task.ConfigureAwait(false); } catch { }
    }

    private void CloseLinkQuietly()
    {
        _closingLink = true;
        try
        {
            _wired.Close();
            _ble.Close();
        }
        catch (Exception ex)
        {
            Log?.Invoke("[LINK] close_error " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _closingLink = false;
        }

        Interlocked.Exchange(ref _rateCount, 0);
        Interlocked.Exchange(ref _firstReportLogged, 0);
        _rateSampleTicks = 0;
        _rateHz = 0;
        _pendingBattery = -1;
        _pendingCharging = false;
    }


    /// <summary>
    /// 蓝牙连上后先量一次真实帧率；偏低就再申请一次 7.5ms 连接间隔。
    /// 首次请求经常被 Windows 在协商完成前吃掉，补一次能明显拉高回报率。
    /// </summary>
    private async Task BoostBluetoothRateAsync()
    {
        try
        {
            await Task.Delay(2500).ConfigureAwait(false);
            if (_disposed || !IsConnected || Snapshot.Kind != Pro2LinkKind.Bluetooth) return;

            double measured = await MeasureRateAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Log?.Invoke("[BLE] rate_probe hz=" + measured.ToString("F1") +
                        " interval=" + _ble.IntervalStatus);

            if (measured < 100)
            {
                _ble.NudgeFastInterval("low_rate_" + measured.ToString("F0"));
                await Task.Delay(2500).ConfigureAwait(false);

                if (_disposed || !IsConnected || Snapshot.Kind != Pro2LinkKind.Bluetooth) return;

                measured = await MeasureRateAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                Log?.Invoke("[BLE] rate_after_nudge hz=" + measured.ToString("F1") +
                            " interval=" + _ble.IntervalStatus);
            }

            // 默认不再订阅 legacy 电量通知。实测这条订阅会把这台手柄的推送切到旧报告流，
            // FD2 随即停推：界面上就是「回报率已经到 125，然后归零，电量这时出现，之后才回升」。
            // 电量改由连接期的一次读取 + 每 10 分钟的 RuntimeBattery 探测提供，
            // 两者都不与 FD2 抢流。诊断需要时用 PRO2_BLE_BATTERY_NOTIFY=1 打开。
            if (EnvironmentSwitches.IsEnabled("PRO2_BLE_BATTERY_NOTIFY"))
                await EnableBatteryNotifyAsync(measured).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke("[BLE] rate_probe_error " + ex.GetType().Name);
        }
    }


    /// <summary>
    /// 打开 legacy 特征的 0x09 通知拿电量（档位 0–8，8 是满电）。
    /// 旧版担心它会抢走 FD2 的推送，所以这里订阅后量一次帧率：
    /// 只要掉到基线 60% 以下就立刻退订，输入优先。
    /// </summary>
    private async Task EnableBatteryNotifyAsync(double baselineHz)
    {
        try
        {
            if (_disposed || !IsConnected || Snapshot.Kind != Pro2LinkKind.Bluetooth) return;

            if (!await _ble.SubscribeLegacyBatteryAsync(CancellationToken.None)
                    .ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(2500).ConfigureAwait(false);
            if (_disposed || !IsConnected || Snapshot.Kind != Pro2LinkKind.Bluetooth) return;

            double after = await MeasureRateAsync(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
            if (baselineHz >= 30 && after < baselineHz * 0.6)
            {
                _ble.DropLegacyBattery("fd2_rate_drop_" + after.ToString("F0"));
                Log?.Invoke("[BLE] battery_notify_rollback baseline_hz=" +
                            baselineHz.ToString("F1") + " after_hz=" + after.ToString("F1"));
                return;
            }

            Log?.Invoke("[BLE] battery_notify_kept fd2_hz=" + after.ToString("F1"));
        }
        catch (Exception ex)
        {
            Log?.Invoke("[BLE] battery_notify_error " + ex.GetType().Name);
        }
    }

    private async Task<double> MeasureRateAsync(TimeSpan window)
    {
        long start = Interlocked.Read(ref _rateCount);
        long startTicks = Stopwatch.GetTimestamp();
        await Task.Delay(window).ConfigureAwait(false);
        long end = Interlocked.Read(ref _rateCount);
        double seconds = Stopwatch.GetElapsedTime(startTicks).TotalSeconds;
        return seconds <= 0 ? 0 : (end - start) / seconds;
    }

    private void DropInputSession()
    {
        LinkDebug.Write("SESSION drop");
        Pro2InputSession? session = _inputSession;
        _inputSession = null;
        RaiseInputSessionChanged();
        session?.Stop();
    }

    private void Publish(Pro2LinkKind kind, Pro2LinkPhase phase, string title, string detail)
    {
        lock (_gate)
        {
            // 连接期读到的电量发生在 Ready 之前，早期版本会在这里被丢掉，
            // 于是只能靠「再订阅一次 legacy 通知」补回来——那一步会抢走 FD2 的推送，
            // 表现为回报率 125 → 0 → 电量出现 → 回升。现在把连接期读数缓存下来，直接沿用。
            int battery = _pendingBattery >= 0 ? _pendingBattery : _snapshot.BatterySteps;
            bool charging = _pendingBattery >= 0 ? _pendingCharging : _snapshot.Charging;
            _snapshot = new Pro2LinkSnapshot(
                kind,
                phase,
                title,
                detail,
                phase == Pro2LinkPhase.Ready ? _snapshot.RateHz : 0,
                phase == Pro2LinkPhase.Ready ? battery : -1,
                phase == Pro2LinkPhase.Ready && charging,
                phase == Pro2LinkPhase.Failed ? detail : "");
        }

        Log?.Invoke("[LINK] kind=" + kind + " phase=" + phase +
                    " title=\"" + title + "\" detail=\"" + detail + "\"");
        LinkDebug.Write("PUBLISH kind=" + kind + " phase=" + phase +
                        " title=\"" + title + "\" detail=\"" + detail + "\"");
        if (phase == Pro2LinkPhase.Ready)
            ConnectionDiag.Ok("connected kind=" + kind + " title=\"" + title + "\"");
        else if (phase == Pro2LinkPhase.Failed)
            ConnectionDiag.Warn("state_failed title=\"" + title + "\" detail=\"" + detail + "\"");
        Changed?.Invoke();
    }

    private static string DescribeException(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "手柄被其它程序占用，关掉占用它的软件后再试。",
        TimeoutException => "连接超时，手柄可能没有响应。",
        _ => "连接没能建立：" + ex.Message
    };

    public void Dispose()
    {
        _disposed = true;
        _usbWatch?.Dispose();
        _usbWatch = null;
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _wired.Dispose();
        _ble.Dispose();
    }
}
