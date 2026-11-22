using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Pro2Plus.Mapping;

namespace Pro2Plus;

/// <summary>
/// 键鼠模式的参数段，挂在 gyro.json 的 "keymouse" 下，与陀螺参数同一个文件。
/// 缺字段一律回落到这里的默认值，所以旧配置可以直接用。
/// </summary>
public sealed class KeyMouseSection
{
    // 右摇杆满推时每秒移动的鼠标计数。值越大转身越快。
    [JsonPropertyName("stick_turn_speed")] public double StickTurnSpeed { get; set; } = 7000;
    // 陀螺满速（full_speed_dps）时每秒移动的鼠标计数。用于常规瞄准，比摇杆稳。
    [JsonPropertyName("gyro_aim_speed")] public double GyroAimSpeed { get; set; } = 4200;
    // 陀螺输出的上限（单位：full_speed_dps 的倍数）。1.0 = 超过满速就削平，
    // 甩枪会「糊」；放大到 2.0 允许快甩时继续加速。摇杆不受此限制。
    [JsonPropertyName("gyro_out_cap")] public double GyroOutCap { get; set; } = 2.0;
    // Y 轴缩放。小于 1 表示上下更慢，便于压住头线。
    [JsonPropertyName("mouse_y_scale")] public double MouseYScale { get; set; } = 0.45;
    // 右摇杆死区。0 = 完全线性，推多少走多少。
    [JsonPropertyName("stick_deadzone")] public double StickDeadzone { get; set; } = 0.0;
    // 左摇杆 WASD 的触发阈值（和鼠标死区分开，免得 0 死区把走路搞得太灵）。
    [JsonPropertyName("move_threshold")] public double MoveThreshold { get; set; } = 0.25;
    // 指数曲线：低幅度更精细，高幅度更快。
    [JsonPropertyName("stick_exponent")] public double StickExponent { get; set; } = 1.8;
    // 线性/指数混合比。0 = 纯线性（最跟手），1 = 纯指数（低区最钝）。
    [JsonPropertyName("stick_curve_blend")] public double StickCurveBlend { get; set; } = 0.0;
    // 摇杆输出的一阶平滑时间常数（毫秒）。越小越跟手，越大越顺但延迟高。
    [JsonPropertyName("stick_smooth_ms")] public double StickSmoothMs { get; set; } = 5.0;
    // 面键（ABXY）按哪种物理排布绑定功能键。内部按键位次名就是物理位置：
    //   North = 上、West = 左、South = 下、East = 右。
    //   "xbox"（默认）：上 = Y（轻按 R / 长按 F）、左 = X（1 / 2 / 3）、
    //                  下 = A（Ctrl）、右 = B（空格）—— Xbox 布局。
    //   "switch"      ：上 = X（1 / 2 / 3）、左 = Y（R / F）、
    //                  右 = A（Ctrl）、下 = B（空格）—— Switch Pro 丝印布局。
    [JsonPropertyName("face_layout")] public string FaceLayout { get; set; } = "xbox";
    // X / Y 长短按的分界时间（毫秒）。
    [JsonPropertyName("long_press_ms")] public int LongPressMs { get; set; } = 280;
    // X 双击判定窗口：单击后等这么久没第二下，才真的发 1。
    [JsonPropertyName("double_tap_ms")] public int DoubleTapMs { get; set; } = 220;
    // 长按 Y 时 F 脉冲的重复间隔。
    [JsonPropertyName("pulse_repeat_ms")] public int PulseRepeatMs { get; set; } = 110;
    // 每个按键脉冲（1/2/3/R/F）按住的时长。
    [JsonPropertyName("pulse_width_ms")] public int PulseWidthMs { get; set; } = 28;
    [JsonPropertyName("gyro_deadzone")] public double GyroDeadzone { get; set; } = 0.02;
    // 左肩键/右肩键判定为组合键的等待窗口。
    [JsonPropertyName("combo_window_ms")] public double ComboWindowMs { get; set; } = 45;
    // 鼠标报告的发送间隔（毫秒）。4 ≈ 250Hz。
    [JsonPropertyName("mouse_report_ms")] public int MouseReportMs { get; set; } = 2;
    [JsonPropertyName("invert_mouse_y")] public bool InvertMouseY { get; set; } = true;
    // 左摇杆 WASD 的上下方向。项目内 Ry/Ly 增大 = 上推（XInput 口径），
    // 而 WASD 要先判断「下 = S」，所以默认也要翻一次。
    [JsonPropertyName("invert_move_y")] public bool InvertMoveY { get; set; } = true;
    // 左摇杆 W/A 的左右方向。正常（SDL 口径：原始值增大 = 右推）不用翻，
    // 留这个开关是为了万一某台手柄的 X 轴反过来时不用改代码。
    [JsonPropertyName("invert_move_x")] public bool InvertMoveX { get; set; } = false;
    // 右摇杆鼠标左右的转向方向，同样只在异常时用。
    [JsonPropertyName("invert_stick_x")] public bool InvertStickX { get; set; } = false;
    // 每个物理键输出什么，界面上可改。缺这段时用下面的默认值（即定版映射）。
    [JsonPropertyName("bindings")] public KeyMouseBindings? Bindings { get; set; }

