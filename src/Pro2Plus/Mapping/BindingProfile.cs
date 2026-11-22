using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pro2Plus.Mapping;

/// <summary>触发方式。轻按 / 长按 / 双击共用同一个源键，各自可以挂不同目标。</summary>
public enum BindingTrigger
{
    Tap = 0,
    Long = 1,
    Double = 2,
    /// <summary>组合键：与另一颗键在判定窗口内一起按下时触发（搭子存在 KeyBinding.Partner）。</summary>
    Combo = 3
}

public enum BindingTargetKind
{
    None = 0,
    /// <summary>手柄上的逻辑键（A/B/X/Y/L/R/ZL/ZR/L3/R3/十字/＋－/GL/GR/截图/Home）。</summary>
    PadButton = 1,
    /// <summary>键盘按键，值用 <see cref="System.Windows.Input.Key"/> 的名字。</summary>
    Keyboard = 2,
    /// <summary>鼠标轴，值是 "X" / "Y" / "Wheel"。</summary>
    MouseAxis = 3,
    /// <summary>鼠标键，值是 "Left" / "Right" / "Middle" / "X1" / "X2"。</summary>
    MouseButton = 4
}

/// <summary>一个映射目标。Code 是稳定标识，界面文案由 <see cref="BindingLabels"/> 按模式翻译。</summary>
public readonly record struct BindingTarget(BindingTargetKind Kind, string Code)
{
    public static BindingTarget None => new(BindingTargetKind.None, "");

    public bool IsNone => Kind == BindingTargetKind.None;

    public static BindingTarget Pad(string code) => new(BindingTargetKind.PadButton, code);
    public static BindingTarget Key(string code) => new(BindingTargetKind.Keyboard, code);
    public static BindingTarget Mouse(string axis) => new(BindingTargetKind.MouseAxis, axis);
    public static BindingTarget MouseBtn(string button) => new(BindingTargetKind.MouseButton, button);

    public override string ToString() => Kind + ":" + Code;
}

/// <summary>源键的规范名字。界面上用中文，存储也用这些名字（不做二次映射）。</summary>
public static class SourceKeys
{
    public const string LeftStick = "左摇杆";
    public const string RightStick = "右摇杆";
    public const string Gyro = "陀螺仪";

    /// <summary>可以独立映射的按键（不含摇杆/陀螺仪这类模拟量）。</summary>
    public static readonly string[] Buttons =
    [
        "A", "B", "X", "Y", "↑", "↓", "←", "→",
        "L", "R", "ZL", "ZR", "L3", "R3", "＋", "－",
        "截图", "Home", "GL", "GR"
    ];

    public static readonly string[] Analog = [LeftStick, RightStick, Gyro];

    public static readonly string[] All = [.. Buttons, .. Analog];

    public static bool IsAnalog(string source) => Array.IndexOf(Analog, source) >= 0;
}

/// <summary>手柄逻辑键（与物理布局无关）。</summary>
public static class PadButtons
{
    public const string A = "PadA";
    public const string B = "PadB";
    public const string X = "PadX";
    public const string Y = "PadY";
    public const string L = "PadL";
    public const string R = "PadR";
    public const string ZL = "PadZL";
    public const string ZR = "PadZR";
    public const string L3 = "PadL3";
    public const string R3 = "PadR3";
    public const string Plus = "PadPlus";
    public const string Minus = "PadMinus";
    public const string Up = "PadUp";
    public const string Down = "PadDown";
    public const string Left = "PadLeft";
    public const string Right = "PadRight";
    public const string GL = "PadGL";
    public const string GR = "PadGR";
    public const string Capture = "PadCapture";
    public const string Home = "PadHome";
    public const string Touchpad = "PadTouchpad";

