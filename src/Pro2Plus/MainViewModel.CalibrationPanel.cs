using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Pro2Plus.Connection;

namespace Pro2Plus;

/// <summary>
/// 设置页「设备校准」的二级面板：点重新校准先弹面板，说明步骤 →
/// 开始后逐秒显示进度与实时提示 → 完成/失败都给明确反馈。
/// 三种校准（摇杆零位、摇杆行程、陀螺仪）共用同一个面板。
/// </summary>
public sealed partial class MainViewModel
{
    private const double CalibrationTotalPercent = 100.0;
    private const double CalibrationBarMaxWidth = 488.0;
    /// <summary>面板刷新心跳：40ms ≈ 25fps，进度条不会一秒一跳。</summary>
    private const int CalibrationRefreshMs = 40;

    private string calibrationKind = "";
    private string calibrationTitle = "";
    private string calibrationInstruction = "";
    private string calibrationLive = "";
    private string calibrationHint = "";
    private string calibrationResult = "";
    private bool calibrationSuccess;
    private bool calibrationPanelOpen;
    private bool calibrating;
    private bool calibrationFinished;
    private int calibrationSecondsLeft;
    private double calibrationProgress;

    private ICommand? openCalibrationCommand, closeCalibrationCommand, startCalibrationCommand;

    public ICommand OpenCalibrationCommand => openCalibrationCommand ??=
        new ActionCommand(parameter => OpenCalibrationPanel(parameter?.ToString() ?? "center"));
    public ICommand CloseCalibrationCommand => closeCalibrationCommand ??=
        new ActionCommand(_ => CloseCalibrationPanel());
    public ICommand StartCalibrationCommand => startCalibrationCommand ??=
        new ActionCommand(_ => RunExclusiveAsync("设备校准", RunCalibrationAsync));

    public bool IsCalibrationPanelOpen
    {
        get => calibrationPanelOpen;
        private set { if (calibrationPanelOpen == value) return; calibrationPanelOpen = value; OnPropertyChanged(); }
    }

    public string CalibrationTitle
    {
        get => calibrationTitle;
        private set { if (calibrationTitle == value) return; calibrationTitle = value; OnPropertyChanged(); }
    }

    public string CalibrationInstruction
    {
        get => calibrationInstruction;
        private set { if (calibrationInstruction == value) return; calibrationInstruction = value; OnPropertyChanged(); }
    }