    public KeyMouseSection Normalize()
    {
        StickTurnSpeed = Math.Clamp(StickTurnSpeed, 100, 20000);
        GyroAimSpeed = Math.Clamp(GyroAimSpeed, 50, 20000);
        GyroOutCap = Math.Clamp(GyroOutCap, 1.0, 3.0);
        MouseYScale = Math.Clamp(MouseYScale, 0.05, 4.0);
        StickDeadzone = Math.Clamp(StickDeadzone, 0.0, 0.9);
        MoveThreshold = Math.Clamp(MoveThreshold, 0.05, 0.95);
        StickExponent = Math.Clamp(StickExponent, 0.5, 5.0);
        StickCurveBlend = Math.Clamp(StickCurveBlend, 0.0, 1.0);
        StickSmoothMs = Math.Clamp(StickSmoothMs, 0.0, 60.0);
        LongPressMs = Math.Clamp(LongPressMs, 80, 1000);
        DoubleTapMs = Math.Clamp(DoubleTapMs, 80, 600);
        PulseRepeatMs = Math.Clamp(PulseRepeatMs, 30, 1000);
        PulseWidthMs = Math.Clamp(PulseWidthMs, 8, 300);
        GyroDeadzone = Math.Clamp(GyroDeadzone, 0.0, 0.5);
        ComboWindowMs = Math.Clamp(ComboWindowMs, 0, 400);
        MouseReportMs = Math.Clamp(MouseReportMs, 1, 50);
        FaceLayout = string.Equals((FaceLayout ?? "").Trim(), "switch", StringComparison.OrdinalIgnoreCase)
            ? "switch"
            : "xbox";
        Bindings ??= new KeyMouseBindings();
        return this;
    }

    /// <summary>深拷贝：UI 编辑草稿用，改坏了不影响正在跑的那份。</summary>
    public KeyMouseSection Clone()
    {
        KeyMouseSection copy = (KeyMouseSection)MemberwiseClone();
        copy.Bindings = (Bindings ?? new KeyMouseBindings()).Clone();
        return copy;
    }
}

