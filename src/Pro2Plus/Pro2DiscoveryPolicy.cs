using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

/// <summary>
/// Connection-only discovery acceleration. A saved address must be advertising
/// now; an unrecognised Nintendo manufacturer flag alone never ends a scan.
/// Manual lists and explicit diagnostics do not construct this policy.
/// </summary>
public sealed class Pro2DiscoveryPolicy
{
    private readonly HashSet<string> preferred;
    private readonly HashSet<string> excluded;
    private readonly bool onlyPreferred;
    public static TimeSpan CollectionWindow => TimeSpan.FromMilliseconds(250);

    public static bool AllowsEarlyCompletion(IEnumerable<string> arguments, bool isolatedCustomExperiment)
    {
        if (isolatedCustomExperiment) return false;
        foreach (string argument in arguments)
            if (argument.Equals("--ble-diagnostic", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public Pro2DiscoveryPolicy(IEnumerable<string> preferred, IEnumerable<string> excluded, bool onlyPreferred)
    {
        this.preferred = new(preferred, StringComparer.OrdinalIgnoreCase);
        this.excluded = new(excluded, StringComparer.OrdinalIgnoreCase);
        this.onlyPreferred = onlyPreferred;
    }

    public bool IsFreshSavedTarget(string address) =>
        !excluded.Contains(address) && preferred.Contains(address);

    /// <summary>
    /// 是否已经有保存过的目标地址。有的话可以先用被动扫描直取，
    /// 不必从主动扫描的完整窗口等起。
    /// </summary>
    public bool HasPreferredTargets => preferred.Count > 0;

    public string? EarlyCompletionReason(string address, string? name, bool nintendoManufacturer, int score = 0)
    {
        if (excluded.Contains(address)) return null;
        if (preferred.Contains(address)) return "saved_address_advertisement";
        if (onlyPreferred && preferred.Count > 0) return null;
        if (!nintendoManufacturer) return null;
        // 名字里明确写了 Pro 2 是最可靠的信号。
        if (HasExplicitPro2Name(name)) return "named_pro2_advertisement";
        // 实际广播名常常只是 "Pro Controller" 之类，不含 "2"。
        // Nintendo 厂商位 + 名称带 Pro/Controller（评分 ≥70）已经足够像一只 Pro 系手柄，
        // 值得直接结束扫描；连接阶段仍会校验服务 UUID，选错会自动落回继续等。
        if (score >= 70) return "pro_controller_score_advertisement";
        return null;
    }

    private static bool HasExplicitPro2Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (string token in new[] { "pro2", "pro 2" })
        {
            int start = 0;
            while ((start = name.IndexOf(token, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int end = start + token.Length;
                if ((start == 0 || !char.IsLetterOrDigit(name[start - 1])) &&
                    (end == name.Length || !char.IsLetterOrDigit(name[end]))) return true;
                start = end;
            }
        }
        return false;
    }

    public static async Task<string> WaitForScanCompletionAsync(TimeSpan duration,
        Task<string> earlyCandidate, Task watcherStopped, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task fullWindowWait = Task.Delay(duration, waitCts.Token);
        Task? collectionWait = null;
        try
        {
            Task completed = await Task.WhenAny(fullWindowWait, watcherStopped, earlyCandidate);
            cancellationToken.ThrowIfCancellationRequested();
            if (completed == earlyCandidate)
            {
                string reason = await earlyCandidate;
                collectionWait = Task.Delay(CollectionWindow, waitCts.Token);
                Task collectionCompleted = await Task.WhenAny(collectionWait, fullWindowWait, watcherStopped);
                cancellationToken.ThrowIfCancellationRequested();
                return collectionCompleted == fullWindowWait ? "full_window" :
                    collectionCompleted == watcherStopped ? "watcher_stopped" : reason;
            }
            return completed == watcherStopped ? "watcher_stopped" : "full_window";
        }
        finally
        {
            // Observe and cancel both timers; no background continuation is
            // retained after a short connection scan, stop or cancellation.
            waitCts.Cancel();
            try { await fullWindowWait; } catch (OperationCanceledException) { }
            if (collectionWait != null) { try { await collectionWait; } catch (OperationCanceledException) { } }
        }
    }
}
