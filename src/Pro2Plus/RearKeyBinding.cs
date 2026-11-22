using System;
using System.Collections.Generic;

namespace Pro2Plus;

public enum RearKeyAction { Hold, Tap, Repeat, Sequence }

/// <summary>序列动作中的一步；所有数值在加载/保存时统一收敛到合法区间。</summary>
public sealed record RearKeyStep
{
    private const int MaxTimingMs = 30_000;
    private const int MinPressMs = 30;

    public GamepadButtons Buttons { get; init; }
    public Ps5TouchGesture TouchGesture { get; init; }
    public int DelayMs { get; init; }
    public int PressMs { get; init; } = 80;

    public RearKeyStep Normalize()
    {
        bool hasGesture = GestureIsPlayable(TouchGesture);
        return this with
        {
            Buttons = RearKeyBinding.SafeButtons(Buttons),
            TouchGesture = Enum.IsDefined(TouchGesture) ? TouchGesture : Ps5TouchGesture.None,
            DelayMs = Math.Clamp(DelayMs, 0, MaxTimingMs),
            PressMs = Math.Clamp(PressMs, hasGesture ? Ps5TouchGestures.MinDurationMs : MinPressMs, MaxTimingMs)
        };
    }

    internal static bool GestureIsPlayable(Ps5TouchGesture gesture) =>
        Enum.IsDefined(gesture) && gesture != Ps5TouchGesture.None;
}

public sealed record RearKeyBinding
{
    private const int MaxTimingMs = 30_000;
    private const int MinPressMs = 30;
    private const int MaxSteps = 16;
    private const int MaxRepeats = 1000;

    public const GamepadButtons AllowedButtons = (GamepadButtons)((1UL << 18) - 1);

    public bool Enabled { get; init; }
    public RearKeyAction Action { get; init; }
    public GamepadButtons Buttons { get; init; }
    public Ps5TouchGesture TouchGesture { get; init; }
    public int DelayMs { get; init; }
    public int PressMs { get; init; } = 80;
    public int GapMs { get; init; } = 80;
    /// <summary>0 表示物理键按住期间一直重复。</summary>
    public int RepeatCount { get; init; }
    public RearKeyStep[] Steps { get; init; } = [];

    public RearKeyBinding Normalize()
    {
        bool hasGesture = RearKeyStep.GestureIsPlayable(TouchGesture);
        RearKeyStep[]? source = Steps;
        var steps = new List<RearKeyStep>(source is null ? 0 : Math.Min(source.Length, MaxSteps));
        if (source != null)
        {
            for (int i = 0; i < source.Length && steps.Count < MaxSteps; i++)
            {
                if (source[i] != null)
                {
                    steps.Add(source[i].Normalize());
                }
            }
        }

        return this with
        {
            Action = Enum.IsDefined(Action) ? Action : RearKeyAction.Hold,
            Buttons = SafeButtons(Buttons),
            TouchGesture = Enum.IsDefined(TouchGesture) ? TouchGesture : Ps5TouchGesture.None,
            DelayMs = Math.Clamp(DelayMs, 0, MaxTimingMs),
            PressMs = Math.Clamp(PressMs, hasGesture ? Ps5TouchGestures.MinDurationMs : MinPressMs, MaxTimingMs),
            GapMs = Math.Clamp(GapMs, MinPressMs, MaxTimingMs),
            RepeatCount = Math.Clamp(RepeatCount, 0, MaxRepeats),
            Steps = steps.ToArray()
        };
    }

    public static bool HasOppositeDirections(GamepadButtons buttons) =>
        (buttons & (GamepadButtons.DPadUp | GamepadButtons.DPadDown)) == (GamepadButtons.DPadUp | GamepadButtons.DPadDown) ||
        (buttons & (GamepadButtons.DPadLeft | GamepadButtons.DPadRight)) == (GamepadButtons.DPadLeft | GamepadButtons.DPadRight);

    /// <summary>清掉未使用的位，并让同时按下的相反方向互相抵消。</summary>
    public static GamepadButtons SafeButtons(GamepadButtons buttons)
    {
        GamepadButtons result = buttons & AllowedButtons;
        const GamepadButtons vertical = GamepadButtons.DPadUp | GamepadButtons.DPadDown;
        const GamepadButtons horizontal = GamepadButtons.DPadLeft | GamepadButtons.DPadRight;
        if ((result & vertical) == vertical) result &= ~vertical;
        if ((result & horizontal) == horizontal) result &= ~horizontal;
        return result;
    }
}

public sealed record RearKeyProfile
{
    public RearKeyBinding Left { get; init; } = new();
    public RearKeyBinding Right { get; init; } = new();
    public RearKeyBinding C { get; init; } = new();

    public RearKeyProfile Normalize() => new()
    {
        Left = (Left ?? new()).Normalize(),
        Right = (Right ?? new()).Normalize(),
        C = (C ?? new()).Normalize()
    };
}

/// <summary>三套输出模式各自一份背键配置，默认全部不预绑，交给界面配置。</summary>
public sealed record RearKeyConfiguration
{
    public RearKeyProfile Xbox { get; init; } = new();
    public RearKeyProfile Ps5 { get; init; } = new();
    public RearKeyProfile Pro1 { get; init; } = new();

    public RearKeyConfiguration Normalize() => new()
    {
        Xbox = (Xbox ?? new()).Normalize(),
        Ps5 = (Ps5 ?? new()).Normalize(),
        Pro1 = (Pro1 ?? new()).Normalize()
    };

    public RearKeyProfile? ForMode(ViiperVirtualMode mode) => mode switch
    {
        ViiperVirtualMode.Xbox or ViiperVirtualMode.XboxProfessionalImuTest => Xbox,
        ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.DualSenseProfessionalImuTest => Ps5,
        ViiperVirtualMode.Pro1 => Pro1,
        // Pro2 原生协议（含有线）没有独立界面，沿用 Xbox 页配置。
        // 报告中 GL/GR/C 是三个独立位，重映射只清自己那一位。
        ViiperVirtualMode.Pro2 => Xbox,
        _ => null
    };
}
