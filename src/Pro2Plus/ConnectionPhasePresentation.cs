namespace Pro2Plus;

public enum ConnectionPhase
{
    Offline, Scanning, Connecting, Initializing, Measuring, Ready, RateLimited, Disconnected
}

/// <summary>
/// 连接页的状态文案。物理传输断开时优先于一切逻辑阶段，避免把上一帧的旧状态
/// 继续显示成「已连接」；其余分支按阶段优先级依次给出徽标、标题与说明。
/// </summary>
public sealed record ConnectionPhasePresentation(string Badge, string Title, string Hint)
{
    public static bool CanStartVirtual(bool strict, bool live, bool rateAccepted, bool gameplayReady = false) =>
        !strict || live && (rateAccepted || gameplayReady);

    public static ConnectionPhasePresentation Create(ConnectionPhase phase,
        bool transportConnected, bool virtualRunning, bool busy, bool reconnecting, bool gameplayReady = false)
    {
        if (!transportConnected &&
            phase is ConnectionPhase.Ready or ConnectionPhase.RateLimited or
                ConnectionPhase.Measuring or ConnectionPhase.Initializing)
            return new("连接已断开", "手柄连接已断开", "物理连接已中断，不能将旧状态显示为已连接。");
        if (phase == ConnectionPhase.RateLimited)
            return new(gameplayReady ? "可用 · 建议优化" : "已连接 · 速率偏低",
                virtualRunning ? "可以游玩，建议优化蓝牙" : gameplayReady ? "手柄可用，正在准备游戏" : "输入速率偏低，等待恢复",
                virtualRunning
                    ? "按真实输入速率转发，不补帧。下方可查看低速原因线索与优化建议。"
                    : gameplayReady ? "已达到兼容游玩速率，无需等待 125Hz；仍建议优化蓝牙，降低输入间隔。"
                    : "BLE 已连接，但输入尚未达到 60Hz 兼容门槛，游戏输出暂不启动；恢复后自动继续。");
        if (phase == ConnectionPhase.Ready)
            return virtualRunning
                ? new("游戏就绪", "已连接，开始游戏吧", "切换兼容模式无需重新配对手柄。")
                : new("手柄已连接", "手柄已连接", "输入验收已通过，正在准备所选游戏模式。");
        if (phase == ConnectionPhase.Measuring)
            return new("已连接 · 正在测速", "蓝牙已连接，正在验证输入", "初始低速可能是蓝牙参数协商过程，并非最终结果。测速与优化通常需要数十秒，验收通过后自动进入游戏。");
        if (transportConnected || phase == ConnectionPhase.Initializing)
            return new("蓝牙已连接 · 初始化", "蓝牙已连接", "正在初始化手柄和订阅 FD2 输入，请稍候。");
        if (phase == ConnectionPhase.Connecting)
            return new("正在建立连接", "已找到 Pro2，正在连接", "发现广播不等于已建链，正在等待 GATT 服务。");
        if (phase == ConnectionPhase.Scanning || reconnecting)
            return new("正在搜索", "正在寻找你的 Pro2", "请唤醒手柄并开启配对广播。");
        if (phase == ConnectionPhase.Disconnected)
            return new("连接已断开", "手柄连接已断开", "系统报告连接断开；开启自动重连后会继续寻找。");
        return busy ? new("处理中", "正在处理，请稍候", "正在处理当前操作。") :
            new("待连接", "连接 Pro2 手柄", "手柄切到 PRO2 模式并开启配对，然后点击下方连接。");
    }
}
