namespace Pro2Plus;

public sealed record EnvironmentFacts(bool IsWindows, int Build,
    bool RequestParametersApiAvailable, bool ReadParametersApiAvailable,
    string StackSha256, bool Win10StackValuesConfigured);

public enum ConnectionRoute { Unsupported, Windows11PublicApi, Win10AuditedStack, NativeNotifyOnly, Win10ExperimentalStack }

public sealed record ConnectionPlan(ConnectionRoute Route, string Title, string Guidance,
    bool CanRequestPreferredParameters, bool CanReadConnectionParameters, bool CanConfigureWin10Stack);

/// <summary>
/// 根据系统版本、公开 API 可用性与蓝牙栈指纹选择连接策略。
/// 这里只做判定：不写注册表、不重启适配器，也不承诺任何输入频率。
/// </summary>
public static class ConnectionRoutePolicy
{
    public static ConnectionPlan Select(EnvironmentFacts facts)
    {
        if (!facts.IsWindows || facts.Build < 19041)
            return new(ConnectionRoute.Unsupported, "此系统不在支持范围",
                "最低要求 Windows 10 2004（19041）x64。本程序不保证旧系统能够启动或达到 125Hz。", false, false, false);
        if (facts.Build >= 22000)
        {
            if (facts.RequestParametersApiAvailable)
                return new(ConnectionRoute.Windows11PublicApi, "Win11 · 分层连接策略",
                    "启用自动优化时，先测原连接；低速则在同一连接内最多试 4 个预设并复核较好结果。自定义 7.5ms 参数是兼容尝试，可能被系统拒绝；无效则继续寻找可玩路线。记忆只调整精确环境的重测顺序，不把 66Hz 算作 125Hz、不写 Win10 全局参数。", true,
                    facts.ReadParametersApiAvailable, false);
            return new(ConnectionRoute.NativeNotifyOnly, "Win11 · 公开 API 不可用",
                "保留原生 BLE 连接并按真实 Notify 验收，不盲改注册表。低速时导出诊断，检查官方蓝牙驱动及系统组件。", false,
                facts.ReadParametersApiAvailable, false);
        }
        if (BleStackProfile.IsAudited(facts.Build, facts.StackSha256))
            return new(ConnectionRoute.Win10AuditedStack, "Win10 · 已验证的系统栈",
                facts.Win10StackValuesConfigured
                    ? "已检测到固定 7.5ms 配置；普通启动不重复改参数或重启蓝牙。不同适配器仍以真实 Notify 验收。"
                    : "先连接并实测；速率不足时可明确授权启用可恢复的 Win10 参数。不会自动修改其他 BLE 设备的全局默认值。",
                false, false, true);
        return new(ConnectionRoute.Win10ExperimentalStack, "Win10 · 可恢复参数实验",
            "未知蓝牙栈不等于不支持参数。可明确授权试验 7.5ms / 7.5–15ms / 15ms：先检查本机系统文件、备份四项全局值，再保存并核对重启结果。参数机制尚未审查，写入成功不等于生效；按真实 FD2 比较，支持恢复，不更换驱动。",
            false, false, true);
    }
}
