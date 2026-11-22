using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Pro2Plus;

/// <summary>
/// 界面上的「按键映射 / 陀螺仪」两页的数据源。
/// 编辑的是 gyro.json 的一份草稿，点保存才落盘并推给正在运行的会话，
/// 所以有线、无线两种连接方式拿到的是同一份参数，行为完全一致。
/// </summary>
public sealed partial class MainViewModel
{
    private GyroConfig? tuningConfig;
    private List<KeyBindingRow>? keyBindingRows;
    private ActionCommand? saveTuningCommand;
    private ActionCommand? resetBindingsCommand;
    private ActionCommand? resetSensitivityCommand;
    private double sensitivityScale = 1.0;
    private bool tuningGyroEnabled = true;
    private bool tuningGyroStartEnabled = true;
    private string tuningStatus = "";

    private GyroConfig Tuning
    {
        get
        {
            if (tuningConfig != null) return tuningConfig;
            tuningConfig = GyroConfig.LoadOrDefault(out _);
            sensitivityScale = ViewModelFormatting.ClampScale(tuningConfig.Gyro.SensitivityScale);
            tuningConfig.Gyro.SensitivityScale = sensitivityScale;
            tuningGyroEnabled = tuningConfig.Gyro.Enabled;
            tuningGyroStartEnabled = tuningConfig.Gyro.StartEnabled;
            keyBindingRows = BuildKeyBindingRows(tuningConfig.KeyMouse);
            return tuningConfig;
        }
    }

    // ---------------- 陀螺仪 ----------------

