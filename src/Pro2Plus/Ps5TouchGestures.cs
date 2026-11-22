using System;
using System.Collections.Generic;

namespace Pro2Plus;

// 与按键位分开：手指轨迹不是触摸板点击。
public enum Ps5TouchGesture { None, Touch, SwipeUp, SwipeDown, SwipeLeft, SwipeRight, CircleClockwise, CircleCounterClockwise }

public readonly record struct Ps5TouchPoint(bool Active, ushort X, ushort Y);
public sealed record Ps5TouchGestureChoice(Ps5TouchGesture Gesture, string Label);

/// <summary>
/// 触摸板手势的几何定义：只在 1920×1080 的触摸坐标里取点，
/// 不含计时与队列逻辑（播放由 <see cref="Ps5TouchPlayback"/> 负责）。
/// </summary>
public static class Ps5TouchGestures
{
    public const int MinDurationMs = 180;
    public const int DefaultDurationMs = 400;
    public const ushort MaxX = 1919, MaxY = 1079;

    private const double CenterX = 960, CenterY = 540;
    private const double SwipeTravel = 720, SwipeHorizontal = 1400;
    private const double CircleRadiusX = 460, CircleRadiusY = 340;

    public static IReadOnlyList<Ps5TouchGestureChoice> Choices { get; } =
    [
        new(Ps5TouchGesture.None, "无"),
        new(Ps5TouchGesture.Touch, "中央轻触"),
        new(Ps5TouchGesture.SwipeUp, "向上滑动"),
        new(Ps5TouchGesture.SwipeDown, "向下滑动"),
        new(Ps5TouchGesture.SwipeLeft, "向左滑动"),
        new(Ps5TouchGesture.SwipeRight, "向右滑动"),
        new(Ps5TouchGesture.CircleClockwise, "顺时针画圈"),
        new(Ps5TouchGesture.CircleCounterClockwise, "逆时针画圈")
    ];

    /// <summary>按 0–1 的进度取手势上的一个点；越界与非法进度都夹到端点。</summary>
    public static Ps5TouchPoint PointAt(Ps5TouchGesture gesture, double progress)
    {
        double p = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        (double X, double Y) at = gesture switch
        {
            Ps5TouchGesture.SwipeUp => (CenterX, 900 - SwipeTravel * p),
            Ps5TouchGesture.SwipeDown => (CenterX, 180 + SwipeTravel * p),
            Ps5TouchGesture.SwipeLeft => (1660 - SwipeHorizontal * p, CenterY),
            Ps5TouchGesture.SwipeRight => (260 + SwipeHorizontal * p, CenterY),
            Ps5TouchGesture.CircleClockwise => Circle(p, 1),
            Ps5TouchGesture.CircleCounterClockwise => Circle(p, -1),
            _ => (CenterX, CenterY)
        };

        bool active = gesture != Ps5TouchGesture.None && Enum.IsDefined(gesture);
        return new Ps5TouchPoint(active, ToAxis(at.X, MaxX), ToAxis(at.Y, MaxY));
    }

    private static (double X, double Y) Circle(double p, int direction)
    {
        // 屏幕坐标 +Y 向下，所以正角度方向就是顺时针。从 12 点位置起画。
        double angle = (-Math.PI / 2) + (direction * 2 * Math.PI * p);
        return (CenterX + CircleRadiusX * Math.Cos(angle), CenterY + CircleRadiusY * Math.Sin(angle));
    }

    private static ushort ToAxis(double value, ushort max) =>
        (ushort)Math.Clamp((int)Math.Round(value), 0, max);
}
