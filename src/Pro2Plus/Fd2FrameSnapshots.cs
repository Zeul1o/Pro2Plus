using System;
using System.Collections.Generic;
using System.Linq;

namespace Pro2Plus;

/// <summary>一帧里的四个摇杆轴值（原始或滤波后）。</summary>
public sealed record Pro2AxisSnapshot(ushort Lx, ushort Ly, ushort Rx, ushort Ry);

/// <summary>一帧里的加速度与陀螺读数，带各自的“有效”标记。</summary>
public sealed record Pro2MotionSnapshot(
    bool AccelValid,
    bool GyroValid,
    short AccelX,
    short AccelY,
    short AccelZ,
    short GyroX,
    short GyroY,
    short GyroZ);

/// <summary>滤波器的单次决策记录，用于事后复盘“这一帧为什么被改/被吞”。</summary>
public sealed record Pro2FrameFilterEventSnapshot(
    string AxisName,
    string Decision,
    string Reason,
    ushort OldValue,
    ushort NewValue,
    ushort OutputValue,
    int Delta,
    double StickVectorDelta,
    int ConsecutiveSuspectFrames,
    double CandidateAgeMs,
    double FrameDeltaMs,
    bool DirectionStable,
    bool Continuous,
    string MotionClass,
    bool ActiveMotion,
    bool FastReversal,
    bool CenterCrossing,
    bool InputSwallowed,
    int RawToFilteredDelta);

/// <summary>环形缓冲里保存的一帧完整快照（原始字节 + 解析结果 + 滤波决策）。</summary>
public sealed record Pro2Fd2FrameSnapshot(
    ulong FrameIndex,
    DateTimeOffset Timestamp,
    double DeltaMsFromPreviousFrame,
    string ReportType,
    int ReportLen,
    string RawFd2Hex,
    Pro2AxisSnapshot RawAxes,
    Pro2AxisSnapshot FilteredAxes,
    ulong Buttons,
    Pro2MotionSnapshot Motion,
    bool ParseOk,
    string ParseSource,
    string FilterResult,
    IReadOnlyList<Pro2FrameFilterEventSnapshot> FilterEvents);

/// <summary>一次轴突跳的现场信息：触发时刻的链路状态、速率与相邻帧的上下文。</summary>
public sealed record Pro2AxisSpikeTelemetry(
    DateTimeOffset Timestamp,
    string ReportType,
    int ReportLen,
    string AxisName,
    ushort OldValue,
    ushort NewValue,
    ushort OutputValue,
    int Delta,
    ushort OldLeftX,
    ushort OldLeftY,
    ushort OldRightX,
    ushort OldRightY,
    ushort NewLeftX,
    ushort NewLeftY,
    ushort NewRightX,
    ushort NewRightY,
    double StickVectorDelta,
    double SourceAgeMs,
    double BleGapMs,
    int ConsecutiveSuspectFrames,
    string AcceptedOrRejected,
    string Reason,
    string RawFd2Hex,
    ulong ParseSeq,
    ulong StateSeq,
    double BleHz,
    double ViiperPushHz,
    double CandidateAgeMs,
    double FrameDeltaMs,
    bool DirectionStable,
    bool Continuous,
    string MotionClass,
    bool ActiveMotion,
    bool FastReversal,
    bool CenterCrossing,
    bool InputSwallowed,
    int RawToFilteredDelta);

/// <summary>把运行时状态压成可序列化快照；只做取值，不改状态。</summary>
public static class Pro2SpikeSnapshot
{
    public static Pro2AxisSnapshot Axes(GamepadState state) =>
        new(state.Lx, state.Ly, state.Rx, state.Ry);

    public static Pro2MotionSnapshot Motion(GamepadState state) =>
        new(
            state.AccelValid,
            state.GyroValid,
            state.AccelX,
            state.AccelY,
            state.AccelZ,
            state.GyroX,
            state.GyroY,
            state.GyroZ);

    public static IReadOnlyList<Pro2FrameFilterEventSnapshot> Events(
        IReadOnlyList<Pro2AxisFilterEvent> events) =>
        events.Select(e => new Pro2FrameFilterEventSnapshot(
            e.AxisName, e.Decision.ToString(), e.Reason, e.OldValue, e.NewValue, e.OutputValue,
            e.Delta, e.StickVectorDelta, e.ConsecutiveSuspectFrames, e.CandidateAgeMs, e.FrameDeltaMs,
            e.DirectionStable, e.Continuous, e.MotionClass, e.ActiveMotion, e.FastReversal,
            e.CenterCrossing, e.InputSwallowed, e.RawToFilteredDelta)).ToArray();
}
