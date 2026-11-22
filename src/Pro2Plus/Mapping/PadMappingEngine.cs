using System;
using System.Collections.Generic;

namespace Pro2Plus.Mapping;

/// <summary>
/// 把「物理键」按当前配置翻成「最终输出键」，在数据进入虚拟手柄之前做一次改写。
/// 手柄模式只映射轻按：按下即出、松开即收，不做双击 / 长按判定
/// （那套判定只在键鼠模式里，见 <see cref="KbmMappingEngine"/>）。
/// </summary>
public static class PadMappingEngine
{
    private static volatile MappingProfile? profile;
    private static readonly object gate = new();

    /// <summary>切换配置。传 null 表示直通（不做任何改写）。</summary>
    public static void SetProfile(MappingProfile? next)
    {
        lock (gate)
        {
            profile = next;
            // 配置自己带了物理布局就跟着换，没带就保持现状（由页面按输出平台给默认值）
            if (next?.FaceLayout is "xbox" or "pro2") faceLayout = next.FaceLayout!;
        }
    }

    /// <summary>ABXY 物理布局（"xbox" / "pro2"）。面键靠它按位置反查盘面。</summary>
    private static volatile string faceLayout = "xbox";

    public static void SetFaceLayout(string layout) =>
        faceLayout = layout == "pro2" ? "pro2" : "xbox";

    /// <summary>输出平台（xbox / ps5 / pro2）：决定面键字母落在盘面哪个位置。</summary>
    private static volatile string platform = "xbox";

    public static void SetPlatform(string layout) =>
        platform = layout is "pro2" or "pro1" ? "pro2" : layout == "ps5" ? "ps5" : "xbox";

    public static MappingProfile? Current => profile;

    /// <summary>源键 → 内部位。与映射页热点用的是同一套定义，避免两处各写一遍。</summary>
    public static GamepadButtons BitOf(string source)
    {
        // 面键按物理丝印走：Xbox 布局 下=A、右=B；Pro2(NS) 布局 右=A、下=B。
        // 这样「按住丝印写着的那颗」发出去的就是那个字母，ABXY 对调就靠这一行生效。
        bool ns = faceLayout == "pro2";
        return source switch
        {
            "A" => ns ? GamepadButtons.East : GamepadButtons.South,
            "B" => ns ? GamepadButtons.South : GamepadButtons.East,
            "X" => ns ? GamepadButtons.North : GamepadButtons.West,
            "Y" => ns ? GamepadButtons.West : GamepadButtons.North,
            _ => BitOfRest(source)
        };
    }

    /// <summary>
    /// 固定位置版：面键永远 下=A/右=B（不看物理布局）。
    /// 键鼠通路用它——那边的丝印差由键鼠配置自己的 face_layout 处理，别被手柄页的开关带跑。
    /// </summary>
    public static GamepadButtons BitOfPositional(string source) => source switch
    {
        "A" => GamepadButtons.South,
        "B" => GamepadButtons.East,
        "X" => GamepadButtons.West,
        "Y" => GamepadButtons.North,
        _ => BitOfRest(source)
    };

    private static GamepadButtons BitOfRest(string source) => source switch
    {
        "L" => GamepadButtons.L1,
        "R" => GamepadButtons.R1,
        "ZL" => GamepadButtons.L2,
        "ZR" => GamepadButtons.R2,
        "L3" => GamepadButtons.LeftStick,
        "R3" => GamepadButtons.RightStick,
        "＋" => GamepadButtons.Start,
        "－" => GamepadButtons.Back,
        "↑" => GamepadButtons.DPadUp,
        "↓" => GamepadButtons.DPadDown,
        "←" => GamepadButtons.DPadLeft,
        "→" => GamepadButtons.DPadRight,
        "截图" => GamepadButtons.Capture,
        "Home" => GamepadButtons.Home,
        "GL" => GamepadButtons.PaddleLeft,
        "GR" => GamepadButtons.PaddleRight,
        _ => GamepadButtons.None
    };

