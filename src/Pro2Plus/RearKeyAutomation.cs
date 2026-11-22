using System;
using System.Diagnostics;
using System.Linq;

namespace Pro2Plus;

/// <summary>
/// 单个背键通道的状态：只在新鲜的物理输入帧上推进，不启动定时器、
/// 不做 GATT 操作、不补造 FD2 帧。时序决策交给 RearKeyTiming。
/// </summary>
internal sealed class RearKeyChannel
{
    private readonly Ps5TouchPlayback touch = new();
    private bool initialized, down, active, allowTouch;
    private long start;
    private long lastPulse = -1;
    private GamepadButtons lastOutput;

    public Ps5TouchFrame Touch { get; private set; }
    public bool Active => active || touch.HasPending;

    public void Reset()
    {
        initialized = down = active = false;
        start = 0;
        lastPulse = -1;
        lastOutput = GamepadButtons.None;
        touch.Reset();
        Touch = default;
    }

    private GamepadButtons Emit(RearKeyPlan plan, bool cancelTouch = false)
    {
        Touch = cancelTouch || !allowTouch
            ? touch.Cancel()
            : touch.Sample(plan.Gesture, plan.Pulse, plan.Fraction);

        // 两次相邻的脉冲若实际投递跳过了间隔，必须先露出一帧真正的松手。
        if (plan.Pulse != lastPulse && (lastOutput & plan.Buttons) != 0)
        {
            lastOutput = GamepadButtons.None;
            return GamepadButtons.None;
        }

        lastPulse = plan.Pulse;
        lastOutput = plan.Buttons;
        return plan.Buttons;
    }

    public GamepadButtons Get(RearKeyBinding binding, bool pressed, long ticks, bool ps5)
    {
        allowTouch = ps5;
        if (!initialized)
        {
            initialized = true;
            down = pressed;
            // 刚启动或刚应用设置时若键已经按住，不触发动作。
            return GamepadButtons.None;
        }

        if (!binding.Enabled)
        {
            active = false;
            down = pressed;
            return Emit(RearKeyPlan.Neutral, cancelTouch: true);
        }

        // 延迟动作不排队、不重叠重启。
        if (pressed && !down && !active && !touch.HasPending)
        {
            active = true;
            start = ticks;
            touch.Reset();
        }

        down = pressed;
        if (!pressed && binding.Action is RearKeyAction.Hold or RearKeyAction.Repeat)
        {
            active = false;
            return Emit(RearKeyPlan.Neutral, cancelTouch: true);
        }

        if (!active)
        {
            return Emit(RearKeyPlan.Neutral);
        }

        double elapsed = (ticks - start) * 1000.0 / Stopwatch.Frequency - binding.DelayMs;
        if (elapsed < 0)
        {
            return Emit(RearKeyPlan.Neutral);
        }

        RearKeyPlan plan = RearKeyTiming.Plan(binding, elapsed);
        if (plan.Finished)
        {
            active = false;
            return Emit(RearKeyPlan.Neutral);
        }

        return Emit(plan);
    }
}

/// <summary>
/// 唯一输出消费者上的背键映射器。配置快照属于会话，界面不能中途改写；
/// 触摸手势独占一根合成手指，冲突请求直接丢弃而不排队。
/// </summary>
public sealed class RearKeyAutomation
{
    private readonly RearKeyChannel left = new(), right = new(), center = new();
    private RearKeyConfiguration? current;
    private ViiperVirtualMode currentMode;
    private long lastTicks;
    private GamepadState? lastPhysical;
    private GamepadButtons lastMapped;
    private bool lastTouchActive;
    private int touchOwner = -1;
    private ulong touchStarts, touchReleases, touchSuppressed;

    public string TouchMetrics =>
        $"touch_starts={touchStarts} touch_releases={touchReleases} touch_conflicts_ignored={touchSuppressed} touch_owner={touchOwner} touch_cadence=fresh_source_frames";

    public bool HasPendingAction =>
        left.Active || right.Active || center.Active ||
        lastMapped != GamepadButtons.None || lastTouchActive;

    public void Reset()
    {
        current = null;
        lastTicks = 0;
        left.Reset();
        right.Reset();
        center.Reset();
        lastPhysical = null;
        lastMapped = GamepadButtons.None;
        lastTouchActive = false;
        touchOwner = -1;
    }

    /// <summary>只撤掉本映射器加上去的位，不松开玩家独立按住的物理键。</summary>
    public bool TryCancelOutput(out GamepadState release, bool forceRelease = false)
    {
        bool needed = lastPhysical != null &&
            (lastMapped != GamepadButtons.None || lastTouchActive ||
             (forceRelease && current?.ForMode(currentMode) != null));
        release = lastPhysical?.Clone() ?? GamepadState.Neutral();
        RearKeyProfile? profile = current?.ForMode(currentMode);
        if (profile?.Left.Enabled == true) release.Buttons &= ~GamepadButtons.PaddleLeft;
        if (profile?.Right.Enabled == true) release.Buttons &= ~GamepadButtons.PaddleRight;
        if (profile?.C.Enabled == true) release.Buttons &= ~GamepadButtons.Aux;
        Reset();
        return needed;
    }

    public static bool ProfilesEquivalent(RearKeyProfile? a, RearKeyProfile? b) =>
        ReferenceEquals(a, b) ||
        (a != null && b != null && BindingEquivalent(a.Left, b.Left) &&
         BindingEquivalent(a.Right, b.Right) && BindingEquivalent(a.C, b.C));

