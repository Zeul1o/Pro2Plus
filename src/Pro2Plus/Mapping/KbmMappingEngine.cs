using System;
using System.Collections.Generic;

namespace Pro2Plus.Mapping;

/// <summary>
/// 键鼠模式的按键仲裁器。每个源键有三个触发位（轻按 / 双击 / 长按），
/// 每个触发位只允许挂一个目标，同一时刻只出一个——
///
///   · 只绑轻按：按下即出、松开即收，零延迟，和普通键盘键一样可以按住不放。
///   · 绑了双击：轻按必须等「双击等待间隔」过去，确认没有第二下才算轻按。
///   · 只绑长按（没有双击）：松开时就能确认是轻按，不必再等双击窗口。
///   · 轻按未绑定：双击 / 长按不参与判定（界面上也是锁住的）。
///
/// 长按到点立刻出，按住期间持续输出（不是连发），松开才收。
/// </summary>
public sealed class KbmMappingEngine
{
    public static KbmMappingEngine Shared { get; } = new();

    private enum Emit
    {
        Key,
        Modifier,
        MouseButton,
        Wheel
    }

    private readonly record struct Action(Emit Kind, int Code);

    private sealed class SlotState
    {
        public bool Down;
        public long SinceMs;
        public bool LongFired;
        public bool SecondTap;
        public BindingTarget PendingTap = BindingTarget.None;
        public long PendingUntilMs;
    }

    private readonly Dictionary<string, SlotState> states = new(StringComparer.Ordinal);
    private readonly List<(Action Action, long Until)> pulses = new();
    private readonly HashSet<byte> keys = new();
    private readonly object gate = new();

    private MappingProfile? profile;
    private int holdMs = 250;
    private int doubleTapMs = 100;
    private int comboWindowMs = 150;
    private int pulseWidthMs = 28;

    private byte modifiers;
    private byte mouseButtons;
    private int wheel;

    // 组合键：每个「设了组合的键」一份状态；判定为组合后两颗一起让位，直到都松开。
    private sealed class ComboState
    {
        public bool SourceDown;
        public bool PartnerDown;
        public long SourceMs;
        public long PartnerMs;
        public bool Latched;
    }

    private readonly Dictionary<string, ComboState> comboStates = new(StringComparer.Ordinal);
    private readonly HashSet<string> blockedSources = new(StringComparer.Ordinal);
    private readonly HashSet<string> gatedSources = new(StringComparer.Ordinal);

    /// <summary>当前生效的键鼠映射表；null 表示不处理。</summary>
    public MappingProfile? Current => profile;

    public void SetProfile(MappingProfile? next)
    {
        lock (gate)
        {
            profile = next;
            states.Clear();
            pulses.Clear();
            keys.Clear();
            modifiers = 0;
            mouseButtons = 0;
            wheel = 0;
            comboStates.Clear();
            blockedSources.Clear();
            gatedSources.Clear();
        }
    }

    /// <summary>判定窗口由设置页的「双击间隔」「长按判定」「组合键间隔」驱动。</summary>
    public void SetTiming(int hold, int doubleTap, int comboWindow, int pulseWidth)
    {
        lock (gate)
        {
            holdMs = Math.Clamp(hold, 60, 1200);
            doubleTapMs = Math.Clamp(doubleTap, 30, 600);
            comboWindowMs = Math.Clamp(comboWindow, 30, 600);
            pulseWidthMs = Math.Clamp(pulseWidth, 8, 300);
        }
    }

    public int HoldMs => holdMs;
    public int DoubleTapMs => doubleTapMs;
    public int ComboWindowMs => comboWindowMs;

    /// <summary>物理按下判断：扳机按模拟量过半计算，和手柄通路一致。</summary>
    private static bool IsDown(GamepadState input, string source)
    {
        GamepadButtons bit = PadMappingEngine.BitOfPositional(source);
        if (bit == GamepadButtons.None) return false;
        if ((input.Buttons & bit) != 0) return true;
        if (source == "ZL" && input.L2 >= GamepadState.TriggerMax / 2) return true;
        if (source == "ZR" && input.R2 >= GamepadState.TriggerMax / 2) return true;
        return false;
    }

