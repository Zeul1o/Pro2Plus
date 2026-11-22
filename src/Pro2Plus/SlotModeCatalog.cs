using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

public sealed record SlotModeOption(ViiperVirtualMode Mode, string Key, string Label);

/// <summary>
/// 槽位输出模式的目录与查找。只有五个候选项，线性查找即可，避免每次
/// 都构造 LINQ 谓词闭包；四个槽位的持久化数组也直接按索引填充。
/// </summary>
public static class SlotModeCatalog
{
    public static IReadOnlyList<SlotModeOption> Options { get; } =
    [
        new(ViiperVirtualMode.Pro2, "pro2", "Steam / Pro2"),
        new(ViiperVirtualMode.Pro1, "pro1", "Pro1"),
        new(ViiperVirtualMode.Xbox, "xbox", "Xbox"),
        new(ViiperVirtualMode.DualSenseLike, "dualsense", "PS5"),
        new(ViiperVirtualMode.KeyMouse, "keymouse", "键鼠")
    ];

    public static SlotModeOption FromKey(string? key)
    {
        string normalized = AppSettings.NormalizeModeKey(key);
        foreach (SlotModeOption option in Options)
        {
            if (option.Key == normalized) return option;
        }
        return Options[0];
    }

    public static SlotModeOption FromMode(ViiperVirtualMode mode)
    {
        foreach (SlotModeOption option in Options)
        {
            if (option.Mode == mode) return option;
        }
        return Options[0];
    }

    public static string[] Normalize(string[]? saved, string primaryKey)
    {
        string fallback = FromKey(primaryKey).Key;
        var result = new string[4];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = saved != null && i < saved.Length && !string.IsNullOrWhiteSpace(saved[i])
                ? FromKey(saved[i]).Key : fallback;
        }
        return result;
    }

    public static ViiperDeviceProfile Profile(ViiperVirtualMode mode, int slot, OutputRateOption cadence)
    {
        if (slot is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(slot));
        ViiperDeviceProfile profile = mode switch
        {
            ViiperVirtualMode.Pro2 => ViiperDeviceProfile.Pro2,
            ViiperVirtualMode.Pro1 => ViiperDeviceProfile.Pro1,
            ViiperVirtualMode.Xbox => ViiperDeviceProfile.Xbox,
            ViiperVirtualMode.DualSenseLike => ViiperDeviceProfile.DualSenseLike,
            ViiperVirtualMode.KeyMouse => ViiperDeviceProfile.KeyMouse,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return profile with
        {
            Label = profile.Label + " Slot " + slot,
            DeviceSpecificSerialNumber = ViiperDeviceProfile.SlotSerialNumber(mode, slot),
            SendInterval = cadence.Interval,
            SourcePaced = cadence.SourcePaced
        };
    }
}

public enum SlotModeChangeResult { Changed, RestoredPrevious, OutputStopped }

/// <summary>
/// 只负责所选槽位的虚拟输出生命周期；物理蓝牙连接与兄弟会话不在这个事务里。
/// 创建新模式失败时尽力恢复此前已知的模式，全部失败才上报输出已停止。
/// </summary>
public static class SlotModeSwitch
{
    public static async Task<SlotModeChangeResult> ReplaceAsync(ViiperVirtualMode? previous,
        ViiperVirtualMode requested, Func<Task> stopSlot,
        Func<ViiperVirtualMode, CancellationToken, Task<bool>> startSlot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await stopSlot();
        try
        {
            if (await startSlot(requested, token)) return SlotModeChangeResult.Changed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { /* 创建失败后恢复此前已知的模式 */ }
        token.ThrowIfCancellationRequested();
        if (previous.HasValue && await startSlot(previous.Value, token)) return SlotModeChangeResult.RestoredPrevious;
        return SlotModeChangeResult.OutputStopped;
    }
}