    private static bool BindingEquivalent(RearKeyBinding a, RearKeyBinding b) =>
        a.Enabled == b.Enabled && a.Action == b.Action && a.Buttons == b.Buttons &&
        a.TouchGesture == b.TouchGesture && a.DelayMs == b.DelayMs && a.PressMs == b.PressMs &&
        a.GapMs == b.GapMs && a.RepeatCount == b.RepeatCount && a.Steps.SequenceEqual(b.Steps);

    public static GamepadState? MergeProfessionalControls(GamepadState mapped, GamepadState? professional)
    {
        if (professional == null ||
            (professional.Buttons == mapped.Buttons && professional.L2 == mapped.L2 &&
             professional.R2 == mapped.R2 && professional.Ps5Touch == mapped.Ps5Touch))
        {
            return professional;
        }

        GamepadState result = professional.Clone();
        result.Buttons = mapped.Buttons;
        result.L2 = mapped.L2;
        result.R2 = mapped.R2;
        result.Ps5Touch = mapped.Ps5Touch;
        return result;
    }

    public GamepadState Apply(
        GamepadState physical,
        ViiperVirtualMode mode,
        RearKeyConfiguration configuration,
        long ticks)
    {
        RearKeyProfile? profile = configuration.ForMode(mode);
        bool profileChanged = !ReferenceEquals(current, configuration) &&
            !ProfilesEquivalent(current?.ForMode(currentMode), profile);
        if (profileChanged || currentMode != mode || ticks < lastTicks ||
            (current != null && ticks - lastTicks > Stopwatch.Frequency / 10))
        {
            left.Reset();
            right.Reset();
            center.Reset();
            touchOwner = -1;
        }

        current = configuration;
        currentMode = mode;
        lastTicks = ticks;
        lastPhysical = physical;
        lastMapped = GamepadButtons.None;
        lastTouchActive = false;

        if (profile == null || (!profile.Left.Enabled && !profile.Right.Enabled && !profile.C.Enabled))
        {
            return physical;
        }

        bool ps5 = mode is ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.DualSenseProfessionalImuTest;
        GamepadButtons mapped =
            left.Get(profile.Left, physical.IsPressed(GamepadButtons.PaddleLeft), ticks, ps5) |
            right.Get(profile.Right, physical.IsPressed(GamepadButtons.PaddleRight), ticks, ps5) |
            center.Get(profile.C, physical.IsPressed(GamepadButtons.Aux), ticks, ps5);

        Ps5TouchPoint touch = ResolveTouch(ps5, physical.Ps5Touch);
        // 物理方向优先；两条宏产生的相反方向互相抵消，避免出现非法十字键和弦。
        if (physical.IsPressed(GamepadButtons.DPadUp)) mapped &= ~GamepadButtons.DPadDown;
        if (physical.IsPressed(GamepadButtons.DPadDown)) mapped &= ~GamepadButtons.DPadUp;
        if (physical.IsPressed(GamepadButtons.DPadLeft)) mapped &= ~GamepadButtons.DPadRight;
        if (physical.IsPressed(GamepadButtons.DPadRight)) mapped &= ~GamepadButtons.DPadLeft;
        mapped = RearKeyBinding.SafeButtons(mapped);
        lastMapped = mapped;
        if (mapped != GamepadButtons.None || lastTouchActive)
        {
            lastPhysical = physical.Clone();
        }

        GamepadButtons buttons = physical.Buttons;
        if (profile.Left.Enabled) buttons &= ~GamepadButtons.PaddleLeft;
        if (profile.Right.Enabled) buttons &= ~GamepadButtons.PaddleRight;
        if (profile.C.Enabled) buttons &= ~GamepadButtons.Aux;
        buttons |= mapped;
        if (buttons == physical.Buttons && touch == physical.Ps5Touch &&
            ((mapped & GamepadButtons.L2) == 0 || physical.L2 == GamepadState.TriggerMax) &&
            ((mapped & GamepadButtons.R2) == 0 || physical.R2 == GamepadState.TriggerMax))
        {
            return physical;
        }

        GamepadState result = physical.Clone();
        result.Buttons = buttons; // OR 保留玩家独立按住的面键/肩键。
        result.Ps5Touch = touch;
        if ((mapped & GamepadButtons.L2) != 0) result.L2 = GamepadState.TriggerMax;
        if ((mapped & GamepadButtons.R2) != 0) result.R2 = GamepadState.TriggerMax;
        return result;
    }

    private Ps5TouchPoint ResolveTouch(bool ps5, Ps5TouchPoint physicalTouch)
    {
        if (!ps5)
        {
            lastTouchActive = false;
            return physicalTouch;
        }

        Ps5TouchFrame l = left.Touch, r = right.Touch, c = center.Touch;
        if (touchOwner < 0)
        {
            if (l.Started) touchOwner = 0;
            else if (r.Started) touchOwner = 1;
            else if (c.Started) touchOwner = 2;
        }

        if (l.Started && touchOwner != 0) touchSuppressed++;
        if (r.Started && touchOwner != 1) touchSuppressed++;
        if (c.Started && touchOwner != 2) touchSuppressed++;

        if (touchOwner < 0)
        {
            lastTouchActive = false;
            return physicalTouch;
        }

        Ps5TouchFrame owned = touchOwner == 0 ? l : touchOwner == 1 ? r : c;
        if (owned.Started) touchStarts++;
        if (!owned.Point.Active)
        {
            touchReleases++;
            touchOwner = -1;
        }

        lastTouchActive = owned.Point.Active && owned.Point != physicalTouch;
        return owned.Point;
    }
}