    /// <summary>跑一帧仲裁。输出通过下面的只读属性取。</summary>
    public void Update(GamepadState input, long nowMs)
    {
        lock (gate)
        {
            keys.Clear();
            modifiers = 0;
            mouseButtons = 0;

            MappingProfile? p = profile;
            if (p is null) return;

            // ---------- 组合键 ----------
            // 顺序敏感：主键先按下，搭子在窗口内跟上才算组合。
            // 搭子只有在主键已按下时才被「压住」——单独按搭子（比如 R）完全走它自己的映射，
            // 不受任何额外延迟，别人配了 L+R 不会拖累 R 的正常使用。
            blockedSources.Clear();
            gatedSources.Clear();
            foreach (string source in SourceKeys.Buttons)
            {
                (BindingTarget comboTarget, string partner) = p.GetCombo(source);
                if (comboTarget.IsNone || string.IsNullOrEmpty(partner)) continue;

                if (!comboStates.TryGetValue(source, out ComboState? cs))
                    comboStates[source] = cs = new ComboState();

                bool sDown = IsDown(input, source);
                bool pDown = IsDown(input, partner);
                if (sDown && !cs.SourceDown) { cs.SourceDown = true; cs.SourceMs = nowMs; }
                if (pDown && !cs.PartnerDown) { cs.PartnerDown = true; cs.PartnerMs = nowMs; }
                if (!sDown) cs.SourceDown = false;
                if (!pDown) cs.PartnerDown = false;

                // 主键排前面才有资格组合；先按搭子就直接按它自己的映射出，不会两头都出。
                if (!cs.Latched)
                {
                    if (sDown && pDown && cs.PartnerMs >= cs.SourceMs &&
                        cs.PartnerMs - cs.SourceMs <= comboWindowMs)
                        cs.Latched = true;
                }
                else if (!sDown && !pDown)
                {
                    cs.Latched = false;
                }

                gatedSources.Add(source);                 // 主键要等窗口，确认不是组合再出单键
                // 搭子只在「主键按住、且这一下是主键之后才按下」时让位；
                // 先按住搭子再按主键属于两次单键，搭子保持原样输出，不会中途掉一下。
                if (sDown && cs.PartnerMs >= cs.SourceMs) gatedSources.Add(partner);

                if (!cs.Latched) continue;

                blockedSources.Add(source);
                blockedSources.Add(partner);
                if (sDown && pDown) EmitHold(comboTarget);
            }

            foreach (string source in SourceKeys.Buttons)
            {
                GamepadButtons bit = PadMappingEngine.BitOfPositional(source);
                if (bit == GamepadButtons.None) continue;

                bool down = (input.Buttons & bit) != 0;
                // 扳机是模拟量，过半即算按下，和之前的手感一致。
                if (source == "ZL" && input.L2 >= GamepadState.TriggerMax / 2) down = true;
                if (source == "ZR" && input.R2 >= GamepadState.TriggerMax / 2) down = true;

                if (!states.TryGetValue(source, out SlotState? st)) states[source] = st = new SlotState();

                BindingTarget tap = p.Get(source, BindingTrigger.Tap);
                bool tapBound = !tap.IsNone;
                // 轻按没绑定，另外两位不参与判定：先有轻按，才有双击/长按。
                BindingTarget dbl = tapBound ? p.Get(source, BindingTrigger.Double) : BindingTarget.None;
                BindingTarget lng = tapBound ? p.Get(source, BindingTrigger.Long) : BindingTarget.None;
                bool hasDouble = !dbl.IsNone;
                bool hasLong = !lng.IsNone;

                // 这一颗正在组合里：完全让位，只维护按下状态。
                if (blockedSources.Contains(source))
                {
                    {
                        if (!down)
                        {
                            st.Down = false;
                            st.LongFired = false;
                            st.SecondTap = false;
                            st.PendingTap = BindingTarget.None;
                        }
                        else if (!st.Down)
                        {
                            st.Down = true;
                            st.SinceMs = nowMs;
                            st.LongFired = false;
                        }
                        continue;
                    }
                }

                // 参与组合的键要先等窗口，确认不是组合才出单键。
                if (gatedSources.Contains(source))
                {
                    {
                        if (!down && st.Down && nowMs - st.SinceMs < comboWindowMs)
                        {
                            st.Down = false;
                            st.LongFired = false;
                            st.SecondTap = false;
                            st.PendingTap = BindingTarget.None;
                            if (tapBound) EmitPulse(tap, nowMs);
                            continue;
                        }

                        if (down && !st.Down)
                        {
                            st.Down = true;
                            st.SinceMs = nowMs;
                            st.LongFired = false;
                            st.SecondTap = false;
                            st.PendingTap = BindingTarget.None;
                        }

                        if (down && nowMs - st.SinceMs < comboWindowMs) continue;
                    }
                }

                if (down && !st.Down)
                {
                    st.Down = true;
                    st.SinceMs = nowMs;
                    st.LongFired = false;
                    if (!st.PendingTap.IsNone && nowMs < st.PendingUntilMs)
                    {
                        // 双击窗口内又按下：撤销待发的轻按，这一下算双击。
                        st.PendingTap = BindingTarget.None;
                        st.SecondTap = true;
                    }
                    else
                    {
                        st.SecondTap = false;
                    }
                }
                else if (!down && st.Down)
                {
                    st.Down = false;
                    if (st.LongFired)
                    {
                        // 长按已经出过，松开不补轻按。
                    }
                    else if (st.SecondTap)
                    {
                        st.SecondTap = false;
                        EmitPulse(dbl, nowMs);
                    }
                    else if (tapBound)
                    {
                        if (hasDouble)
                        {
                            // 还要等第二下：先挂起，窗口内没第二下再真发。
                            st.PendingTap = tap;
                            st.PendingUntilMs = nowMs + doubleTapMs;
                        }
                        else if (hasLong)
                        {
                            // 只有轻按+长按：松开即可确认是轻按，不用等双击窗口。
                            EmitPulse(tap, nowMs);
                        }
                        // 只绑轻按：按住期间已经输出，松开直接收，不补脉冲。
                    }
                    st.LongFired = false;
                }

                if (down && tapBound)
                {
                    if (!hasDouble && !hasLong)
                    {
                        // 只绑轻按：按下即出，按住期间持续。
                        EmitHold(tap);
                    }
                    else if (hasLong)
                    {
                        if (!st.LongFired && nowMs - st.SinceMs >= holdMs)
                        {
                            st.LongFired = true;
                            st.PendingTap = BindingTarget.None;
                        }
                        if (st.LongFired) EmitHold(lng);
                    }
                }

                if (!st.PendingTap.IsNone && nowMs >= st.PendingUntilMs)
                {
                    EmitPulse(st.PendingTap, nowMs);
                    st.PendingTap = BindingTarget.None;
                }
            }

            // 短脉冲（轻按 / 双击）叠到本帧，到期自动松开。
            for (int i = pulses.Count - 1; i >= 0; i--)
            {
                (Action action, long until) = pulses[i];
                if (nowMs >= until) { pulses.RemoveAt(i); continue; }
                Apply(action);
            }
        }
    }