/// <summary>
/// 键鼠模式的按键绑定表。字段名是物理位置，值是一个动作 id（见 <see cref="KeyMouseActions"/>）。
/// 默认值就是此前定版的映射，旧配置里没有 bindings 段时行为不变。
/// </summary>
public sealed class KeyMouseBindings
{
    [JsonPropertyName("face_down")] public string FaceDown { get; set; } = "mod:Ctrl";
    [JsonPropertyName("face_right")] public string FaceRight { get; set; } = "key:Space";
    [JsonPropertyName("face_up_tap")] public string FaceUpTap { get; set; } = "key:R";
    [JsonPropertyName("face_up_hold")] public string FaceUpHold { get; set; } = "key:F";
    [JsonPropertyName("face_left_tap")] public string FaceLeftTap { get; set; } = "key:1";
    [JsonPropertyName("face_left_double")] public string FaceLeftDouble { get; set; } = "key:2";
    [JsonPropertyName("face_left_hold")] public string FaceLeftHold { get; set; } = "key:3";
    [JsonPropertyName("left_shoulder")] public string LeftShoulder { get; set; } = "key:C";
    [JsonPropertyName("right_shoulder")] public string RightShoulder { get; set; } = "key:E";
    [JsonPropertyName("shoulder_combo")] public string ShoulderCombo { get; set; } = "key:X";
    [JsonPropertyName("dpad_up")] public string DPadUp { get; set; } = "key:G";
    [JsonPropertyName("dpad_down")] public string DPadDown { get; set; } = "key:4";
    [JsonPropertyName("dpad_left")] public string DPadLeft { get; set; } = "none";
    [JsonPropertyName("dpad_right")] public string DPadRight { get; set; } = "key:B";
    [JsonPropertyName("r3")] public string R3 { get; set; } = "key:Q";
    [JsonPropertyName("l3")] public string L3 { get; set; } = "none";
    [JsonPropertyName("paddle_left")] public string PaddleLeft { get; set; } = "mod:Shift";
    [JsonPropertyName("paddle_right")] public string PaddleRight { get; set; } = "mod:Ctrl";
    [JsonPropertyName("plus")] public string Plus { get; set; } = "key:Esc";
    [JsonPropertyName("minus")] public string Minus { get; set; } = "none";
    [JsonPropertyName("l2")] public string L2 { get; set; } = "mouse:right";
    [JsonPropertyName("r2")] public string R2 { get; set; } = "mouse:left";

    public KeyMouseBindings Clone() => (KeyMouseBindings)MemberwiseClone();
}

public enum KeyMouseActionKind
{
    None,
    Key,
    Modifier,
    MouseButton,
    Wheel
}

public readonly struct KeyMouseAction
{
    public KeyMouseAction(KeyMouseActionKind kind, byte code = 0, int wheel = 0)
    {
        Kind = kind;
        Code = code;
        Wheel = wheel;
    }

    public KeyMouseActionKind Kind { get; }
    public byte Code { get; }
    public int Wheel { get; }
}

public sealed record KeyMouseActionChoice(string Id, string Label);

/// <summary>
/// 动作 id 的解析表。格式 "<type>:<name>"：
///   none           不输出
///   key:Space      键盘按键
///   mod:Ctrl       修饰键（可以和普通键同时按）
///   mouse:left     鼠标按键（按住期间持续）
///   wheel:down     滚轮（按下瞬间滚一格）
/// </summary>
public static class KeyMouseActions
{
    public const string None = "none";

    private static readonly Dictionary<string, KeyMouseAction> Lookup =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<KeyMouseActionChoice> Choices { get; } = Build();

    private static IReadOnlyList<KeyMouseActionChoice> Build()
    {
        var choices = new List<KeyMouseActionChoice> { new(None, "不输出") };

        void Add(string id, string label, KeyMouseActionKind kind, byte code, int wheel = 0)
        {
            Lookup[id] = new KeyMouseAction(kind, code, wheel);
            choices.Add(new KeyMouseActionChoice(id, label));
        }

        Add("mod:Ctrl", "Ctrl", KeyMouseActionKind.Modifier, HidModifier.LeftCtrl);
        Add("mod:Shift", "Shift", KeyMouseActionKind.Modifier, HidModifier.LeftShift);
        Add("mod:Alt", "Alt", KeyMouseActionKind.Modifier, 0x04);

        Add("mouse:left", "鼠标左键", KeyMouseActionKind.MouseButton, HidMouseButton.Left);
        Add("mouse:right", "鼠标右键", KeyMouseActionKind.MouseButton, HidMouseButton.Right);
        Add("mouse:middle", "鼠标中键", KeyMouseActionKind.MouseButton, 0x04);

        Add("wheel:up", "滚轮上", KeyMouseActionKind.Wheel, 0, 1);
        Add("wheel:down", "滚轮下", KeyMouseActionKind.Wheel, 0, -1);

        Add("key:Space", "空格", KeyMouseActionKind.Key, 0x2C);
        Add("key:Esc", "Esc", KeyMouseActionKind.Key, 0x29);
        Add("key:Tab", "Tab", KeyMouseActionKind.Key, 0x2B);
        Add("key:Enter", "回车", KeyMouseActionKind.Key, 0x28);
        Add("key:Backspace", "退格", KeyMouseActionKind.Key, 0x2A);

        string[] digits = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" };
        for (int i = 0; i < digits.Length; i++)
            Add("key:" + digits[i], digits[i], KeyMouseActionKind.Key, (byte)(0x1E + i));

        string[] letters = { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
                             "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z" };
        for (int i = 0; i < letters.Length; i++)
            Add("key:" + letters[i], letters[i], KeyMouseActionKind.Key, (byte)(0x04 + i));

        Add("key:Up", "方向键上", KeyMouseActionKind.Key, 0x52);
        Add("key:Down", "方向键下", KeyMouseActionKind.Key, 0x51);
        Add("key:Left", "方向键左", KeyMouseActionKind.Key, 0x50);
        Add("key:Right", "方向键右", KeyMouseActionKind.Key, 0x4F);

        for (int i = 1; i <= 12; i++)
            Add("key:F" + i, "F" + i, KeyMouseActionKind.Key, (byte)(0x39 + i));

        return choices;
    }

