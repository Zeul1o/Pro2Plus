using System;

namespace Pro2Plus;

/// <summary>
/// 诊断侧保持到达顺序，实时输出侧在每个「按键/扳机边沿组」内只取最新轴值。
/// 用定长环形缓冲替代链式队列：容量固定后不再扩容，也不为每个帧节点分配对象。
/// </summary>
public sealed class SequentialFrameQueue
{
    private readonly Entry[] buffer;
    private readonly int capacity;
    private int head;
    private int count;

    public SequentialFrameQueue(int capacity = 64)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        buffer = new Entry[capacity];
    }

    public int Count => count;
    public int MaximumDepth { get; private set; }
    public ulong EnqueuedCount { get; private set; }
    public ulong DequeuedCount { get; private set; }
    public ulong OverflowDropCount { get; private set; }
    public ulong RealtimeSupersededCount { get; private set; }

    public void Enqueue(GamepadState state, long arrivalTicks)
    {
        if (count >= capacity)
        {
            AdvanceHead();
            count--;
            OverflowDropCount++;
        }

        int slot = head + count;
        if (slot >= capacity) slot -= capacity;
        buffer[slot] = new Entry(state.Clone(), arrivalTicks);
        count++;
        EnqueuedCount++;
        if (count > MaximumDepth) MaximumDepth = count;
    }

    public bool TryDequeue(out GamepadState state, out long arrivalTicks)
    {
        if (count == 0)
        {
            state = GamepadState.Neutral();
            arrivalTicks = 0;
            return false;
        }

        Entry entry = Take();
        state = entry.State;
        arrivalTicks = entry.ArrivalTicks;
        return true;
    }

    public bool TryDequeueNewest(out GamepadState state, out long arrivalTicks, out int supersededCount)
    {
        supersededCount = 0;
        if (count == 0)
        {
            state = GamepadState.Neutral();
            arrivalTicks = 0;
            return false;
        }

        while (count > 1)
        {
            Take();
            RealtimeSupersededCount++;
            supersededCount++;
        }

        Entry newest = Take();
        state = newest.State;
        arrivalTicks = newest.ArrivalTicks;
        return true;
    }

    /// <summary>
    /// Windows 可能把一次批量上报塞进同一个回调：只要数字控制完全相同就合并轴值，
    /// 但按下/抬起的边沿必须逐帧保留。
    /// </summary>
    public bool TryDequeueRealtimeEdges(out GamepadState state, out long arrivalTicks,
        out int supersededCount)
    {
        supersededCount = 0;
        if (count == 0)
        {
            state = GamepadState.Neutral();
            arrivalTicks = 0;
            return false;
        }

        Entry entry = Take();
        while (TryPeek(out Entry next) &&
               next.State.Buttons == entry.State.Buttons &&
               (next.State.L2 > 0) == (entry.State.L2 > 0) &&
               (next.State.R2 > 0) == (entry.State.R2 > 0))
        {
            entry = Take();
            RealtimeSupersededCount++;
            supersededCount++;
        }

        state = entry.State;
        arrivalTicks = entry.ArrivalTicks;
        return true;
    }

    public void Reset()
    {
        Array.Clear(buffer, 0, buffer.Length);
        head = 0;
        count = 0;
        MaximumDepth = 0;
        EnqueuedCount = 0;
        DequeuedCount = 0;
        OverflowDropCount = 0;
        RealtimeSupersededCount = 0;
    }

    public void ResetTo(GamepadState state, long arrivalTicks)
    {
        Reset();
        Enqueue(state, arrivalTicks);
    }

    private void AdvanceHead() => head = head + 1 == capacity ? 0 : head + 1;

    private bool TryPeek(out Entry entry)
    {
        if (count == 0)
        {
            entry = default;
            return false;
        }

        entry = buffer[head];
        return true;
    }

    private Entry Take()
    {
        Entry entry = buffer[head];
        buffer[head] = default;
        AdvanceHead();
        count--;
        DequeuedCount++;
        return entry;
    }

    private readonly record struct Entry(GamepadState State, long ArrivalTicks);
}