    public IReadOnlyCollection<byte> Keys
    {
        get { lock (gate) return keys.ToArray(); }
    }

    public byte Modifiers
    {
        get { lock (gate) return modifiers; }
    }

    public byte MouseButtons
    {
        get { lock (gate) return mouseButtons; }
    }

    public int TakeWheel()
    {
        lock (gate)
        {
            int value = wheel;
            wheel = 0;
            return value;
        }
    }

    // ── 输出 ─────────────────────────────────────────────────────

    private void EmitHold(BindingTarget target)
    {
        Action action = Resolve(target);
        if (action.Kind == Emit.Wheel) { wheel += action.Code; return; }
        Apply(action);
    }

    private void EmitPulse(BindingTarget target, long nowMs)
    {
        Action action = Resolve(target);
        if (action.Kind == Emit.Wheel) { wheel += action.Code; return; }
        if (action.Kind is Emit.Key or Emit.Modifier or Emit.MouseButton)
        {
            pulses.Add((action, nowMs + pulseWidthMs));
            Apply(action);
        }
    }

    private void Apply(Action action)
    {
        switch (action.Kind)
        {
            case Emit.Key:
                if (action.Code != 0) keys.Add((byte)action.Code);
                break;
            case Emit.Modifier:
                modifiers |= (byte)action.Code;
                break;
            case Emit.MouseButton:
                mouseButtons |= (byte)action.Code;
                break;
        }
    }

