using System;
using System.Collections.Generic;

namespace Pro2Plus;

/// <summary>一次端点检查得到的只读快照，策略层只认这三个字段。</summary>
internal sealed record EndpointSnapshot(string Id, string Name, AudioFlow Flow, bool IsController);

/// <summary>
/// 纯策略：判断某个音频端点是否属于手柄、在候选里挑迁移目标、以及拼装诊断文案。
/// 不接触 COM、注册表、设备树或文件系统，便于离线差分验证。
/// </summary>
internal static class AudioEndpointPolicy
{
    /// <summary>遍历顺序沿用诊断日志的历史顺序，缓存成静态数组避免每次枚举分配。</summary>
    internal static readonly EndpointRole[] InspectionOrder =
    [
        EndpointRole.Console,
        EndpointRole.Multimedia,
        EndpointRole.Communications
    ];

    /// <summary>挑迁移目标时优先复用其它角色的默认设备，其次才用任意活跃设备。</summary>
    private static readonly EndpointRole[] ReuseOrder =
    [
        EndpointRole.Multimedia,
        EndpointRole.Console,
        EndpointRole.Communications
    ];

    internal static bool IsControllerEndpoint(string id, string name)
    {
        // 设备 ID 与友好名拼在一起查，兼容只在一侧出现 VID/PID 的情况。
        string joined = string.Concat(id ?? "", " ", name ?? "");
        bool byIdentifiers =
            joined.Contains("dualsense", StringComparison.OrdinalIgnoreCase) ||
            joined.Contains("vid_054c", StringComparison.OrdinalIgnoreCase) ||
            joined.Contains("pid_0ce6", StringComparison.OrdinalIgnoreCase) ||
            joined.Contains("054c&0ce6", StringComparison.OrdinalIgnoreCase) ||
            (joined.Contains("054c", StringComparison.OrdinalIgnoreCase) &&
             joined.Contains("0ce6", StringComparison.OrdinalIgnoreCase));

        // 大小写不敏感比较直接作用于原串，省掉一次全串小写副本。
        return byIdentifiers ||
               (name ?? "").Contains("DualSense Wireless Controller", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "DualSense", StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="defaultsByRole">按 EndpointRole 的整数值索引，元素可为空。</param>
    internal static EndpointSnapshot? PickFallback(
        IReadOnlyList<EndpointSnapshot> active,
        EndpointSnapshot?[] defaultsByRole)
    {
        foreach (EndpointRole role in ReuseOrder)
        {
            EndpointSnapshot? candidate = defaultsByRole[(int)role];
            if (candidate is { IsController: false })
            {
                return candidate;
            }
        }

        for (int i = 0; i < active.Count; i++)
        {
            if (!active[i].IsController)
            {
                return active[i];
            }
        }

        return null;
    }

    internal static string DescribeClean() =>
        "[AUDIO_GUARD] checked defaults; DualSense is not the default playback/communication endpoint.";

    internal static string DescribeWarning(Exception ex) =>
        "[AUDIO_GUARD] warning: unable to inspect or adjust Windows audio defaults: " +
        ex.GetType().Name + ": " + ex.Message;

    internal static string DescribeNoActive(AudioFlow flow) =>
        "[AUDIO_GUARD] " + flow + " has no active endpoint.";

    internal static string DescribeCurrent(AudioFlow flow, EndpointRole role, EndpointSnapshot current) =>
        "[AUDIO_GUARD] default " + flow + "/" + role +
        "=\"" + current.Name + "\" dualsense=" + current.IsController;

    internal static string DescribeNoFallback(AudioFlow flow, EndpointRole role) =>
        "[AUDIO_GUARD] " + flow + "/" + role +
        " is DualSense, but no non-DualSense active fallback endpoint was found.";

    internal static string DescribeMoved(AudioFlow flow, EndpointRole role, EndpointSnapshot fallback) =>
        "[AUDIO_GUARD] moved default " + flow + "/" + role +
        " from DualSense to \"" + fallback.Name + "\".";

    internal static string DescribeMoveFailure(AudioFlow flow, EndpointRole role, EndpointSnapshot fallback, int hr) =>
        "[AUDIO_GUARD] failed to move default " + flow + "/" + role +
        " to \"" + fallback.Name + "\" hr=0x" + hr.ToString("x8") + ".";
}