    public static readonly string[] All =
    [
        A, B, X, Y, L, R, ZL, ZR, L3, R3, Plus, Minus,
        Up, Down, Left, Right, Capture, Home, Touchpad
    ];
}

    /// <summary>按输出模式把逻辑键翻成用户看到的字面。</summary>
    public static class BindingLabels
    {
        /// <summary>
        /// 三平台按键命名对照（肩键 / 扳机 / 摇杆按下各家叫法不同）：
        /// 逻辑键       Switch(NS)  Xbox   PS5
        /// 肩键 L/R       L / R      LB/RB  L1/R1
        /// 扳机 ZL/ZR     ZL / ZR    LT/RT  L2/R2
        /// 摇杆按下       L3 / R3    LS/RS  L3/R3
        /// </summary>
        public static string Pad(string code, string layout)
        {
            bool ps5 = layout == "ps5";
            bool xbox = layout == "xbox";
            return code switch
            {
                PadButtons.A => ps5 ? "×" : "A",
                PadButtons.B => ps5 ? "○" : "B",
                PadButtons.X => ps5 ? "□" : "X",
                PadButtons.Y => ps5 ? "△" : "Y",
                PadButtons.L => xbox ? "LB" : ps5 ? "L1" : "L",
                PadButtons.R => xbox ? "RB" : ps5 ? "R1" : "R",
                PadButtons.ZL => xbox ? "LT" : ps5 ? "L2" : "ZL",
                PadButtons.ZR => xbox ? "RT" : ps5 ? "R2" : "ZR",
                PadButtons.L3 => xbox ? "LS" : "L3",
                PadButtons.R3 => xbox ? "RS" : "R3",
                PadButtons.Plus => ps5 ? "选项" : (xbox ? "菜单" : "＋"),
                PadButtons.Minus => ps5 ? "创建" : (xbox ? "视图" : "－"),
                PadButtons.Up => "↑",
                PadButtons.Down => "↓",
                PadButtons.Left => "←",
                PadButtons.Right => "→",
                PadButtons.GL => "GL",
                PadButtons.GR => "GR",
                PadButtons.Capture => "截图",
                PadButtons.Home => ps5 ? "PS" : "Home",
                PadButtons.Touchpad => "触摸板",
                _ => code
            };
        }

    public static string Trigger(BindingTrigger trigger) => trigger switch
    {
        BindingTrigger.Long => "长按",
        BindingTrigger.Double => "双击",
        BindingTrigger.Combo => "组合",
        _ => "轻按"
    };

    public static string Target(BindingTarget target, string layout)
    {
        return target.Kind switch
        {
            BindingTargetKind.None => "未映射",
            BindingTargetKind.PadButton => "手柄 " + Pad(target.Code, layout),
            BindingTargetKind.Keyboard => "键盘 " + KeyboardName(target.Code),
            BindingTargetKind.MouseAxis => target.Code switch
            {
                "Wheel" or "WheelUp" => "鼠标滚轮上",
                "WheelDown" => "鼠标滚轮下",
                "X" => "鼠标左右",
                "Y" => "鼠标上下",
                _ => "鼠标 " + target.Code
            },
            BindingTargetKind.MouseButton => "鼠标 " + target.Code,
            _ => target.Code
        };
    }

    /// <summary>把 WPF 的 Key 名翻成键帽上的字。</summary>
    public static string KeyboardName(string code) => code switch
    {
        "Space" => "空格",
        "Return" => "Enter",
        "Back" => "⌫",
        "Escape" => "Esc",
        "LeftShift" or "RightShift" => "Shift",
        "LeftCtrl" or "RightCtrl" => "Ctrl",
        "LeftAlt" or "RightAlt" => "Alt",
        "OemMinus" => "-",
        "OemPlus" => "=",
        "OemOpenBrackets" => "[",
        "OemCloseBrackets" => "]",
        "OemPipe" => "\\",
        "OemSemicolon" => ";",
        "OemQuotes" => "'",
        "OemComma" => ",",
        "OemPeriod" => ".",
        "OemQuestion" => "/",
        "OemTilde" => "`",
        "Up" => "↑",
        "Down" => "↓",
        "Left" => "←",
        "Right" => "→",
        _ => code
    };
}