    /// <summary>目标 → 输出动作。键位用 HID usage，修饰键折成 modifier 位。</summary>
    private static Action Resolve(BindingTarget target)
    {
        switch (target.Kind)
        {
            // 键鼠模式只出键盘 / 鼠标；手柄键目标不属于这一路，直接当未映射。
            case BindingTargetKind.PadButton:
                return new Action(Emit.Key, 0);
            case BindingTargetKind.MouseButton:
                return new Action(Emit.MouseButton, target.Code switch
                {
                    "Left" => 0x01,
                    "Right" => 0x02,
                    "Middle" => 0x04,
                    _ => 0
                });
            case BindingTargetKind.MouseAxis:
                return new Action(Emit.Wheel, target.Code == "WheelDown" ? -1 : 1);
            case BindingTargetKind.Keyboard:
                byte modifier = ModifierBit(target.Code);
                if (modifier != 0) return new Action(Emit.Modifier, modifier);
                return new Action(Emit.Key, HidUsage(target.Code));
            default:
                return new Action(Emit.Key, 0);
        }
    }

    private static byte ModifierBit(string key) => key switch
    {
        "LeftCtrl" or "RightCtrl" => 0x01,
        "LeftShift" or "RightShift" => 0x02,
        "LeftAlt" or "RightAlt" => 0x04,
        _ => (byte)0
    };

    /// <summary>WPF Key 名 → USB HID usage。取不到返回 0（不输出）。</summary>
    public static byte HidUsage(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c >= 'A' && c <= 'Z') return (byte)(0x04 + (c - 'A'));
            if (c >= '0' && c <= '9') return (byte)(0x1E + (c - '0'));
        }

        if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]))
            return (byte)(0x1E + (key[1] - '0'));
        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out int fn)
            && fn is >= 1 and <= 12)
            return (byte)(0x3A + fn - 1);

        return key switch
        {
            "Space" => 0x2C,
            "Return" or "Enter" => 0x28,
            "Escape" => 0x29,
            "Back" => 0x2A,
            "Tab" => 0x2B,
            "CapsLock" => 0x39,
            "Up" => 0x52,
            "Down" => 0x51,
            "Left" => 0x50,
            "Right" => 0x4F,
            "Insert" => 0x49,
            "Delete" => 0x4C,
            "Home" => 0x4A,
            "End" => 0x4D,
            "PageUp" => 0x4B,
            "PageDown" => 0x4E,
            "OemMinus" => 0x2D,
            "OemPlus" => 0x2E,
            "OemOpenBrackets" => 0x2F,
            "OemCloseBrackets" => 0x30,
            "OemPipe" => 0x31,
            "OemSemicolon" => 0x33,
            "OemQuotes" => 0x34,
            "OemTilde" => 0x35,
            "OemComma" => 0x36,
            "OemPeriod" => 0x37,
            "OemQuestion" => 0x38,
            _ => 0
        };
    }
}
