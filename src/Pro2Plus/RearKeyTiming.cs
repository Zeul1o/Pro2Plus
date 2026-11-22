using System;

namespace Pro2Plus;

/// <summary>某一帧应当产生的输出；Finished 表示这条动作已经走完，需要释放通道。</summary>
internal readonly record struct RearKeyPlan(
    GamepadButtons Buttons,
    long Pulse,
    Ps5TouchGesture Gesture,
    double Fraction,
    bool Finished)
{
    internal static RearKeyPlan Neutral => new(GamepadButtons.None, 0, Ps5TouchGesture.None, 0, false);
    internal static RearKeyPlan Done => new(GamepadButtons.None, 0, Ps5TouchGesture.None, 0, true);
}

/// <summary>
/// 纯时序规划：把「按下后经过多少毫秒」映射成这一帧要发的按键与手势进度。
/// 不看真实时间、不碰状态、不产生触摸帧，所有分支可离线差分。
/// </summary>
internal static class RearKeyTiming
{
    internal static RearKeyPlan Plan(RearKeyBinding binding, double elapsedMs)
    {
        switch (binding.Action)
        {
            case RearKeyAction.Hold:
                // 按住型没有终点：超过设定时长后只停止手势，按键继续保持。
                return new RearKeyPlan(
                    binding.Buttons,
                    0,
                    elapsedMs < binding.PressMs ? binding.TouchGesture : Ps5TouchGesture.None,
                    elapsedMs / binding.PressMs,
                    false);

            case RearKeyAction.Tap:
                return elapsedMs < binding.PressMs
                    ? new RearKeyPlan(binding.Buttons, 0, binding.TouchGesture, elapsedMs / binding.PressMs, false)
                    : RearKeyPlan.Done;

            case RearKeyAction.Repeat:
            {
                double period = binding.PressMs + binding.GapMs;
                double cycle = Math.Floor(elapsedMs / period);
                if (binding.RepeatCount != 0 && cycle >= binding.RepeatCount)
                {
                    return RearKeyPlan.Done;
                }

                double phase = elapsedMs % period;
                return phase < binding.PressMs
                    ? new RearKeyPlan(binding.Buttons, (long)cycle, binding.TouchGesture, phase / binding.PressMs, false)
                    : RearKeyPlan.Neutral;
            }

            case RearKeyAction.Sequence:
            {
                double remaining = elapsedMs;
                if (remaining < binding.PressMs)
                {
                    return new RearKeyPlan(binding.Buttons, 0, binding.TouchGesture, remaining / binding.PressMs, false);
                }

                remaining -= binding.PressMs;
                long stepIndex = 1;
                foreach (RearKeyStep step in binding.Steps)
                {
                    if (remaining < step.DelayMs)
                    {
                        return RearKeyPlan.Neutral;
                    }

                    remaining -= step.DelayMs;
                    if (remaining < step.PressMs)
                    {
                        return new RearKeyPlan(step.Buttons, stepIndex, step.TouchGesture, remaining / step.PressMs, false);
                    }

                    remaining -= step.PressMs;
                    stepIndex++;
                }

                return RearKeyPlan.Done;
            }

            default:
                return RearKeyPlan.Done;
        }
    }
}
