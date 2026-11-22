using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Pro2Plus;

/// <summary>
/// 陀螺仪映射高级设置：六个数值直接对应 gyro.json 的 gyro 段算法参数。
/// 编辑的是 Tuning 草稿，改动即时推给运行中的会话（gyroStick 与会话共用同一份 config 引用），
/// 点「保存并应用」统一落盘。有线 / 蓝牙共用同一份参数，行为一致。
/// </summary>
public sealed partial class MainViewModel
{
    // ── 满速 DPS：陀螺仪打满时对应的角速度，算法字段 full_speed_dps ──
    public double GyroFullSpeedDps
    {
        get { _ = Tuning; return Tuning.Gyro.FullSpeedDps; }
        set
        {
            _ = Tuning;
            double next = ViewModelFormatting.ClampRange(value, 20, 360, Tuning.Gyro.FullSpeedDps);
            if (Math.Abs(next - Tuning.Gyro.FullSpeedDps) < 1e-6) return;
            Tuning.Gyro.FullSpeedDps = next;
            CommitGyroTuning();
            OnPropertyChanged();
        }
    }

    // ── 水平倍率：yaw 轴输出缩放，算法字段 scale_yaw ──
    public double GyroScaleYaw
    {
        get { _ = Tuning; return Tuning.Gyro.ScaleYaw; }
        set
        {
            _ = Tuning;
            double next = ViewModelFormatting.ClampRange(value, 0.1, 5.0, Tuning.Gyro.ScaleYaw);
            if (Math.Abs(next - Tuning.Gyro.ScaleYaw) < 1e-6) return;
            Tuning.Gyro.ScaleYaw = next;
            CommitGyroTuning();
            OnPropertyChanged();
        }
    }

    // ── 竖直倍率：pitch 轴输出缩放，算法字段 scale_pitch ──
    public double GyroScalePitch
    {
        get { _ = Tuning; return Tuning.Gyro.ScalePitch; }
        set
        {
            _ = Tuning;
            double next = ViewModelFormatting.ClampRange(value, 0.1, 5.0, Tuning.Gyro.ScalePitch);
            if (Math.Abs(next - Tuning.Gyro.ScalePitch) < 1e-6) return;
            Tuning.Gyro.ScalePitch = next;
            CommitGyroTuning();
            OnPropertyChanged();
        }
    }

    // ── 下压倍率：仅放大「准星向下」的俯仰输出，算法字段 scale_pitch_down ──
    public double GyroScalePitchDown
    {
        get { _ = Tuning; return Tuning.Gyro.ScalePitchDown; }
        set
        {
            _ = Tuning;
            double next = ViewModelFormatting.ClampRange(value, 0.1, 5.0, Tuning.Gyro.ScalePitchDown);
            if (Math.Abs(next - Tuning.Gyro.ScalePitchDown) < 1e-6) return;
            Tuning.Gyro.ScalePitchDown = next;
            CommitGyroTuning();
            OnPropertyChanged();
        }
    }

    // ── 右摇杆死区：陀螺映射回中时的死区，算法字段 right_stick_deadzone ──
    public double GyroRightStickDeadzone
    {
        get { _ = Tuning; return Tuning.Gyro.RightStickDeadzone; }
        set
        {
            _ = Tuning;
            double next = ViewModelFormatting.ClampRange(value, 0, 0.3, Tuning.Gyro.RightStickDeadzone);
            if (Math.Abs(next - Tuning.Gyro.RightStickDeadzone) < 1e-6) return;
            Tuning.Gyro.RightStickDeadzone = next;
            CommitGyroTuning();
            OnPropertyChanged();
        }
    }

    // ── 陀螺仪开关：右摇杆陀螺映射的切换按键，算法字段 gyro_off_hold ──
    public string GyroToggleButton
    {
        get { _ = Tuning; return Tuning.Gyro.GyroToggleButton; }
        set
        {
            _ = Tuning;
            string next = string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();
            if (next == Tuning.Gyro.GyroToggleButton) return;
            Tuning.Gyro.GyroToggleButton = next;
            CommitGyroTuning();
            OnPropertyChanged();
        }
    }

    /// <summary>折叠条摘要：满速 / 水平 / 竖直三个关键值。</summary>
    public string GyroTuningSummary
    {
        get
        {
            _ = Tuning;
            return "满速 " + Tuning.Gyro.FullSpeedDps.ToString("F0") + " °/s · 水平 " +
                   Tuning.Gyro.ScaleYaw.ToString("F2") + " · 竖直 " +
                   Tuning.Gyro.ScalePitch.ToString("F2");
        }
    }

    private ActionCommand? testRumbleCommand;
    private ActionCommand? resetGyroTuningCommand;

