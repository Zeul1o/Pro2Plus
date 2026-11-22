using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

public sealed record InitialBatterySnapshotResult(
    byte[]? Payload, bool SubscriptionReleased, string Status, string CleanupStatus);

/// <summary>
/// 在启用 FD2 之前完成的一次电量快照。纯软件发布默认开启；
/// 没有游戏回调、后台查询、重试定时器，也不做电压到 SOC 的估算。
/// 无论订阅成功、被拒还是超时，都必须用独立 token 尝试撤销订阅。
/// </summary>
public static class InitialBatterySnapshot
{
    public const string EnableArgument = "--battery-init-snapshot-test";
    public const string DisableArgument = "--battery-snapshot-off-test";

    public static bool IsRequested(string[] arguments) =>
        ResolveRequested(arguments, SoftwareOnlyMode.IsReleaseBuild);

    public static bool ResolveRequested(string[] arguments, bool defaultEnabled) =>
        !Contains(arguments, DisableArgument) &&
        (defaultEnabled || Contains(arguments, EnableArgument));

    private static bool Contains(string[] arguments, string argument)
    {
        // 手写比较，避免每次启动都构造 LINQ 比较器。
        foreach (string value in arguments)
        {
            if (string.Equals(value, argument, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool IsValid(byte[] payload)
    {
        // 全零回包不是有效读数；必须能解析出合法档位。
        bool anyNonZero = false;
        foreach (byte value in payload)
        {
            if (value != 0) { anyNonZero = true; break; }
        }
        return anyNonZero && BatteryLevelReading.TryDecodePrimary(payload, out var battery) && battery.HasLevel;
    }

    public static async Task<InitialBatterySnapshotResult> CaptureAsync(
        Func<CancellationToken, Task<bool>> enable,
        Func<CancellationToken, Task<byte[]>> receive,
        Func<CancellationToken, Task<bool>> disableAndVerify,
        CancellationToken token, TimeSpan? timeout = null)
    {
        token.ThrowIfCancellationRequested();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(2);
        byte[]? payload = null;
        string status = "not_started", cleanupStatus = "not_started";
        bool released = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(limit);
        try
        {
            if (await enable(deadline.Token))
            {
                byte[] received = await receive(deadline.Token);
                status = IsValid(received) ? "captured" : "invalid_report";
                if (status == "captured") payload = received;
            }
            else status = "subscribe_rejected";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { status = "timeout"; }
        catch (Exception ex) { status = "error_" + ex.GetType().Name; }
        finally
        {
            // 被拒或超时的订阅也可能留下远端订阅，清理用独立 token 以免被取消绕过。
            using var cleanup = new CancellationTokenSource(limit);
            try
            {
                released = await disableAndVerify(cleanup.Token);
                cleanupStatus = released ? "none_verified" : "none_not_verified";
            }
            catch (Exception ex) { cleanupStatus = "error_" + ex.GetType().Name; }
        }
        return new(payload, released, status, cleanupStatus);
    }
}