/// <summary>一条绑定：某个源键在某种触发方式下映射到什么。</summary>
public sealed class KeyBinding
{
    public BindingTrigger Trigger { get; set; } = BindingTrigger.Tap;
    public BindingTargetKind Kind { get; set; } = BindingTargetKind.None;
    public string Code { get; set; } = "";

    /// <summary>组合键的搭子（只有 Trigger = Combo 时有意义）。</summary>
    public string Partner { get; set; } = "";

    [JsonIgnore]
    public BindingTarget Target => new(Kind, Code);
    public static KeyBinding To(BindingTarget target, BindingTrigger trigger = BindingTrigger.Tap) =>
        new() { Trigger = trigger, Kind = target.Kind, Code = target.Code };
}

/// <summary>一份完整的映射表。按键 → 三条（轻按/长按/双击）。</summary>
public sealed class MappingProfile
{
    public Dictionary<string, List<KeyBinding>> Bindings { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 手柄的 ABXY 物理丝印布局（对应映射页那颗开关）：
    ///   "xbox"：上 Y / 左 X / 下 A / 右 B；
    ///   "pro2"：上 X / 左 Y / 右 A / 下 B（Switch Pro 同款）。
    /// 空 = 没设置过，按输出平台取默认（Xbox 输出用 xbox，Pro2/NS 输出用 pro2）。
    /// 换布局的意义是「按手指按住的那颗丝印发键」，所以按位置反着查盘面。
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("face_layout")]
    public string? FaceLayout { get; set; }

    /// <summary>取某个源键在指定触发方式下的目标；没有就是未映射。</summary>
    public BindingTarget Get(string source, BindingTrigger trigger)
    {
        if (!Bindings.TryGetValue(source, out List<KeyBinding>? list)) return BindingTarget.None;
        KeyBinding? hit = list.FirstOrDefault(b => b.Trigger == trigger);
        return hit is null ? BindingTarget.None : hit.Target;
    }

    public void Set(string source, BindingTrigger trigger, BindingTarget target)
    {
        if (!Bindings.TryGetValue(source, out List<KeyBinding>? list))
        {
            list = new List<KeyBinding>();
            Bindings[source] = list;
        }

        KeyBinding? existing = list.FirstOrDefault(b => b.Trigger == trigger);
        if (target.IsNone)
        {
            if (existing is not null) list.Remove(existing);
            if (list.Count == 0) Bindings.Remove(source);
            return;
        }

        if (existing is null) list.Add(KeyBinding.To(target, trigger));
        else
        {
            existing.Kind = target.Kind;
            existing.Code = target.Code;
        }
    }

    /// <summary>取组合键：目标 + 搭子。没配就是空。</summary>
    public (BindingTarget Target, string Partner) GetCombo(string source)
    {
        if (!Bindings.TryGetValue(source, out List<KeyBinding>? list)) return (BindingTarget.None, "");
        KeyBinding? hit = list.FirstOrDefault(b => b.Trigger == BindingTrigger.Combo);
        return hit is null ? (BindingTarget.None, "") : (hit.Target, hit.Partner ?? "");
    }

    /// <summary>写组合键。target 为空同时清掉搭子。</summary>
    public void SetCombo(string source, BindingTarget target, string partner)
    {
        if (!Bindings.TryGetValue(source, out List<KeyBinding>? list))
        {
            list = new List<KeyBinding>();
            Bindings[source] = list;
        }

        KeyBinding? existing = list.FirstOrDefault(b => b.Trigger == BindingTrigger.Combo);
        if (target.IsNone)
        {
            if (existing is not null) list.Remove(existing);
            if (list.Count == 0) Bindings.Remove(source);
            return;
        }

        if (existing is null) list.Add(new KeyBinding { Trigger = BindingTrigger.Combo, Kind = target.Kind, Code = target.Code, Partner = partner });
        else
        {
            existing.Kind = target.Kind;
            existing.Code = target.Code;
            if (!string.IsNullOrEmpty(partner)) existing.Partner = partner;
        }
    }