    public double SensitivityScale
    {
        get { _ = Tuning; return sensitivityScale; }
        set
        {
            double next = ViewModelFormatting.ClampScale(value);
            if (Math.Abs(next - sensitivityScale) < 0.001) return;
            sensitivityScale = next;
            Tuning.Gyro.SensitivityScale = next;
            // 拖动即刻生效：两种模式都按 full_speed_dps 归一化，倍率天然同步。
            session?.ApplySensitivity(next);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SensitivityText));
            OnPropertyChanged(nameof(SensitivityDetail));
        }
    }

    public string SensitivityText
    {
        get { _ = Tuning; return "×" + sensitivityScale.ToString("F2"); }
    }

    public string SensitivityDetail
    {
        get
        {
            _ = Tuning;
            double full = Math.Max(Tuning.Gyro.FullSpeedDps / sensitivityScale, 1.0);
            return "满转约 " + full.ToString("F0") + " °/s · 手柄与键鼠同步变化";
        }
    }

    public bool TuningGyroEnabled
    {
        get { _ = Tuning; return tuningGyroEnabled; }
        set
        {
            if (tuningGyroEnabled == value) return;
            tuningGyroEnabled = value;
            Tuning.Gyro.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TuningRestartHint));
        }
    }

    public bool TuningGyroStartEnabled
    {
        get { _ = Tuning; return tuningGyroStartEnabled; }
        set
        {
            if (tuningGyroStartEnabled == value) return;
            tuningGyroStartEnabled = value;
            Tuning.Gyro.StartEnabled = value;
            OnPropertyChanged();
        }
    }

    public string TuningStatus
    {
        get => tuningStatus;
        private set { if (tuningStatus == value) return; tuningStatus = value; OnPropertyChanged(); }
    }

    public string TuningRestartHint => tuningGyroEnabled
        ? "陀螺仪已启用"
        : "陀螺仪已关闭 · 保存后重新连接生效";

    // ---------------- 按键映射 ----------------

    public IReadOnlyList<KeyBindingRow> KeyBindings
    {
        get { _ = Tuning; return keyBindingRows!; }
    }

    public ICommand SaveTuningCommand =>
        saveTuningCommand ??= new ActionCommand(_ => SaveTuning());

    public ICommand ResetBindingsCommand =>
        resetBindingsCommand ??= new ActionCommand(_ => ResetBindings());

    public ICommand ResetSensitivityCommand =>
        resetSensitivityCommand ??= new ActionCommand(_ => SensitivityScale = 1.0);

    private List<KeyBindingRow> BuildKeyBindingRows(KeyMouseSection section)
    {
        KeyMouseBindings b = section.Bindings ??= new KeyMouseBindings();
        return
        [
            new("面键 · 下（A 位）", "默认 Ctrl", "face_down", v => b.FaceDown = v, b.FaceDown),
            new("面键 · 右（B 位）", "默认 空格", "face_right", v => b.FaceRight = v, b.FaceRight),
            new("面键 · 上（Y 位）轻按", "默认 R · 换弹", "face_up_tap", v => b.FaceUpTap = v, b.FaceUpTap),
            new("面键 · 上（Y 位）长按", "默认 F · 连发互动", "face_up_hold", v => b.FaceUpHold = v, b.FaceUpHold),
            new("面键 · 左（X 位）单击", "默认 1", "face_left_tap", v => b.FaceLeftTap = v, b.FaceLeftTap),
            new("面键 · 左（X 位）双击", "默认 2", "face_left_double", v => b.FaceLeftDouble = v, b.FaceLeftDouble),
            new("面键 · 左（X 位）长按", "默认 3", "face_left_hold", v => b.FaceLeftHold = v, b.FaceLeftHold),
            new("左肩 L1", "默认 C", "left_shoulder", v => b.LeftShoulder = v, b.LeftShoulder),
            new("右肩 R1", "默认 E", "right_shoulder", v => b.RightShoulder = v, b.RightShoulder),
            new("L1 + R1 同时按", "默认 X", "shoulder_combo", v => b.ShoulderCombo = v, b.ShoulderCombo),
            new("十字 · 上", "默认 G", "dpad_up", v => b.DPadUp = v, b.DPadUp),
            new("十字 · 下", "默认 4", "dpad_down", v => b.DPadDown = v, b.DPadDown),
            new("十字 · 左", "默认 不输出", "dpad_left", v => b.DPadLeft = v, b.DPadLeft),
            new("十字 · 右", "默认 B", "dpad_right", v => b.DPadRight = v, b.DPadRight),
            new("右摇杆按下 R3", "默认 Q", "r3", v => b.R3 = v, b.R3),
            new("左摇杆按下 L3", "默认 不输出", "l3", v => b.L3 = v, b.L3),
            new("左背键", "默认 Shift", "paddle_left", v => b.PaddleLeft = v, b.PaddleLeft),
            new("右背键", "默认 Ctrl", "paddle_right", v => b.PaddleRight = v, b.PaddleRight),
            new("＋ 键", "默认 Esc", "plus", v => b.Plus = v, b.Plus),
            new("－ 键", "默认 不输出", "minus", v => b.Minus = v, b.Minus),
            new("左扳机 L2", "默认 鼠标右键", "l2", v => b.L2 = v, b.L2),
            new("右扳机 R2", "默认 鼠标左键", "r2", v => b.R2 = v, b.R2),
        ];
    }

    private void ResetBindings()
    {
        _ = Tuning;
        tuningConfig!.KeyMouse.Bindings = new KeyMouseBindings();
        keyBindingRows = BuildKeyBindingRows(tuningConfig.KeyMouse);
        OnPropertyChanged(nameof(KeyBindings));
        TuningStatus = "已恢复默认映射，点「保存并应用」后生效。";
    }

    private void SaveTuning()
    {
        _ = Tuning;
        if (tuningConfig!.TrySave(out string error))
        {
            session?.ApplyKeyMouse(tuningConfig.KeyMouse);
            session?.ApplySensitivity(tuningConfig.Gyro.SensitivityScale);
            session?.ApplyGyroTuning(tuningConfig.Gyro);
            TuningStatus = session == null
                ? "已保存。下次连接时生效。"
                : "已保存并立即生效（有线 / 无线同一套参数）。";
            AppendLog("[TUNING] 已保存 sensitivity=" + tuningConfig.Gyro.SensitivityScale.ToString("F2") +
                      " gyro=" + (tuningConfig.Gyro.Enabled ? "on" : "off") +
                      " bindings=" + (tuningConfig.KeyMouse.Bindings != null ? "custom" : "default"));
        }
        else
        {
            TuningStatus = "保存失败：" + error;
        }
    }
}

/// <summary>一行按键映射：左列是物理键，右列是输出动作。</summary>
public sealed class KeyBindingRow : INotifyPropertyChanged
{
    private readonly Action<string> apply;
    private string selected;

    public KeyBindingRow(string title, string hint, string key, Action<string> apply, string? initial)
    {
        Title = title;
        Hint = hint;
        Key = key;
        this.apply = apply;
        selected = string.IsNullOrWhiteSpace(initial) ? KeyMouseActions.None : initial;
    }

    public string Title { get; }
    public string Hint { get; }
    public string Key { get; }

    public IReadOnlyList<KeyMouseActionChoice> Choices => KeyMouseActions.Choices;

    public string Selected
    {
        get => selected;
        set
        {
            string next = string.IsNullOrWhiteSpace(value) ? KeyMouseActions.None : value;
            if (selected == next) return;
            selected = next;
            apply(next);
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