    /// <summary>恢复默认：灵敏度、震动强度、六个算法参数回到定版默认值。</summary>
    public ICommand ResetGyroTuningCommand => resetGyroTuningCommand ??=
        new ActionCommand(_ => ResetGyroTuning());

    private void ResetGyroTuning()
    {
        _ = Tuning;
        GyroSection defaults = new();
        Tuning.Gyro.FullSpeedDps = defaults.FullSpeedDps;
        Tuning.Gyro.ScaleYaw = defaults.ScaleYaw;
        Tuning.Gyro.ScalePitch = defaults.ScalePitch;
        Tuning.Gyro.ScalePitchDown = defaults.ScalePitchDown;
        Tuning.Gyro.RightStickDeadzone = defaults.RightStickDeadzone;
        Tuning.Gyro.GyroToggleButton = defaults.GyroToggleButton;
        Tuning.Gyro.SensitivityScale = defaults.SensitivityScale;
        sensitivityScale = defaults.SensitivityScale;
        rumbleMultiplier = 0.6;
        userSettings.RumbleMultiplier = 0.6;
        ApplyPro2SlotRuntimeOptions();
        session?.ApplyGyroTuning(Tuning.Gyro);
        session?.ApplySensitivity(Tuning.Gyro.SensitivityScale);
        OnPropertyChanged(nameof(GyroFullSpeedDps));
        OnPropertyChanged(nameof(GyroScaleYaw));
        OnPropertyChanged(nameof(GyroScalePitch));
        OnPropertyChanged(nameof(GyroScalePitchDown));
        OnPropertyChanged(nameof(GyroRightStickDeadzone));
        OnPropertyChanged(nameof(GyroToggleButton));
        OnPropertyChanged(nameof(SensitivityScale));
        OnPropertyChanged(nameof(RumbleMultiplier));
        OnPropertyChanged(nameof(RumblePercent));
        OnPropertyChanged(nameof(GyroTuningSummary));
        TuningStatus = "已恢复默认参数，点「保存并应用」后写入配置。";
        AppendLog("[GYRO_TUNE] reset to defaults");
    }


    /// <summary>「试一下」：给已连接手柄发一帧短震动，验证强度设置对有线/蓝牙都生效。</summary>
    public ICommand TestRumbleCommand => testRumbleCommand ??=
        new ActionCommand(_ => _ = TestRumbleAsync());

    private async Task TestRumbleAsync()
    {
        IGamepadOutputSink? sink = LinkService.InputSession as IGamepadOutputSink
                                   ?? inputSource as IGamepadOutputSink;
        if (sink == null || !sink.IsOutputReady)
        {
            TuningStatus = "请先连接手柄并进入游戏，再试震动。";
            AppendLog("[RUMBLE_TEST] skipped: no output sink");
            return;
        }

        byte[] report = ViewModelFormatting.BuildTestRumbleReport();
        try
        {
            bool ok = sink.TryWriteOutputReport(report, out string error);
            if (!ok)
            {
                TuningStatus = "震动下发失败：" + error;
                AppendLog("[RUMBLE_TEST] failed: " + error);
                return;
            }
            TuningStatus = "已发送测试震动（受当前强度 " + RumbleMultiplier.ToString("F1") + "x 影响）。";
            AppendLog("[RUMBLE_TEST] sent gain=" + RumbleMultiplier.ToString("F2"));
        }
        catch (Exception ex)
        {
            TuningStatus = "震动下发异常：" + ex.Message;
            AppendLog("[RUMBLE_TEST] exception: " + ex.Message);
            return;
        }

        await Task.Delay(450);
        try
        {
            byte[] neutral = Pro2NativeRumbleEncoder.Neutralize(report);
            neutral[0] = 0x02;
            sink.TryWriteOutputReport(neutral, out _);
        }
        catch
        {
            // 断开瞬间忽略归零失败
        }
    }

    /// <summary>构造 64 字节 report 02：两个马达都用已知格式，填中等振幅。</summary>
    /// <summary>改动即时推给运行会话；落盘由「保存并应用」统一完成。</summary>
    private void CommitGyroTuning()
    {
        session?.ApplyGyroTuning(Tuning.Gyro);
        OnPropertyChanged(nameof(GyroTuningSummary));
        AppendLog("[GYRO_TUNE] full=" + Tuning.Gyro.FullSpeedDps.ToString("F1") +
                  " yaw=" + Tuning.Gyro.ScaleYaw.ToString("F2") +
                  " pitch=" + Tuning.Gyro.ScalePitch.ToString("F2") +
                  " down=" + Tuning.Gyro.ScalePitchDown.ToString("F2") +
                  " dz=" + Tuning.Gyro.RightStickDeadzone.ToString("F3") +
                  " toggle=" + Tuning.Gyro.GyroToggleButton);
    }
}