    public static KeyMouseAction Parse(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return default;
        return Lookup.TryGetValue(id.Trim(), out KeyMouseAction action) ? action : default;
    }

    /// <summary>给界面用：动作 id 对应的可读名称，未知 id 返回「不输出」。</summary>
    public static string LabelOf(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "不输出";
        foreach (KeyMouseActionChoice choice in Choices)
            if (string.Equals(choice.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) return choice.Label;
        return "不输出";
    }
}

/// <summary>USB HID Keyboard/Keypad usage codes（仅收录本映射用到的）。</summary>
public static class HidKey
{
    public const byte A = 0x04;
    public const byte B = 0x05;
    public const byte C = 0x06;
    public const byte D = 0x07;
    public const byte E = 0x08;
    public const byte F = 0x09;
    public const byte G = 0x0A;
    public const byte Q = 0x14;
    public const byte R = 0x15;
    public const byte S = 0x16;
    public const byte W = 0x1A;
    public const byte X = 0x1B;
    public const byte Digit1 = 0x1E;
    public const byte Digit2 = 0x1F;
    public const byte Digit3 = 0x20;
    public const byte Digit4 = 0x21;
    public const byte Space = 0x2C;
    public const byte Escape = 0x29;
}

/// <summary>HID keyboard modifier 位。</summary>
public static class HidModifier
{
    public const byte LeftCtrl = 0x01;
    public const byte LeftShift = 0x02;
}

/// <summary>HID mouse 按键位。</summary>
public static class HidMouseButton
{
    public const byte Left = 0x01;
    public const byte Right = 0x02;
}

/// <summary>
/// 键鼠映射：把 GamepadState 翻译成键盘报告和鼠标报告。
///
/// 按键对应关系（按物理位置；内部位次 North=上、West=左、South=下、East=右）：
///   左摇杆 → W/A/S/D            右摇杆 + 陀螺 → 鼠标移动
///   左肩 L1 → C                 右肩 R1 → E          L1+R1 → X
///   A（物理下方）→ Ctrl         右背键 → Ctrl        B（物理右方）→ 空格
///   X（物理左方）：单击 = 1，双击 = 2，长按 = 3
///   Y（物理上方）：轻按 = R（换弹），按住 = F 脉冲（互动）
///   这四颗面键的物理排布用 face_layout 选择："xbox"（默认）或 "switch"。
///   十字上 → G                  十字下 → 4          十字右 → B
///   左扳机 L2 → 鼠标右键         右扳机 R2 → 鼠标左键
///   右摇杆按下 → Q              左背键 → Shift
/// </summary>
public sealed class KeyMouseMapper
{
    private KeyMouseSection options;
    private readonly HashSet<byte> keys = new();
    private readonly HashSet<byte> previousKeys = new();
    private byte currentModifiers;
    private byte sentModifiers;
    private byte mouseButtons;
    private double wheelAccumulator;
    private double residualX, residualY;
    private double smoothedStickX, smoothedStickY;
    private long lastUpdateMs;
    private long lastMouseSendMs;