    public string CalibrationLive
    {
        get => calibrationLive;
        private set
        {
            if (calibrationLive == value) return;
            calibrationLive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationStatusText));
        }
    }

    /// <summary>行程采样时的一行提示（还差哪根摇杆）；其它状态下为空。</summary>
    public string CalibrationHint
    {
        get => calibrationHint;
        private set
        {
            if (calibrationHint == value) return;
            calibrationHint = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationStatusText));
        }
    }

    public string CalibrationResult
    {
        get => calibrationResult;
        private set
        {
            if (calibrationResult == value) return;
            calibrationResult = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationStatusText));
        }
    }

    public bool CalibrationSuccess
    {
        get => calibrationSuccess;
        private set
        {
            if (calibrationSuccess == value) return;
            calibrationSuccess = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationStatusText));
        }
    }

    public bool IsCalibrating
    {
        get => calibrating;
        private set
        {
            if (calibrating == value) return;
            calibrating = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationStatusText));
            OnPropertyChanged(nameof(CalibrationStartVisible));
            OnPropertyChanged(nameof(CalibrationRedoVisible));
        }
    }

    public bool CalibrationFinished
    {
        get => calibrationFinished;
        private set
        {
            if (calibrationFinished == value) return;
            calibrationFinished = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationStatusText));
            OnPropertyChanged(nameof(CalibrationStartVisible));
            OnPropertyChanged(nameof(CalibrationRedoVisible));
        }
    }

    public int CalibrationSecondsLeft
    {
        get => calibrationSecondsLeft;
        private set
        {
            if (calibrationSecondsLeft == value) return;
            calibrationSecondsLeft = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationStatusText));
        }
    }

    public double CalibrationProgress
    {
        get => calibrationProgress;
        private set
        {
            if (Math.Abs(calibrationProgress - value) < 0.01) return;
            calibrationProgress = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CalibrationBarWidth));
            OnPropertyChanged(nameof(CalibrationPercentText));
            OnPropertyChanged(nameof(CalibrationPercentVisible));
        }
    }

    /// <summary>进度条填充宽度（px），省去自定义转换器。</summary>
    public double CalibrationBarWidth => CalibrationBarMaxWidth *
        Math.Clamp(calibrationProgress / CalibrationTotalPercent, 0.0, 1.0);

    /// <summary>第三行状态：只有一句话，不放长文本。</summary>
    public string CalibrationStatusText
    {
        get
        {
            if (IsCalibrating)
            {
                string tail = calibrationHint.Length == 0 ? "" : " · " + calibrationHint;
                return "采样中 · 剩余 " + calibrationSecondsLeft + " 秒" + tail;
            }
            if (CalibrationFinished)
                return calibrationSuccess ? "已完成 · 已写入配置" : "未通过 · " + ShortFailureReason();
            return calibrationLive;
        }
    }

    /// <summary>右上角百分比，0% 时不显示（避免出现一根空管子配一个 0%）。</summary>
    public string CalibrationPercentText =>
        calibrationProgress >= 0.5 ? ((int)Math.Round(calibrationProgress)) + "%" : "";

    public bool CalibrationPercentVisible => calibrationProgress >= 0.5;

    /// <summary>待开始/进行中/已完成 三态的主按钮互斥显示。</summary>
    public bool CalibrationStartVisible => !IsCalibrating && !CalibrationFinished;
    public bool CalibrationRedoVisible => CalibrationFinished && !IsCalibrating;

    private string ShortFailureReason()
    {
        string text = calibrationResult;
        int cut = text.IndexOf('：');
        if (cut >= 0 && cut < text.Length - 1) text = text[(cut + 1)..];
        text = text.Trim().TrimEnd('。');
        return text.Length <= 20 ? text : text[..20] + "…";
    }

    public bool CalibrationCanStart => !IsCalibrating && linkService.InputSession?.HasFrames == true;

    private void OpenCalibrationPanel(string kind)
    {
        calibrationKind = kind switch
        {
            "range" => "range",
            "gyro" => "gyro",
            _ => "center"
        };
        CalibrationTitle = calibrationKind switch
        {
            "range" => "摇杆行程校准",
            "gyro" => "陀螺仪零位校准",
            _ => "摇杆零位校准"
        };
        CalibrationInstruction = calibrationKind switch
        {
            "range" => "八秒内把左右摇杆分别沿外圈缓慢转满至少三圈，上/下/左/右都要推到底，最后松开。",
            "gyro" => "把手柄平放在稳定桌面，三秒内不要按键、不要移动。校准结果会存到配置页，下次连接直接复用。",
            _ => "完全松开左右摇杆，两秒内不要触碰摇杆或按键，手柄放平稳。"
        };
        CalibrationLive = LinkReadyHint();
        CalibrationResult = "";
        CalibrationSuccess = false;
        CalibrationFinished = false;
        IsCalibrating = false;
        CalibrationSecondsLeft = 0;
        CalibrationProgress = 0;
        CalibrationHint = "";
        IsCalibrationPanelOpen = true;
        OnPropertyChanged(nameof(CalibrationCanStart));
        AppendLog("[CAL_PANEL] open kind=" + calibrationKind);
    }

    private string LinkReadyHint()
    {
        Pro2InputSession? link = linkService.InputSession;
        if (link is null || !link.HasFrames)
            return "当前没有正在接收输入的真实 Pro2。请先点“连接 Pro2 · 进入游戏”再校准。";
        return "已连接 " + link.LinkLabel + "，可以开始校准。";
    }

    private void CloseCalibrationPanel()
    {
        if (IsCalibrating)
        {
            currentOperationCts?.Cancel();
        }
        IsCalibrationPanelOpen = false;
        AppendLog("[CAL_PANEL] close kind=" + calibrationKind);
    }

    private async Task RunCalibrationAsync(CancellationToken cancellationToken)
    {
        if (IsCalibrating) return;
        Pro2InputSession? link = linkService.InputSession;
        if (link is null || !link.HasFrames)
        {
            FinishCalibration(false, "没有正在接收输入的真实 Pro2，校准未开始。请先连接并进入游戏。");
            return;
        }

        IsCalibrating = true;
        CalibrationFinished = false;
        CalibrationResult = "";
        OnPropertyChanged(nameof(CalibrationCanStart));
        try
        {
            switch (calibrationKind)
            {
                case "range":
                    await RunStickCalibrationAsync(link, rangeMode: true, cancellationToken);
                    break;
                case "gyro":
                    await RunGyroCalibrationAsync(cancellationToken);
                    break;
                default:
                    await RunStickCalibrationAsync(link, rangeMode: false, cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            FinishCalibration(false, "校准已取消，旧配置保持不变。");
        }
        catch (Exception ex)
        {
            FinishCalibration(false, "校准失败：" + ViewModelPolicies.FirstLine(ex.Message));
        }
        finally
        {
            IsCalibrating = false;
            OnPropertyChanged(nameof(CalibrationCanStart));
        }
    }

    private async Task RunStickCalibrationAsync(Pro2InputSession link, bool rangeMode, CancellationToken token)
    {
        int seconds = rangeMode ? 8 : 2;
        string label = rangeMode ? "完整行程" : "零位";
        link.StartStickSampling(rangeMode);
        var clock = Stopwatch.StartNew();
        int shownSecond = -1;

        // 行程进度：目标 = 两根摇杆的平均到位程度，但**上升限速**。
        // 一根摇杆转一圈 = 该摇杆 100% = 总进度 50%，若按瞬时值走会"飙"上去；
        // 限速 0.22/s（0→满格约 4.5 秒，铺在 8 秒窗口里）后是"长"上去的，
        // 且摇杆推得快时会明显加速，仍然跟手。
        double shownCoverage = 0;
        const double coverageRisePerSecond = 0.22;

        while (true)
        {
            double t = clock.Elapsed.TotalSeconds;
            if (t >= seconds) break;

            double percent;
            if (rangeMode)
            {
                double target = link.StickSamplingCoverage;
                double step = coverageRisePerSecond * CalibrationRefreshMs / 1000.0;
                shownCoverage = Math.Min(target, shownCoverage + step);
                percent = Math.Min(0.97, shownCoverage) * CalibrationTotalPercent;
                CalibrationHint = ViewModelFormatting.RangeHint(link.LeftStickCoverage, link.RightStickCoverage);
            }
            else
            {
                // 零位（静止校准）：按时间平滑推进。
                percent = t / seconds * CalibrationTotalPercent;
            }
            CalibrationProgress = percent;

            int left = Math.Max(0, (int)Math.Ceiling(seconds - t));
            if (left != shownSecond)
            {
                shownSecond = left;
                CalibrationSecondsLeft = left;
                CalibrationLive = rangeMode
                    ? "正在采集四轴行程端点…请把每个摇杆推到底（剩余 " + left + " 秒）"
                    : "正在采集摇杆静止中心…请勿触碰（剩余 " + left + " 秒）";
                AppendLog("[CAL_PANEL] " + label + " tick=" + (seconds - left) + "/" + seconds +
                          (rangeMode
                              ? " L=" + link.LeftStickCoverage.ToString("F2") +
                                " R=" + link.RightStickCoverage.ToString("F2")
                              : ""));
            }

            await Task.Delay(CalibrationRefreshMs, token);
        }
        CalibrationHint = "";

        StickCalibrationResult result = link.CompleteStickSampling(rangeMode);
        if (!result.Success || result.Profile is null)
        {
            FinishCalibration(false, label + "校准未通过：" + result.Message + " 旧配置保持不变。");
            return;
        }

        // 统一按「手柄身份」存：USB 与蓝牙读写同一份，插拔线不用重校。
        string address = Pro2GyroBiasStore.StickCalibrationKey(Pro2GyroBiasStore.CurrentKey);
        userSettings.SetStickCalibration(address, result.Profile);
        SaveUserSettings("[CAL_PANEL]");
        // 当场推给当前链路：不用等下次连接，松手立刻就不漂。
        link.ApplyStickCalibration(result.Profile);
        RaiseStickCalibrationChanged();
        FinishCalibration(true, label + "校准完成并已保存：" + result.Message + " 下次连接自动加载。");
    }

    /// <summary>
    /// 只报"还差哪根"：两根都没动时不说话（上面的引导已经交代了怎么推），
    /// 一根到位后才提示另一根，一句话压在剩余秒数后面。
    /// </summary>
    private async Task RunGyroCalibrationAsync(CancellationToken token)
    {
        // 用户主动重校准：丢掉旧档案，让当前会话重新学习零偏。
        Pro2GyroBiasStore.Clear();
        session?.ClearGyroBias();
        AppendLog("[CAL_PANEL] gyro saved_bias_cleared key=" + Pro2GyroBiasStore.CurrentKey);

        const int seconds = 3;
        var clock = Stopwatch.StartNew();
        int shownSecond = -1;
        int liveTick = 0;
        while (true)
        {
            double t = clock.Elapsed.TotalSeconds;
            if (t >= seconds) break;

            CalibrationProgress = t / seconds * CalibrationTotalPercent;

            int left = Math.Max(0, (int)Math.Ceiling(seconds - t));
            if (left != shownSecond)
            {
                shownSecond = left;
                CalibrationSecondsLeft = left;
                AppendLog("[CAL_PANEL] gyro tick=" + (seconds - left) + "/" + seconds);
            }

            // 实时角速度每 ~160ms 刷新一次，够看出手有没有动，又不会把文字刷成流水。
            if (liveTick++ % 4 == 0)
            {
                OutputPadSession? active = session;
                CalibrationLive = active is not null
                    ? "实时角速度 yaw=" + active.GyroLastYaw.ToString("F1") +
                      " pitch=" + active.GyroLastPitch.ToString("F1") +
                      " deg/s；保持静置（剩余 " + left + " 秒）"
                    : "静止采集中…（剩余 " + left + " 秒）";
            }

            await Task.Delay(CalibrationRefreshMs, token);
        }

        OutputPadSession? current = session;
        bool ready = current?.GyroBiasReady == true &&
                     !string.Equals(current.GyroBiasSource, "saved", StringComparison.OrdinalIgnoreCase);
        if (ready)
        {
            // 学习完成时 GyroStick 已自动写入配置页。
            OnPropertyChanged(nameof(GyroBiasSavedText));
            OnPropertyChanged(nameof(GyroBiasCalibrationState));
            FinishCalibration(true,
                "陀螺仪零位校准完成并已保存：yaw=" + current!.GyroBiasYawDeg.ToString("F2") +
                " pitch=" + current.GyroBiasPitchDeg.ToString("F2") +
                " deg/s。下次连接直接复用，原生陀螺仪已消除漂移。");
        }
        else
        {
            FinishCalibration(false,
                "采样期间检测到手柄移动，零偏未提交，旧配置保持不变。请把手柄放稳后重新校准。");
        }
    }

    /// <summary>冒烟截图专用：不走硬件，直接把面板摆到指定状态，便于核对版式。</summary>
    public void ApplyCalibrationPreview(string kind, string state)
    {
        OpenCalibrationPanel(kind);
        CalibrationLive = "已连接 USB 有线，可以开始校准。";
        if (state == "running")
        {
            IsCalibrating = true;
            CalibrationSecondsLeft = 8;
            CalibrationProgress = kind == "range" ? 52 : 46;
            // 行程模式的提示文案：截图时用来核对它和秒数挤在一行会不会溢出
            if (kind == "range") CalibrationHint = "还差右摇杆";
        }
        else if (state == "done")
        {
            FinishCalibration(true, "零位校准完成并已保存：中心=(2048, 2048, 2048, 2048)。下次连接自动加载。");
        }
    }

    private void FinishCalibration(bool success, string message)
    {
        CalibrationSuccess = success;
        CalibrationResult = message;
        CalibrationFinished = true;
        CalibrationSecondsLeft = 0;
        CalibrationProgress = CalibrationTotalPercent;
        CalibrationLive = success ? "校准完成" : "校准未通过";
        AppendLog("[CAL_PANEL] result success=" + success + " " + message);
        RequestUserNotification(success ? "校准完成" : "校准未通过", message);
    }
}
