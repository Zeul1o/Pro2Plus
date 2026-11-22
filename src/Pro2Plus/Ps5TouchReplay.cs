using System;

namespace Pro2Plus;

internal readonly record struct Ps5TouchFrame(Ps5TouchPoint Point, bool Started = false);

/// <summary>
/// 手势播放：只跟随新鲜源帧，显式给出终点与抬手。没有定时器、后台队列，
/// 也不补播丢失的手势。
/// </summary>
internal sealed class Ps5TouchPlayback
{
    private Ps5TouchGesture gesture;
    private long pulseToken;
    private long finishedToken;
    private bool finished;
    private double progress;
    private Ps5TouchPoint point;

    public bool HasPending => point.Active;

    public void Reset()
    {
        gesture = Ps5TouchGesture.None;
        pulseToken = 0;
        finishedToken = 0;
        finished = false;
        progress = 0;
        point = default;
    }

    public Ps5TouchFrame Cancel()
    {
        if (point.Active)
        {
            finishedToken = pulseToken;
            finished = true;
        }

        point = point with { Active = false };
        return new Ps5TouchFrame(point);
    }

    public Ps5TouchFrame Sample(Ps5TouchGesture requested, long token, double fraction)
    {
        if (point.Active)
        {
            return Advance(requested, token, fraction);
        }

        // 同一个已结束的脉冲不再重放：必须是新脉冲或新手势才能重新按下。
        if (requested == Ps5TouchGesture.None || (finished && token == finishedToken))
        {
            return new Ps5TouchFrame(point);
        }

        gesture = requested;
        pulseToken = token;
        progress = 0;
        point = Ps5TouchGestures.PointAt(gesture, 0);
        return new Ps5TouchFrame(point, Started: true);
    }

    private Ps5TouchFrame Advance(Ps5TouchGesture requested, long token, double fraction)
    {
        if (requested == gesture && token == pulseToken)
        {
            // 进度只增不减：源帧乱序或重复都不会把手指拉回去。
            progress = Math.Max(progress, Math.Clamp(fraction, 0, 1));
            point = Ps5TouchGestures.PointAt(gesture, progress);
            return new Ps5TouchFrame(point);
        }

        // 源窗口偏短或来迟：先补一个终点，再抬手。
        // 新手势只能从抬手之后开始，不能中途插入。
        if (progress < 1)
        {
            progress = 1;
            point = Ps5TouchGestures.PointAt(gesture, 1);
            return new Ps5TouchFrame(point);
        }

        return Cancel();
    }
}
