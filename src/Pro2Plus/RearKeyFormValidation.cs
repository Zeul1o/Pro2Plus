using System;
using System.Collections.Generic;
using System.Globalization;

namespace Pro2Plus;

internal readonly record struct RearKeyStepForm(
    GamepadButtons Keys,
    Ps5TouchGesture Touch,
    string Delay,
    string Duration);

/// <summary>界面当前值的一次快照，纯校验层只读它，不接触任何 WPF 对象。</summary>
internal readonly record struct RearKeyForm(
    string Title,
    bool Enabled,
    string Action,
    Ps5TouchGesture TouchAction,
    string Delay,
    string Duration,
    string Gap,
    string Count,
    GamepadButtons Keys,
    IReadOnlyList<RearKeyStepForm> Steps);

internal readonly record struct RearKeyBuildResult(bool Ok, string Error, RearKeyBinding Binding);

/// <summary>
/// 背键编辑表单的纯逻辑：动作名/序号、数值区间、展示文案、以及从表单构建配置。
/// 所有分支都能离线逐例比对，UI 只负责把控件值装进 RearKeyForm。
/// </summary>
internal static class RearKeyFormValidation
{
    internal const int MaxTimingMs = 30_000;
    internal const int MinGapMs = 30;
    internal const int MaxRepeatCount = 1000;

    internal static readonly string[] Actions = ["按住映射", "单次按键", "按住连发", "顺序按键"];

    internal static int ActionIndex(string? action) => Array.IndexOf(Actions, action);

    internal static string NormalizeAction(string? action)
    {
        int index = ActionIndex(action);
        return index >= 0 ? Actions[index] : Actions[0];
    }

    internal static bool IsTimed(string? action) => ActionIndex(action) != 0;

    internal static bool IsDurationVisible(string? action, Ps5TouchGesture touch) =>
        IsTimed(action) || touch != Ps5TouchGesture.None;

    internal static bool IsRepeat(string? action) => ActionIndex(action) == 2;

    internal static bool IsSequence(string? action) => ActionIndex(action) == 3;

    internal static bool Number(string? text, int min, int max, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
        value >= min && value <= max;

    internal static string EnabledCaption(bool enabled) => enabled ? "已启用" : "未启用";

    internal static string DurationLabel(Ps5TouchGesture touch) =>
        touch != Ps5TouchGesture.None ? "手势时长（ms）" : "按下时长（ms）";

    internal static string Summary(IEnumerable<string> buttonLabels, Ps5TouchGesture touch)
    {
        var parts = new List<string>();
        foreach (string label in buttonLabels)
        {
            parts.Add(label);
        }

        if (touch != Ps5TouchGesture.None)
        {
            foreach (Ps5TouchGestureChoice choice in Ps5TouchGestures.Choices)
            {
                if (choice.Gesture == touch)
                {
                    parts.Add(choice.Label);
                    break;
                }
            }
        }

        return parts.Count == 0 ? "未分配按键" : string.Join(" + ", parts);
    }

    internal static string BehaviorHint(string? action, Ps5TouchGesture touch)
    {
        if (touch != Ps5TouchGesture.None)
        {
            if (IsRepeat(action)) return "按住时重复手势，松开停止；同一时刻执行一个触控动作。";
            return !IsTimed(action)
                ? "按住完成一次手势，提前松开会停止；游戏需支持触控板。"
                : "每次按下完成一次手势；再次触发需松开。游戏需支持触控板。";
        }

        return ActionIndex(action) switch
        {
            2 => "仅按住时连发；松开停止，0 表示不限次数。",
            1 => "触发一次；执行中重复按下不排队。",
            3 => "触发一次顺序动作；执行中不叠加，可随时停止背键。",
            _ => "按住生效，松开停止。"
        };
    }

    internal static RearKeyBuildResult Build(RearKeyForm form)
    {
        int actionIndex = ActionIndex(form.Action);
        if (form.Enabled && form.Keys == GamepadButtons.None && form.TouchAction == Ps5TouchGesture.None)
            return Fail(form.Title + "：请选择按键或触控板动作。");
        if (form.Enabled && RearKeyBinding.HasOppositeDirections(form.Keys))
            return Fail(form.Title + "：相反方向不能同时选择。");

        int minDuration = form.TouchAction != Ps5TouchGesture.None ? Ps5TouchGestures.MinDurationMs : 30;
        bool delayOk = Number(form.Delay, 0, MaxTimingMs, out int delay);
        bool durationOk = Number(form.Duration, minDuration, MaxTimingMs, out int duration);
        bool gapOk = Number(form.Gap, MinGapMs, MaxTimingMs, out int gap);
        bool countOk = Number(form.Count, 0, MaxRepeatCount, out int count);
        if (form.Enabled &&
            (!delayOk ||
             (IsDurationVisible(form.Action, form.TouchAction) && !durationOk) ||
             (IsRepeat(form.Action) && (!gapOk || !countOk))))
        {
            return Fail(form.Title + "：延迟0–30000ms，动作时长" + minDuration +
                        "–30000ms，间隔30–30000ms，次数0–1000。");
        }

        // 隐藏或禁用的字段不能阻止关掉一条映射；无效草稿存成安全默认值。
        if (!delayOk) delay = 0;
        if (!durationOk) duration = 80;
        if (!gapOk) gap = 80;
        if (!countOk) count = 0;

        var steps = new List<RearKeyStep>();
        if (form.Enabled && IsSequence(form.Action))
        {
            foreach (RearKeyStepForm step in form.Steps)
            {
                if ((step.Keys == GamepadButtons.None && step.Touch == Ps5TouchGesture.None) ||
                    RearKeyBinding.HasOppositeDirections(step.Keys) ||
                    !Number(step.Delay, 0, MaxTimingMs, out int stepDelay) ||
                    !Number(step.Duration,
                        step.Touch != Ps5TouchGesture.None ? Ps5TouchGestures.MinDurationMs : 30,
                        MaxTimingMs, out int stepDuration))
                {
                    return Fail(form.Title + "：请检查后续按键、延迟和按下时长。");
                }

                steps.Add(new RearKeyStep
                {
                    Buttons = step.Keys,
                    TouchGesture = step.Touch,
                    DelayMs = stepDelay,
                    PressMs = stepDuration
                });
            }
        }

        return new RearKeyBuildResult(true, "", new RearKeyBinding
        {
            Enabled = form.Enabled,
            Action = (RearKeyAction)actionIndex,
            Buttons = form.Keys,
            TouchGesture = form.TouchAction,
            DelayMs = delay,
            PressMs = duration,
            GapMs = gap,
            RepeatCount = count,
            Steps = steps.ToArray()
        });
    }

    private static RearKeyBuildResult Fail(string error) =>
        new(false, error, new RearKeyBinding());
}