    private double pressThreshold;

    public KeyMouseMapper(KeyMouseSection? section = null)
    {
        options = (section ?? new KeyMouseSection()).Normalize();
        // WASD 是开关量，用一个独立阈值，避免右摇杆的 0 死区把走路搞得太灵。
        pressThreshold = Math.Clamp(options.MoveThreshold, 0.05, 0.95);
    }

    /// <summary>运行时替换参数。界面点「保存」后立刻生效，不用断开重连。</summary>
    public void ApplyOptions(KeyMouseSection section)
    {
        options = (section ?? new KeyMouseSection()).Normalize();
        pressThreshold = Math.Clamp(options.MoveThreshold, 0.05, 0.95);
        Reset();
    }

    public KeyMouseSection Options => options;

    public byte Modifiers => currentModifiers;

    public byte MouseButtons => mouseButtons;

    public int PressedKeyCount => keys.Count;

    public void Reset()
    {
        keys.Clear();
        previousKeys.Clear();
        currentModifiers = 0;
        sentModifiers = 0;
        mouseButtons = 0;
        wheelAccumulator = 0;
        residualX = residualY = 0;
        smoothedStickX = smoothedStickY = 0;
        lastUpdateMs = 0;
        lastMouseSendMs = 0;
    }

    /// <summary>
    /// 用一帧手柄状态更新输出状态。
    /// <paramref name="gyroUnitX"/>/<paramref name="gyroUnitY"/> 是纯陀螺分量（-1..1），
    /// <paramref name="physicalRx"/>/<paramref name="physicalRy"/> 是物理右摇杆原始值（-1..1）。
    /// 两者必须分开传，否则摇杆和陀螺的灵敏度会互相污染。
    /// </summary>
    public void Update(
        GamepadState state,
        double gyroUnitX,
        double gyroUnitY,
        double physicalRx,
        double physicalRy,
        long nowMs)
    {
        double dtSeconds = lastUpdateMs == 0
            ? 0.0
            : Math.Clamp((nowMs - lastUpdateMs) / 1000.0, 0.0, 0.1);
        lastUpdateMs = nowMs;

        keys.Clear();

        // ---------- 按键：交给仲裁器按「轻按 / 双击 / 长按」出键 ----------
        // 每个源键三个触发位，各挂一个目标；只在需要区分时才给轻按加延时。
        KbmMappingEngine.Shared.Update(state, nowMs);
        foreach (byte key in KbmMappingEngine.Shared.Keys) keys.Add(key);
        byte mods = KbmMappingEngine.Shared.Modifiers;
        byte buttons = KbmMappingEngine.Shared.MouseButtons;
        wheelAccumulator += KbmMappingEngine.Shared.TakeWheel();

        // ---------- 左摇杆 → WASD ----------
        // 项目内轴值增大 = 上推（XInput 口径），翻一次后 ly 增大才是「向下」。
        double lx = AxisToUnit(state.Lx);
        double ly = AxisToUnit(state.Ly);
        if (options.InvertMoveX) lx = -lx;
        if (options.InvertMoveY) ly = -ly;
        if (ly >= pressThreshold) keys.Add(HidKey.S);
        else if (ly <= -pressThreshold) keys.Add(HidKey.W);
        if (lx >= pressThreshold) keys.Add(HidKey.D);
        else if (lx <= -pressThreshold) keys.Add(HidKey.A);

        currentModifiers = mods;
        mouseButtons = buttons;

        // ---------- 鼠标移动（只吃右摇杆，左摇杆已经在上面变成 WASD 了） ----------
        // 键鼠层读的是解析器输出，解析器的死区裁不干净时会漏残差过来；
        // 设置页的「摇杆死区」必须同时作用到这里，否则用户设了 1% 仍然漂。
        // 取两层里的较大值：gyro.json 自己的死区仍然可用（更钝的手感）。
        lastDeadzone = Math.Max(
            options.StickDeadzone,
            Pro2GyroBiasStore.ActiveStickDeadzone / (double)GamepadState.AxisCenter);
        double stickTargetX = ShapeStick(physicalRx, options, lastDeadzone);
        double stickTargetY = ShapeStick(physicalRy, options, lastDeadzone);
        if (options.InvertStickX) stickTargetX = -stickTargetX;

        // 一阶低通：摇杆源只有 133Hz，直接送 500Hz 鼠标会一格一格；
        // 只平滑摇杆，陀螺保持零延迟。
        double follow = 1.0;
        if (options.StickSmoothMs > 0.0 && dtSeconds > 0.0)
            follow = 1.0 - Math.Exp(-dtSeconds * 1000.0 / options.StickSmoothMs);
        smoothedStickX += (stickTargetX - smoothedStickX) * follow;
        smoothedStickY += (stickTargetY - smoothedStickY) * follow;
        // 回中时直接掐掉残量，避免松杆后还有一点惯性拖尾
        if (stickTargetX == 0.0 && Math.Abs(smoothedStickX) < 0.004) smoothedStickX = 0.0;
        if (stickTargetY == 0.0 && Math.Abs(smoothedStickY) < 0.004) smoothedStickY = 0.0;

        lastGyroUnitX = gyroUnitX;
        lastGyroUnitY = gyroUnitY;
        double gyroX = Shape(gyroUnitX, options.GyroDeadzone, 1.0, options.GyroOutCap);
        double gyroY = Shape(gyroUnitY, options.GyroDeadzone, 1.0, options.GyroOutCap);

        double velocityX = smoothedStickX * options.StickTurnSpeed + gyroX * options.GyroAimSpeed;
        double velocityY = (smoothedStickY * options.StickTurnSpeed + gyroY * options.GyroAimSpeed) * options.MouseYScale;
        if (options.InvertMouseY) velocityY = -velocityY;

        residualX += velocityX * dtSeconds;
        residualY += velocityY * dtSeconds;
    }

