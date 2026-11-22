using System;
using System.Text.Json;

namespace Pro2Plus;

/// <summary>
/// 实验室采集文件的 JSONL 结构：第一行表头，之后每行一帧。
/// 键名是下游分析脚本的契约，集中在这里，并由自检用指纹锁住。
/// </summary>
internal static class ResearchCaptureFormat
{
    public const string HeaderType = "research_capture";
    public const string FrameType = "frame";
    public const string SourceName = "Pro2 BLE FD2";

    public static string Header(DateTimeOffset created, string environmentVariable) =>
        JsonSerializer.Serialize(new
        {
            type = HeaderType,
            created,
            source = SourceName,
            environment_variable = environmentVariable
        });

    public static string Frame(ulong sequence, DateTimeOffset timestamp, double deltaMs, ReadOnlySpan<byte> raw) =>
        JsonSerializer.Serialize(new
        {
            type = FrameType,
            frame = new
            {
                FrameIndex = sequence,
                Timestamp = timestamp,
                DeltaMsFromPreviousFrame = deltaMs,
                RawFd2Hex = Convert.ToHexString(raw)
            }
        });
}
