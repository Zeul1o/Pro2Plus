using System;

namespace Pro2Plus;

/// <summary>
/// 低速诊断展示给用户的证据段落。只陈述能读到的数值，并把「不是证据」的部分
/// 明确标出来（例如回调耗时不是无线延迟、时间戳不能证明丢包）。
/// </summary>
internal static class BleLowRateEvidence
{
    public static string Describe(Pro2RateEvidence rate, BleLowRateFacts facts, SystemBluetoothSnapshot? environment)
    {
        string configuredText = !facts.Win10 ? "Win11 使用连接参数 API，不套用 Win10 全局配置。" :
            facts.Configuration is { Readable: false } ? "Win10 参数未能读取，不能判断是否漏过配置。" :
            facts.Configured ? "Win10 固定 7.5ms 默认值已存在；有配置不等于此连接已生效。" :
            "Win10 固定 7.5ms 默认值尚未匹配；以管理员运行助手并不会自动写入这些值。";
        RadioDiagnosticInfo? defaultRadio = FirstDefault(environment);
        string device = defaultRadio != null
            ? "系统默认设备：" + SystemBluetoothDiagnostics.Value(defaultRadio.Driver.Name) +
              " / 驱动 " + SystemBluetoothDiagnostics.Value(defaultRadio.Driver.DriverVersion) + "（非每条链路归属证明）"
            : "系统默认蓝牙设备未识别；不能用枚举首项猜测连接归属。";
        string evidence = "FD2 / Parsed：" + Number(rate.Fd2Hz) + " / " + Number(rate.ParsedHz) +
            " Hz（" + (rate.RecentWindow ? "最近约 5 秒窗口" : "连接阶段累计测量") + "）；最新输入年龄：" +
            (double.IsFinite(rate.LastInputAgeMs) ? rate.LastInputAgeMs.ToString("F0") + "ms" : "未获取") +
            "\n系统报告的连接间隔：" + (rate.IntervalUnits > 0 ? (rate.IntervalUnits * 1.25).ToString("F2") + "ms" : "未读取") +
            (rate.Gaps.Samples > 0 ? "；Notify P50 / P95：" + rate.Gaps.P50Ms.ToString("F2") + " / " +
                rate.Gaps.P95Ms.ToString("F2") + "ms；最大间隙：" + rate.Gaps.MaxMs.ToString("F2") +
                "ms；7.5 / 15 / ≥22ms 个数：" + rate.Gaps.Near7p5 + " / " + rate.Gaps.Near15 + " / " + rate.Gaps.Over22
                : "；尚无完整间隔窗口") +
            "\nFD2 133Hz 请求：" + rate.DescriptorStatus + "；参数请求：" + rate.ParameterStatus +
            "；Win11 路线：" + rate.Win11Route + "\n" + configuredText + "\n" + device +
            (facts.Configuration != null
                ? "\nWin10 配置值：" + facts.Configuration.Values + "；主程序提权：" + facts.Configuration.ProcessElevated
                : "") +
            "\n解析失败：" + rate.ParseFailures + (rate.HandlerSamples > 0
                ? "；Notify 回调平均 / 最大耗时：" + Number(rate.HandlerAverageUs) + " / " + Number(rate.HandlerMaxUs) +
                  "µs（连接累计，非无线延迟）"
                : "；尚无回调耗时样本") +
            "\n电量更新状态：" + rate.BatteryRefreshStatus +
            (rate.BatterySuspensionReason.Length > 0 ? "；暂停原因：" + rate.BatterySuspensionReason : "") +
            "；此处只读缓存，不会触发电量查询。";

        if (!facts.HealthObserved) return evidence;
        BleRateStabilitySnapshot health = facts.Health!;
        return evidence + "\n持续监测：" + health.State + "；有效窗口：" + health.ValidWindowCount +
            "；测量时长：" + health.ValidMeasuredSeconds.ToString("F1") + "s；高速健康门槛：" + health.HighRateThresholdHz +
            "Hz（与 60Hz 游玩门槛、125Hz 初始化验收不同）" +
            "\n本次连接有效窗口最低 / 最高：" + Number(health.SessionMinimumHz ?? double.NaN) + " / " +
            Number(health.SessionMaximumHz ?? double.NaN) + "Hz；低速窗口：" + health.LowWindowCount +
            "；时间戳不能独立证明空口丢包或电源故障。";
    }

    private static RadioDiagnosticInfo? FirstDefault(SystemBluetoothSnapshot? environment)
    {
        if (environment == null) return null;
        foreach (RadioDiagnosticInfo radio in environment.Adapters)
            if (radio.IsDefault) return radio;
        return null;
    }

    private static string Number(double value) => double.IsFinite(value) && value >= 0 ? value.ToString("F1") : "未获取";
}