    /// <summary>
    /// 诊断用：把鼠标速度的每一个来源摊开。判断「静止时还在漂」时看它：
    /// stick=物理右摇杆、smooth=平滑后的值、vel=最终速度、res=还没发出的亚像素残量。
    /// 静止时 stick 和 vel 都必须是精确的 0，只要有一个非 0 就能定位到是哪一层。
    /// </summary>
    public string MouseTelemetry =>
        "smooth=(" + smoothedStickX.ToString("F6") + "," + smoothedStickY.ToString("F6") + ")" +
        " vel=(" + (smoothedStickX * options.StickTurnSpeed).ToString("F3") + "," +
                   (smoothedStickY * options.StickTurnSpeed).ToString("F3") + ")" +
        " gyro=(" + (lastGyroUnitX * options.GyroAimSpeed).ToString("F3") + "," +
                    (lastGyroUnitY * options.GyroAimSpeed).ToString("F3") + ")" +
        " residual=(" + residualX.ToString("F4") + "," + residualY.ToString("F4") + ")" +
        " dz=" + lastDeadzone.ToString("F3") +
        " smoothMs=" + options.StickSmoothMs.ToString("F1");

    private double lastGyroUnitX, lastGyroUnitY;

    /// <summary>取出本帧应发送的鼠标位移（扣掉已发出的整数部分，保留小数继续累积）。</summary>
    public (int Dx, int Dy) TakeMouseDelta(long nowMs, out bool due)
    {
        due = lastMouseSendMs == 0 || nowMs - lastMouseSendMs >= options.MouseReportMs;
        if (!due) return (0, 0);
        lastMouseSendMs = nowMs;
        int dx = (int)Math.Truncate(residualX);
        int dy = (int)Math.Truncate(residualY);
        residualX -= dx;
        residualY -= dy;
        return (dx, dy);
    }

    public int TakeWheel()
    {
        int wheel = (int)Math.Truncate(wheelAccumulator);
        wheelAccumulator -= wheel;
        return wheel;
    }

    /// <summary>键盘状态相比上一帧是否变化（只有变化时才发报告）。</summary>
    public bool ConsumeKeyboardChanged()
    {
        bool changed = sentModifiers != currentModifiers;
        if (previousKeys.Count != keys.Count)
        {
            changed = true;
        }
        else
        {
            foreach (byte key in keys)
            {
                if (!previousKeys.Contains(key)) { changed = true; break; }
            }
        }

        if (!changed) return false;
        sentModifiers = currentModifiers;
        previousKeys.Clear();
        foreach (byte key in keys) previousKeys.Add(key);
        return true;
    }

