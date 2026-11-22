using System;

namespace Pro2Plus;

/// <summary>
/// 体感帧的纯变换：清零、缩放、一阶低通、搬移。每次都返回新状态，
/// 不改写调用方持有的帧；不读取时间、不碰线程状态。
/// </summary>
internal static class MotionFrameTransform
{
    internal static GamepadState WithoutMotion(GamepadState state)
    {
        GamepadState result = state.Clone();
        ClearAxes(result);
        result.GyroValid = false;
        result.AccelValid = false;
        return result;
    }

    internal static GamepadState ScaleGyro(GamepadState state, double scale)
    {
        if (!state.GyroValid || Math.Abs(scale - 1.0) < 0.001)
        {
            return state;
        }

        GamepadState result = state.Clone();
        result.GyroX = ScaleInt16(result.GyroX, scale);
        result.GyroY = ScaleInt16(result.GyroY, scale);
        result.GyroZ = ScaleInt16(result.GyroZ, scale);
        return result;
    }

    internal static GamepadState FilterMotion(GamepadState? previous, GamepadState current)
    {
        if (previous == null)
        {
            return current.Clone();
        }

        const double alpha = 0.35;
        GamepadState filtered = current.Clone();
        if (current.GyroValid && previous.GyroValid)
        {
            filtered.GyroX = LerpInt16(previous.GyroX, current.GyroX, alpha);
            filtered.GyroY = LerpInt16(previous.GyroY, current.GyroY, alpha);
            filtered.GyroZ = LerpInt16(previous.GyroZ, current.GyroZ, alpha);
        }

        if (current.AccelValid && previous.AccelValid)
        {
            filtered.AccelX = LerpInt16(previous.AccelX, current.AccelX, alpha);
            filtered.AccelY = LerpInt16(previous.AccelY, current.AccelY, alpha);
            filtered.AccelZ = LerpInt16(previous.AccelZ, current.AccelZ, alpha);
        }

        return filtered;
    }

    internal static GamepadState CopyMotion(GamepadState current, GamepadState? motion)
    {
        if (motion == null)
        {
            return current;
        }

        GamepadState result = current.Clone();
        result.GyroValid = motion.GyroValid;
        result.GyroX = motion.GyroX;
        result.GyroY = motion.GyroY;
        result.GyroZ = motion.GyroZ;
        result.AccelValid = motion.AccelValid;
        result.AccelX = motion.AccelX;
        result.AccelY = motion.AccelY;
        result.AccelZ = motion.AccelZ;
        result.MotionTimestampUs = motion.MotionTimestampUs;
        return result;
    }

    internal static short ScaleInt16(short value, double scale) =>
        ClampInt16((int)Math.Round(value * scale));

    internal static short LerpInt16(short previous, short current, double alpha) =>
        ClampInt16((int)Math.Round(previous + (current - previous) * alpha));

    internal static short ClampInt16(int value)
    {
        if (value < short.MinValue) return short.MinValue;
        if (value > short.MaxValue) return short.MaxValue;
        return (short)value;
    }

    private static void ClearAxes(GamepadState state)
    {
        state.GyroX = 0;
        state.GyroY = 0;
        state.GyroZ = 0;
        state.AccelX = 0;
        state.AccelY = 0;
        state.AccelZ = 0;
    }
}