    public static GamepadButtons BitOfPad(string code)
    {
        // 字母 → 盘面位置按输出平台取：NS 的 AB 和 Xbox 是反的（下=B、右=A），
        // PS5 用符号但位置与 Xbox 一致。这样「按丝印发键」在各平台上都发对字母。
        bool ns = platform == "pro2";
        return code switch
        {
            PadButtons.A => ns ? GamepadButtons.East : GamepadButtons.South,
            PadButtons.B => ns ? GamepadButtons.South : GamepadButtons.East,
            PadButtons.X => ns ? GamepadButtons.North : GamepadButtons.West,
            PadButtons.Y => ns ? GamepadButtons.West : GamepadButtons.North,
            _ => BitOfPadRest(code)
        };
    }

    private static GamepadButtons BitOfPadRest(string code) => code switch
    {
        PadButtons.L => GamepadButtons.L1,
        PadButtons.R => GamepadButtons.R1,
        PadButtons.ZL => GamepadButtons.L2,
        PadButtons.ZR => GamepadButtons.R2,
        PadButtons.L3 => GamepadButtons.LeftStick,
        PadButtons.R3 => GamepadButtons.RightStick,
        PadButtons.Plus => GamepadButtons.Start,
        PadButtons.Minus => GamepadButtons.Back,
        PadButtons.Up => GamepadButtons.DPadUp,
        PadButtons.Down => GamepadButtons.DPadDown,
        PadButtons.Left => GamepadButtons.DPadLeft,
        PadButtons.Right => GamepadButtons.DPadRight,
        PadButtons.Capture => GamepadButtons.Capture,
        PadButtons.Home => GamepadButtons.Home,
        PadButtons.GL => GamepadButtons.PaddleLeft,
        PadButtons.GR => GamepadButtons.PaddleRight,
        _ => GamepadButtons.None
    };

    private static readonly string[] Sources = SourceKeys.Buttons;

    /// <summary>
    /// 改写一帧按键。手柄模式只看轻按：按住时输出轻按目标，松开即清。
    /// </summary>
    public static GamepadState Apply(GamepadState input)
    {
        MappingProfile? p = profile;
        if (p is null) return input;

        GamepadButtons physical = input.Buttons;
        GamepadButtons output = GamepadButtons.None;
        bool zlStillTrigger = false, zrStillTrigger = false;

        foreach (string source in Sources)
        {
            GamepadButtons bit = BitOf(source);
            if (bit == GamepadButtons.None) continue;

            // 扳机是模拟量：过半即算按下，改键后仍然认得「半扣」。
            bool down = (physical & bit) != 0;
            if (source == "ZL" && input.L2 >= GamepadState.TriggerMax / 2) down = true;
            if (source == "ZR" && input.R2 >= GamepadState.TriggerMax / 2) down = true;
            if (!down) continue;

            BindingTarget target = p.Get(source, BindingTrigger.Tap);
            if (target.Kind == BindingTargetKind.PadButton)
            {
                output |= BitOfPad(target.Code);
            }

            if (source == "ZL" && target.Code == PadButtons.ZL) zlStillTrigger = true;
            if (source == "ZR" && target.Code == PadButtons.ZR) zrStillTrigger = true;
        }

        // 没有映射到的位（例如 Aux）原样保留，避免丢掉未来新增的键。
        GamepadButtons passthrough = physical & ~AllMappable;
        GamepadState next = input.Clone();
        next.Buttons = output | passthrough;

        // ZL/ZR 被改到别的键时，扳机行程也要归零，否则会出现「按 A 却带着扳机」。
        if (!zlStillTrigger) next.L2 = 0;
        if (!zrStillTrigger) next.R2 = 0;
        return next;
    }

    private static GamepadButtons AllMappable
    {
        get
        {
            GamepadButtons all = GamepadButtons.None;
            foreach (string source in Sources) all |= BitOf(source);
            return all;
        }
    }
}
