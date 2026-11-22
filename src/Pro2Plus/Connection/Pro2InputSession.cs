using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Pro2Plus.Connection;

/// <summary>
/// 把新连接层收到的原始报告转成 <see cref="GamepadState"/>，并作为
/// 虚拟手柄会话的输入源。
///
/// 有线直接复用 <see cref="Pro2WiredProtocol"/> 的 0x05 报告解析（含摇杆校准、
/// 陀螺、电量）；蓝牙复用 <see cref="Pro2BleReportParser"/> 的 FD2 解析。
/// 这一层不做按键映射，映射在输出侧（VIIPER 会话）完成。
/// </summary>
public sealed class Pro2InputSession :
    IGamepadInputSource,
    IGamepadOutputSink,
    IGamepadInputRateSource,
    IGamepadSequentialInputSource,
    IGamepadRealtimeInputSource,
    IGamepadRuntimeTelemetrySink
{
    private const int MaxQueued = 32;

    private readonly Pro2WiredLink? _wired;
    private readonly Pro2BleLink? _ble;
    private readonly Pro2BleReportParser _bleParser = new();
    private readonly object _gate = new();
    private readonly Queue<GamepadState> _queue = new(MaxQueued);

    private GamepadState _latest = GamepadState.Neutral();
    private long _latestTicks;
    private ulong _sequence;
    private bool _active = true;

    private long _parsedCount;
    private long _windowStartTicks;
    private long _windowCount;
    private double _rateHz;
    private double _pushHz;
    private long _parseFailures;
    private readonly object _sampleGate = new();
    private Pro2RuntimeCalibration.StickSampleAccumulator? _sampler;
    private readonly Pro2RuntimeCalibration.GyroRawBiasLearner _bleBiasLearner = new();

    public Pro2InputSession(Pro2WiredLink wired)
    {
        _wired = wired;
        LinkLabel = "USB 有线";
    }

    public Pro2InputSession()
    {
        LinkLabel = "蓝牙";
    }

    /// <summary>蓝牙链路：输入走 FD2 解析，震动写回 rumble 特征。</summary>
    public Pro2InputSession(Pro2BleLink ble)
    {
        _ble = ble;
        LinkLabel = "蓝牙";
    }

    public string LinkLabel { get; }

    /// <summary>
    /// 震动强度增益：同时推给当前链路的两条实现，改完即时生效、不用重连。
    /// 有线与蓝牙用同一个值，保证两种连接的震动强度一致。
    /// </summary>
    private double pendingRumbleGain = 1.0;

    public void SetRumbleGain(double gain)
    {
        pendingRumbleGain = double.IsFinite(gain) ? Math.Clamp(gain, 0, 3) : 1.0;
        _wired?.SetRumbleGain(pendingRumbleGain);
        _ble?.SetRumbleGain(pendingRumbleGain);
    }

    /// <summary>链路刚建好但还没推增益时，连接层会读它带入初始值。</summary>
    public double RumbleGain => pendingRumbleGain;

    public bool IsRunning => _active;

    public string Status
    {
        get
        {
            lock (_gate)
            {
                if (!_active) return LinkLabel + "已断开";
                if (_latestTicks == 0) return LinkLabel + "已连接，等待第一帧";
                TimeSpan age = Stopwatch.GetElapsedTime(_latestTicks);
                return LinkLabel + "已连接 · " + age.TotalMilliseconds.ToString("0") + " ms 前收到输入";
            }
        }
    }

    public string MetricsSummary
    {
        get
        {
            lock (_gate)
            {
                return "link=" + LinkLabel +
                       " parsed=" + _parsedCount +
                       " parse_fail=" + _parseFailures +
                       " parsed_hz=" + _rateHz.ToString("F1") +
                       " push_hz=" + _pushHz.ToString("F1");
            }
        }
    }

    public double CurrentParsedRateHz => _rateHz;

    /// <summary>是否已经收到过至少一帧（用来区分「刚连上还没数据」和「数据停了」）。</summary>
    public bool HasFrames
    {
        get { lock (_gate) return _latestTicks != 0; }
    }

    /// <summary>距上一帧过了多久；没收到过帧时返回 TimeSpan.MaxValue。</summary>
    public TimeSpan LastFrameAge
    {
        get
        {
            lock (_gate)
            {
                return _latestTicks == 0
                    ? TimeSpan.MaxValue
                    : Stopwatch.GetElapsedTime(_latestTicks);
            }
        }
    }

    /// <summary>设置页校准用：开始采集当前手柄的摇杆零位或完整行程。</summary>
    public void StartStickSampling(bool rangeMode)
    {
        lock (_sampleGate)
        {
            _sampler = new Pro2RuntimeCalibration.StickSampleAccumulator(rangeMode);
        }
    }

    /// <summary>行程采样实时覆盖度（0–1）；非行程模式恒为 0。供面板做互动进度。</summary>
    public double StickSamplingCoverage
    {
        get
        {
            Pro2RuntimeCalibration.StickSampleAccumulator? sampler;
            lock (_sampleGate) sampler = _sampler;
            return sampler?.RangeCoverage ?? 0;
        }
    }

    /// <summary>左 / 右摇杆各自的到位程度（0–1），用来在面板上提示还差哪一根。</summary>
    public double LeftStickCoverage
    {
        get
        {
            Pro2RuntimeCalibration.StickSampleAccumulator? sampler;
            lock (_sampleGate) sampler = _sampler;
            return sampler?.LeftCoverage ?? 0;
        }
    }

    public double RightStickCoverage
    {
        get
        {
            Pro2RuntimeCalibration.StickSampleAccumulator? sampler;
            lock (_sampleGate) sampler = _sampler;
            return sampler?.RightCoverage ?? 0;
        }
    }

    /// <summary>结束采集并返回可保存的档案；调用方负责写设置页。</summary>
    /// <summary>诊断用：有线链路的原始轴值 / 当前零位。</summary>
    public string StickDiagnostic => _wired is not null
        ? _wired.Protocol?.StickDiagnostic ?? "no-wired-protocol"
        : "ble " + _bleParser.StickTelemetry;

    /// <summary>
    /// 诊断用：两条链路**未经归一化**的原始四轴。
    /// 判断「漂移是摇杆本身在动，还是软件零位没跟上」只能看它：
    /// 归一化之后的输出被死区剪过，残差看不见。
    /// </summary>
    public PhysicalStickAxes? RawStickAxes => _wired is not null
        ? _wired.Protocol?.LastRaw
        : _bleParser.LastPhysicalStickAxes;

    /// <summary>
    /// 设置页校准写盘后立刻推给当前链路：零位/行程当场生效，不用拔插重连。
    /// 两条链路同一条规矩：中心只在与实测原始零位吻合（≤96）时采纳，
    /// 否则只借行程——另一条链路的坐标系可能差 160 多个单位，套过来就是 8% 漂移。
    /// </summary>
    public void ApplyStickCalibration(StickCalibrationProfile? profile)
    {
        if (profile is null || !profile.CenterCalibrated) return;

        if (_wired?.Protocol is { } pad)
        {
            pad.ApplyCalibration(profile);
            return;
        }

        if (_ble is null) return;

        StickCalibrationProfile normalized = StickCalibrationProfile.Normalize(profile);
        PhysicalStickAxes raw = _bleParser.LastPhysicalStickAxes;
        bool agrees =
            Math.Abs(normalized.CenterLx - raw.Lx) <= 96 &&
            Math.Abs(normalized.CenterLy - raw.Ly) <= 96 &&
            Math.Abs(normalized.CenterRx - raw.Rx) <= 96 &&
            Math.Abs(normalized.CenterRy - raw.Ry) <= 96;

        _bleParser.SetStickCalibration(agrees ? normalized : normalized with
        {
            CenterLx = raw.Lx,
            CenterLy = raw.Ly,
            CenterRx = raw.Rx,
            CenterRy = raw.Ry
        });
    }

    public StickCalibrationResult CompleteStickSampling(bool rangeMode)
    {
        Pro2RuntimeCalibration.StickSampleAccumulator? sampler;
        lock (_sampleGate)
        {
            sampler = _sampler;
            _sampler = null;
        }
        if (sampler is null || sampler.RangeMode != rangeMode)
        {
            return new StickCalibrationResult(false, "校准未在进行中，请重新点击。", null);
        }
        return sampler.Complete(
            Pro2GyroBiasStore.LoadStickCalibration(),
            Pro2GyroBiasStore.CurrentKey);
    }

    public bool IsOutputReady
    {
        get
        {
            // 有线震动走 USB 中断端点；蓝牙写 rumble 特征（0x02 报告编码成 33 字节包）。
            if (!_active) return false;
            if (_wired is { IsOpen: true }) return true;
            return _ble?.IsRumbleReady == true;
        }
    }

    /// <summary>连接层每收到一帧就调用一次。</summary>
    public void Feed(byte[] report, long ticks)
    {
        if (!_active || report is null || report.Length == 0) return;

        GamepadState? state = null;
        bool parsed;

        if (_wired is not null)
        {
            Pro2WiredProtocol? pad = _wired.Protocol;
            if (pad is null) return;
            try
            {
                state = pad.Parse(report, report.Length, ticks, ++_sequence);
                parsed = state is not null;
            }
            catch
            {
                parsed = false;
            }
        }
        else
        {
            GamepadState bleState = GamepadState.Neutral();
            parsed = _bleParser.TryParseFd2Payload(report, out bleState, out _) ||
                     _bleParser.TryParse(report, out bleState, out _);
            if (parsed)
            {
                // 关键：每帧的递增序号。陀螺转摇杆（GyroStick）用
                // state.Updates 判断「这一帧是不是新的」，序号恒为 0 时
                // 它从第二帧起就一直走「重复帧」分支，把右摇杆冻结在
                // 第一帧的值（通常是正中的 0），表现就是右摇杆完全没反应。
                bleState.Updates = (uint)++_sequence;
                state = bleState;
            }
        }

        if (!parsed || state is null)
        {
            lock (_gate) { _parseFailures++; }
            return;
        }

        // 蓝牙链路没有协议层零偏学习，这里用静止窗口补一份。
        if (_ble is not null && _wired is null) _bleBiasLearner.Add(state);

        // 零位采样必须在 Apply 之前：Apply 会按旧档案归中，采到的是「已经被纠正过的值」，
        // 拿它当新零位等于把上一次的偏差记住，越校越偏。
        Pro2RuntimeCalibration.StickSampleAccumulator? sampler;
        lock (_sampleGate) sampler = _sampler;
        if (sampler is not null)
        {
            // 校准时一律喂**原始读数**：state 已经被归一化过（静止恒 2048），
            // 拿它当零位只会存下「假零位」——旧版就是这么让有线套上 2048 而漂 8% 的。
            GamepadState sample = state;
            PhysicalStickAxes? raw = _wired is not null
                ? _wired.Protocol?.LastRaw
                : _bleParser.LastPhysicalStickAxes;
            if (raw is { } axes)
            {
                sample.Lx = axes.Lx;
                sample.Ly = axes.Ly;
                sample.Rx = axes.Rx;
                sample.Ry = axes.Ry;
            }
            sampler.Add(sample);
        }

        // 统一应用保存的摇杆零位/行程、IMU 零偏与原生陀螺仪灵敏度。
        // PS5 / Pro1 原生体感直接读 state.GyroX/Y/Z，必须在这里处理。
        Pro2RuntimeCalibration.Apply(state, Pro2GyroBiasStore.ActiveGyroSensitivity);

        lock (_gate)
        {
            _parsedCount++;
            _latest = state;
            _latestTicks = ticks;

            if (_queue.Count >= MaxQueued) _queue.Dequeue();
            _queue.Enqueue(state);

            UpdateRateNoLock(ticks);
        }
    }

    private void UpdateRateNoLock(long ticks)
    {
        if (_windowStartTicks == 0)
        {
            _windowStartTicks = ticks;
            _windowCount = 0;
            return;
        }

        _windowCount++;
        double seconds = Stopwatch.GetElapsedTime(_windowStartTicks, ticks).TotalSeconds;
        if (seconds >= 0.5)
        {
            _rateHz = _windowCount / seconds;
            _windowStartTicks = ticks;
            _windowCount = 0;
        }
    }

    public bool TryGetLatest(out GamepadState state, out TimeSpan age)
    {
        lock (_gate)
        {
            state = _latest;
            age = _latestTicks == 0
                ? TimeSpan.MaxValue
                : Stopwatch.GetElapsedTime(_latestTicks);
            return _latestTicks != 0 && _active;
        }
    }

    public bool TryGetNext(out GamepadState state, out TimeSpan age)
    {
        lock (_gate)
        {
            if (_active && _queue.Count > 0)
            {
                state = _queue.Dequeue();
                age = _latestTicks == 0
                    ? TimeSpan.Zero
                    : Stopwatch.GetElapsedTime(_latestTicks);
                return true;
            }
        }

        return TryGetLatest(out state, out age);
    }

    /// <summary>实时消费：丢掉排队的旧帧，只给最新状态，避免 VIIPER 追赶。</summary>
    public bool TryGetNewest(out GamepadState state, out TimeSpan age, out int supersededCount)
    {
        lock (_gate)
        {
            supersededCount = _queue.Count;
            _queue.Clear();

            state = _latest;
            age = _latestTicks == 0
                ? TimeSpan.MaxValue
                : Stopwatch.GetElapsedTime(_latestTicks);
            return _latestTicks != 0 && _active;
        }
    }

    public bool TryWriteOutputReport(ReadOnlySpan<byte> report, out string error)
    {
        error = "";

        if (!_active)
        {
            error = "手柄已断开";
            return false;
        }

        if (_wired is { IsOpen: true } wired)
        {
            return wired.WriteOutput(report.ToArray());
        }

        if (_ble is not null)
        {
            return _ble.TryQueueRumble(report, out error);
        }

        error = "手柄已断开";
        return false;
    }

    public void ReportOutputPushRate(double actualHz) => _pushHz = actualHz;

    /// <summary>链路断开时调用：清空队列，避免旧帧被重新播出去。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _active = false;
            _queue.Clear();
            _latest = GamepadState.Neutral();
            _latestTicks = 0;
            _rateHz = 0;
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