    /// <summary>
    /// 构造 VIIPER 键盘流的变长包：[modifiers][keyCount][HID usage...]。
    /// 注意这不是 HID 的 34 字节位图报告——服务端按 count 逐字节读键码，
    /// 多发一个 reserved 字节会让 count 变成 0，所有普通键都会被丢掉。
    /// </summary>
    public byte[] BuildKeyboardReport()
    {
        int count = 0;
        foreach (byte key in keys) if (key != 0) count++;

        byte[] report = new byte[2 + count];
        report[0] = sentModifiers;
        report[1] = (byte)count;
        int index = 2;
        foreach (byte key in keys)
        {
            if (key == 0) continue;
            report[index++] = key;
        }

        return report;
    }

    /// <summary>构造 9 字节 HID 鼠标报告（buttons + dx + dy + wheel + pan，小端）。</summary>
    public static byte[] BuildMouseReport(byte buttons, int dx, int dy, int wheel, int pan = 0)
    {
        byte[] report = new byte[9];
        report[0] = (byte)(buttons & 0x1F);
        short sdx = (short)Math.Clamp(dx, short.MinValue, short.MaxValue);
        short sdy = (short)Math.Clamp(dy, short.MinValue, short.MaxValue);
        short swh = (short)Math.Clamp(wheel, short.MinValue, short.MaxValue);
        short span = (short)Math.Clamp(pan, short.MinValue, short.MaxValue);
        report[1] = (byte)sdx;
        report[2] = (byte)(sdx >> 8);
        report[3] = (byte)sdy;
        report[4] = (byte)(sdy >> 8);
        report[5] = (byte)swh;
        report[6] = (byte)(swh >> 8);
        report[7] = (byte)span;
        report[8] = (byte)(span >> 8);
        return report;
    }

    public static double AxisToUnit(ushort value) =>
        Math.Clamp((value - GamepadState.AxisCenter) / 2047.0, -1.0, 1.0);

    /// <summary>
    /// 死区 + 指数曲线。死区内输出 0，之外按幂函数放大。
    /// maxOut 是输出上限：摇杆用 1.0（推到边就是满速），
    /// 陀螺传更大的值，让快甩超过 full_speed_dps 时还能继续加速，而不是被削平。
    /// </summary>
    private static double Shape(double value, double deadzone, double exponent, double maxOut = 1.0)
    {
        double magnitude = Math.Abs(value);
        if (magnitude <= deadzone) return 0.0;
        double normalized = Math.Clamp((magnitude - deadzone) / Math.Max(1e-6, 1.0 - deadzone),
            0.0, maxOut);
        return Math.Sign(value) * Math.Pow(normalized, exponent);
    }

    /// <summary>
    /// 右摇杆曲线：线性与指数按比例混合。
    /// 纯指数（blend=1）在死区边缘输出接近 0，手感是「要么不动要么猛冲」；
    /// 混一半线性后小幅度也有响应，同时保留大幅度的加速段。
    /// 出死区立刻给一点最小速度，避免刚好越过死区时卡住不动。
    /// </summary>
    private double lastDeadzone;

    private static double ShapeStick(double value, KeyMouseSection opt, double deadzone)
    {
        double magnitude = Math.Abs(value);
        if (magnitude <= deadzone) return 0.0;

        double t = Math.Clamp((magnitude - deadzone) / Math.Max(1e-6, 1.0 - deadzone),
            0.0, 1.0);
        double shaped = (1.0 - opt.StickCurveBlend) * t
                        + opt.StickCurveBlend * Math.Pow(t, opt.StickExponent);
        // 只有存在死区时才补最小输出（用来跨过死区边缘）；零死区下补底会把微抖放大。
        if (deadzone > 0.0)
        {
            const double MinOutput = 0.02;
            if (shaped < MinOutput) shaped = MinOutput;
        }
        return Math.Sign(value) * shaped;
    }
}
