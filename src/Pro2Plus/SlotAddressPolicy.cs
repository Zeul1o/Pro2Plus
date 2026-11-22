using System;
using System.Collections.Generic;

namespace Pro2Plus;

public sealed record SlotAddressInfo(int Index, bool Enabled, string SavedAddress, string ConnectedAddress, bool Occupied);

/// <summary>
/// 已取得连接闸门后才调用：休眠或离线的已启用槽位仍归原槽所有，不被其它槽抢走。
/// 一次收集、两次线性扫描，省掉 LINQ 的迭代器与谓词闭包分配。
/// </summary>
public static class SlotAddressPolicy
{
    public static HashSet<string> Exclusions(int index, IEnumerable<SlotAddressInfo> slots)
    {
        var all = new List<SlotAddressInfo>();
        foreach (SlotAddressInfo slot in slots) all.Add(slot);

        string own = "";
        foreach (SlotAddressInfo slot in all)
        {
            if (slot.Index == index)
            {
                own = slot.SavedAddress ?? "";
                break;
            }
        }

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SlotAddressInfo slot in all)
        {
            if (slot.Index == index) continue;
            if (slot.Occupied && !string.IsNullOrWhiteSpace(slot.ConnectedAddress))
                excluded.Add(slot.ConnectedAddress);
            if (slot.Enabled && !string.IsNullOrWhiteSpace(slot.SavedAddress) &&
                (!slot.SavedAddress.Equals(own, StringComparison.OrdinalIgnoreCase) || slot.Index < index))
                excluded.Add(slot.SavedAddress);
        }
        return excluded;
    }
}

public sealed record PeerRateSample(int Slot, string Address, bool Connected, bool Fresh, double Hz);

public static class PeerRateGuard
{
    public static bool Healthy(IReadOnlyList<PeerRateSample> before, IReadOnlyList<PeerRateSample> now)
    {
        foreach (PeerRateSample peer in before)
        {
            if (!peer.Connected || !peer.Fresh || !double.IsFinite(peer.Hz) || peer.Hz <= 0) continue;

            PeerRateSample? after = null;
            foreach (PeerRateSample candidate in now)
            {
                if (candidate.Slot == peer.Slot &&
                    candidate.Address.Equals(peer.Address, StringComparison.OrdinalIgnoreCase))
                {
                    after = candidate;
                    break;
                }
            }

            if (after == null || !after.Connected || !after.Fresh || !double.IsFinite(after.Hz) ||
                after.Hz < peer.Hz - Math.Max(8, peer.Hz * .2)) return false;
        }
        return true;
    }
}