    /// <summary>某个源键是否配置过任何触发方式。</summary>
    public bool HasAny(string source) =>
        Bindings.TryGetValue(source, out List<KeyBinding>? list) && list.Count > 0;

    public bool IsBound(string source, BindingTrigger trigger) => !Get(source, trigger).IsNone;

    /// <summary>轻按是否已绑定。双击 / 长按只有在轻按占用之后才参与判定。</summary>
    public bool IsTapBound(string source) => IsBound(source, BindingTrigger.Tap);

    /// <summary>这张表里是否有输出到手柄的映射（键鼠模式据此决定要不要附加虚拟手柄）。</summary>
    public bool HasPadTarget()
    {
        foreach (KeyValuePair<string, List<KeyBinding>> pair in Bindings)
        {
            foreach (KeyBinding binding in pair.Value)
            {
                if (binding.Kind == BindingTargetKind.PadButton) return true;
            }
        }

        return false;
    }

    /// <summary>清掉一个源键除轻按以外的触发位（轻按被清空时用来保持一致）。</summary>
    public void ClearSecondaryTriggers(string source)
    {
        Set(source, BindingTrigger.Double, BindingTarget.None);
        Set(source, BindingTrigger.Long, BindingTarget.None);
    }

    public MappingProfile Clone()
    {
        var copy = new MappingProfile();
        foreach ((string key, List<KeyBinding> value) in Bindings)
        {
            copy.Bindings[key] = value
                .Select(b => new KeyBinding { Trigger = b.Trigger, Kind = b.Kind, Code = b.Code, Partner = b.Partner })
                .ToList();
        }

        return copy;
    }

    /// <summary>出厂默认：背键保留手柄键语义，摇杆/陀螺仪走键鼠。</summary>
    public static MappingProfile CreateDefault()
    {
        var profile = new MappingProfile();
        profile.Set("GL", BindingTrigger.Tap, BindingTarget.Pad(PadButtons.B));
        profile.Set("GR", BindingTrigger.Tap, BindingTarget.Pad(PadButtons.A));
        profile.Set("截图", BindingTrigger.Tap, BindingTarget.Pad(PadButtons.Capture));
        profile.Set("Home", BindingTrigger.Tap, BindingTarget.Pad(PadButtons.Home));
        profile.Set(SourceKeys.LeftStick, BindingTrigger.Tap, BindingTarget.Key("W"));
        profile.Set(SourceKeys.RightStick, BindingTrigger.Tap, BindingTarget.Mouse("X"));
        profile.Set(SourceKeys.Gyro, BindingTrigger.Tap, BindingTarget.Mouse("X"));
        return profile;
    }

    /// <summary>
    /// 手柄输出的出厂表：每个键原样透传（A→A、ZL→ZL…），只有背键映射到面键，
    /// 因为 Xbox / PS5 虚拟手柄没有 GL / GR。用户改过哪一条就覆盖哪一条。
    /// </summary>
    public static MappingProfile CreateIdentityDefault(string layout)
    {
        var profile = new MappingProfile();
        foreach (string source in SourceKeys.Buttons)
        {
            if (source is "GL" or "GR") continue;
            string? code = PadCodeOf(source);
            if (code is not null)
                profile.Set(source, BindingTrigger.Tap, BindingTarget.Pad(code));
        }

        profile.Set("GL", BindingTrigger.Tap, BindingTarget.Pad(PadButtons.B));
        profile.Set("GR", BindingTrigger.Tap, BindingTarget.Pad(PadButtons.A));
        return profile;
    }

