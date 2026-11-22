using System;

namespace Pro2Plus;

public sealed record BleLowRateDiagnosis(bool Visible, string Code, string Title, string Summary,
    string Advice, string Evidence, bool CanOfferWin10Configuration)
{
    public static BleLowRateDiagnosis Hidden { get; } = new(false, "none", "", "", "", "", false);
}

/// <summary>Explain measured evidence, not speculate that every low rate is a hardware fault.</summary>
public static class BleLowRateDiagnoser
{
    public static BleLowRateDiagnosis Evaluate(Pro2RateEvidence rate, EnvironmentSnapshot compatibility,
        SystemBluetoothSnapshot? environment)
    {
        // 判定顺序就是产品口径：先排除「不该提示」的情况，再按证据强度逐级降级。
        BleLowRateFacts facts = BleLowRateFacts.From(rate, compatibility, environment);
        BleRateStabilitySnapshot? health = facts.Health;

        if (!rate.Connected || !rate.Running || (facts.Healthy && facts.Fresh) ||
            (facts.HealthObserved && health!.IsRecent && !health.ShouldWarnLowRate && facts.Fresh &&
                rate.DescriptorStatus == "write_success" && rate.ParsedHz >= rate.Fd2Hz * .9) ||
            rate.Phase is ConnectionPhase.Measuring or ConnectionPhase.Initializing or
                ConnectionPhase.Scanning or ConnectionPhase.Connecting) return BleLowRateDiagnosis.Hidden;

        string evidence = BleLowRateEvidence.Describe(rate, facts, environment);
        string title = rate.GameplayReady ? "可以游玩，建议优化蓝牙" : "输入偏低，暂未达到兼容门槛";
        BleLowRateDiagnosis Result(string code, string summary, string advice, bool offer = false) =>
            new(true, code, title, summary, advice, evidence, offer);

        if (!double.IsFinite(rate.LastInputAgeMs) || rate.LastInputAgeMs >= 500)
            return Result("input_stalled", "已确认：当前真实输入有停顿，不能用过去的频率代表现在。",
                "检查手柄是否仍开机、距离和干扰；若持续停顿，保存诊断，不要反复修改连接参数。");
        if (double.IsFinite(rate.Fd2Hz) && double.IsFinite(rate.ParsedHz) && rate.Fd2Hz > 0 &&
            rate.ParsedHz < rate.Fd2Hz * .9)
            return Result("parsed_below_notify", "已确认：收到的 FD2 多于成功解析的输入，优先排查报告格式或解析失败。",
                "复制诊断并导出日志，核对固件、原始报告与 parse_fail；不能把它简单归因于蓝牙连接间隔。");
        if (rate.DescriptorStatus != "write_success")
            return Result("fd2_request_unconfirmed", "线索：本次 FD2 133Hz descriptor 写入未确认成功。",
                "保留日志中的具体写入状态；可在退出其他占用程序后重新连接一次。descriptor 读回失败不等于写入失败。");
        if (facts.HealthObserved && !health!.IsRecent)
            return Result("measurement_stale", "已确认：没有新鲜的完整测量窗口，不能用历史平均代表当前输入。",
                "保持手柄开机并等待新窗口；若持续无更新，保存日志检查系统交付与连接状态，不自动重启蓝牙。");
        if (health is { State: BleRateStabilityState.Recovering })
            return Result("rate_recovering", "已观测：速率正在恢复，尚未连续通过三个高速窗口。",
                "保持当前连接，继续测量；不会为短时波动重建虚拟手柄或重复修改参数。");
        if (facts.Win10 && facts.Configuration is { Readable: false })
            return Result("win10_configuration_unreadable", "Win10 配置读取失败，尚不能确认是否漏掉管理员配置步骤。",
                "先在系统与蓝牙信息中重新读取并保存错误日志；不要因读取失败直接覆盖系统参数。");
        if (facts.Win10 && facts.Configuration is { RestartPending: true })
            return Result("win10_restart_pending", "线索：上次 Win10 配置记录提示热重启未成功，记录后尚未重启 Windows。",
                "保存工作后正常重启 Windows，再测真实 FD2；仅重复以管理员身份打开助手不能替代重启。");
        if (facts.Win10 && !facts.Configured)
        {
            if (facts.Configuration == null && !compatibility.ConfigurationProbeSucceeded)
                return Result("win10_configuration_unknown", "Win10 参数探测失败，不能判断配置缺失。",
                    "只读重新采集系统与蓝牙信息并保存错误，不根据未知状态覆盖参数。");
            string restartAdvice = environment?.Adapters.Count == 1
                ? "仅在唯一当前 USB 适配器可确认时尝试重启；无法确认时须正常重启 Windows。"
                : "当前无法确认唯一适配器，不猜测重启设备；授权保存参数后须正常重启 Windows。";
            return Result("win10_configuration_missing",
                "已确认：这台 Win10 尚未匹配固定 7.5ms 的四项 BLE 默认配置；管理员运行本身不会完成配置。",
                facts.CanOfferWin10Trial
                    ? (facts.ExperimentalWin10
                        ? "此系统栈的参数作用尚无实机验证，可执行下方备份参数试验：确认后提权，可选 7.5ms、7.5–15ms 或 15ms，按真实输入比较是否有效。"
                        : "可执行下方 Win10 BLE 参数设置：确认后提权并备份，配置后仍须按真实输入验证。") +
                        "这是影响其他 BLE 连接的全局设置。" + restartAdvice
                    : "当前系统状态未满足安全试验条件；先复制诊断核对系统栈。不是因蓝牙设备型号或新哈希不同而拒绝。",
                facts.CanOfferWin10Trial);
        }
        if (rate.IntervalUnits > 6 && rate.Fd2Hz > 0 &&
            Math.Abs(rate.Fd2Hz - 800.0 / rate.IntervalUnits) <= 800.0 / rate.IntervalUnits * .25)
            return Result("connection_interval_matches_rate", "已确认：系统报告 " +
                (rate.IntervalUnits * 1.25).ToString("F2") + "ms 连接间隔，和当前通知频率吻合。优先排查连接参数协商。",
                facts.Win10
                    ? "已配置的默认值未能保证这次连接更快；核对重启记录、驱动和实际 Notify，勿重复盲写。"
                    : rate.Win11Route == "AdaptiveBoundedPresets"
                        ? "程序已按真实输入尝试本次连接的有限预设；查看测试结果与驱动信息。当前速率可游玩时继续保留连接，不反复重启蓝牙或改写系统。"
                        : rate.Win11Route == "UnvalidatedEnvironment"
                            ? "跨适配器型号、驱动版本的有限预设已开放；可启用自动优化或手动尝试一次有限预设，优先请求 7.5ms 并比较真实输入。不要求旧版白名单，不修改驱动。"
                            : "查看参数请求结果与官方蓝牙驱动版本；API 返回成功不保证对端实际持续采用 7.5ms。");
        if (rate.HandlerSamples > 0 && rate.HandlerAverageUs >= 4000)
            return Result("notify_handler_slow", "线索：程序 Notify 回调平均耗时偏高，需先核对本地处理负载。",
                "比较回调耗时、解析失败和系统负载；回调统计不是无线延迟，不能仅凭它判定蓝牙硬件故障。");
        if (rate.Gaps.Samples >= 50 && rate.Gaps.Near7p5 >= rate.Gaps.Samples * .15 &&
            rate.Gaps.Over22 >= rate.Gaps.Samples * .15 && rate.Gaps.P95Ms >= 20)
            return Result("notify_mixed_short_and_skipped", "已确认：约 7.5ms 与较长间隙混合出现，不是单一 15ms 通知节奏。",
                "优先对照无线干扰、节电、系统负载和电量操作时间。混合间隙本身不能证明丢包，也不能确定是驱动还是手柄限制。");
        if (!facts.Win10 && rate.Win11Route == "UnvalidatedEnvironment")
            return Result("win11_profile_unvalidated", "线索：此连接尚未通过固定 7.5ms 实测；不同型号或驱动不再阻止有限预设测试。",
                "可启用自动优化或手动尝试有限预设，优先 7.5ms，比较前后真实 FD2。请求成功不代表系统持续采用 7.5ms，不自动重启蓝牙。");
        if (!facts.Win10 && !compatibility.Plan.CanRequestPreferredParameters)
            return Result(compatibility.ApiProbeSucceeded ? "win11_parameter_api_unavailable" : "win11_parameter_api_unknown",
                compatibility.ApiProbeSucceeded ? "已确认：当前 Win11 环境未提供可用的公开连接参数请求 API。" :
                    "Win11 API 探测失败，不能断言系统不支持连接参数请求。",
                "保留真实输入和兼容游玩；先检查官方系统组件与蓝牙驱动，再导出诊断。不会套用 Win10 注册表配置。");
        if (rate.Gaps.Samples > 0 && Math.Abs(rate.Gaps.P50Ms - 15) <= 1.5 && rate.Fd2Hz >= 55 && rate.Fd2Hz <= 75)
            return Result("notify_15ms_inferred", "线索：真实 Notify 呈约 15ms 节奏，与约 66Hz 相符；这不是直接读取的连接间隔。",
                "重点检查连接参数协商和官方蓝牙驱动；只凭时间戳无法确定是哪一层限制了速率。");
        if (rate.Gaps.Samples > 0 &&
            (rate.Gaps.P95Ms > Math.Max(15, rate.Gaps.P50Ms * 2) || rate.Gaps.MaxMs >= 60))
            return Result("notify_irregular", "线索：通知存在明显跳间隔或长停顿，单纯固定间隔不能解释全部低速。",
                "检查距离、无线干扰、节电和系统负载，并比较 P50/P95 与 parse_fail；现有信息不足以指定唯一根因。");
        if (facts.Win10 && facts.Configured)
            return Result("win10_configured_but_slow", "已确认：Win10 四项默认值已配置，但实际 FD2 仍未达到 125Hz。",
                "不能断言驱动一定读取或采用了这些值。先核对适配器重启、驱动版本、Notify 节奏和系统更新，再决定实验。");
        return Result("cause_not_determined", "已确认：真实输入偏低；现有证据不足以确定唯一原因。",
            "复制下方诊断并导出完整日志，比较系统栈、驱动、descriptor、连接参数与 Notify 间隔，不猜测硬件故障。");
    }
}