    /// <summary>
    /// 键鼠输出的出厂表：沿用此前定版的一套（X 单击/双击/长按 = 1/2/3，
    /// Y 轻按/长按 = R/F，A = Ctrl，B = 空格，L/R = C/E，ZL/ZR = 鼠标右键/左键）。
    /// </summary>
    public static MappingProfile CreateKeyboardDefault()
    {
        var profile = new MappingProfile();
        void K(string source, string key, BindingTrigger trigger = BindingTrigger.Tap) =>
            profile.Set(source, trigger, BindingTarget.Key(key));

        K("A", "Space");
        K("B", "LeftCtrl");
        K("X", "D1");
        K("X", "D2", BindingTrigger.Double);
        K("X", "D3", BindingTrigger.Long);
        K("Y", "R");
        K("Y", "F", BindingTrigger.Long);
        K("L", "C");
        K("R", "E");
        K("↑", "G");
        K("↓", "D4");
        K("→", "B");
        K("R3", "Q");
        K("GL", "LeftShift");
        K("GR", "LeftCtrl");
        K("＋", "Escape");
        // L+R 组合：两颗在判定窗口内一起按下时出 X，单键先让位。
        profile.SetCombo("L", BindingTarget.Key("X"), "R");
        profile.Set("ZL", BindingTrigger.Tap, BindingTarget.MouseBtn("Right"));
        profile.Set("ZR", BindingTrigger.Tap, BindingTarget.MouseBtn("Left"));
        return profile;
    }

    /// <summary>
    /// 把没写到的源键补成「原样透传」。旧配置只有少数几个键，直接拿来用会让其余键没有输出，
    /// 这里只补空缺，已经配过的（包括刻意设成未映射的空表）不动。
    /// </summary>
    public MappingProfile FillIdentity(string layout)
    {
        foreach (string source in SourceKeys.Buttons)
        {
            if (Bindings.TryGetValue(source, out List<KeyBinding>? list) && list.Count > 0) continue;
            string? code = PadCodeOf(source);
            if (code is not null)
            {
                Set(source, BindingTrigger.Tap, BindingTarget.Pad(code));
                continue;
            }

            if (source is "GL" or "GR")
                Set(source, BindingTrigger.Tap, BindingTarget.Pad(source == "GL" ? PadButtons.B : PadButtons.A));
        }

        return this;
    }

    /// <summary>源键对应的逻辑键代码（用于出厂透传表）。</summary>
    public static string? PadCodeOf(string source) => source switch
    {
        "A" => PadButtons.A,
        "B" => PadButtons.B,
        "X" => PadButtons.X,
        "Y" => PadButtons.Y,
        "L" => PadButtons.L,
        "R" => PadButtons.R,
        "ZL" => PadButtons.ZL,
        "ZR" => PadButtons.ZR,
        "L3" => PadButtons.L3,
        "R3" => PadButtons.R3,
        "＋" => PadButtons.Plus,
        "－" => PadButtons.Minus,
        "↑" => PadButtons.Up,
        "↓" => PadButtons.Down,
        "←" => PadButtons.Left,
        "→" => PadButtons.Right,
        "截图" => PadButtons.Capture,
        "Home" => PadButtons.Home,
        _ => null
    };
}

/// <summary>映射表的 JSON 读写。放在用户配置目录，和界面设置分开。</summary>
public static class MappingStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string FilePath => AppPaths.ConfigFile("bindings.json");

    public static MappingProfile Load(string? path = null)
    {
        string file = path ?? FilePath;
        try
        {
            if (!File.Exists(file)) return MappingProfile.CreateDefault();
            string json = File.ReadAllText(file);
            MappingProfile? profile = JsonSerializer.Deserialize<MappingProfile>(json, Options);
            return profile ?? MappingProfile.CreateDefault();
        }
        catch
        {
            return MappingProfile.CreateDefault();
        }
    }

    public static bool Save(MappingProfile profile, string? path = null)
    {
        string file = path ?? FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(profile, Options));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
