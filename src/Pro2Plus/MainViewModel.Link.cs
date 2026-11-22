using System;
using System.IO;
using System.Windows.Input;
using Pro2Plus.Connection;

namespace Pro2Plus;

/// <summary>
/// 第一页「连接」的后端接线：把新的 <see cref="Pro2ConnectionService"/> 暴露成
/// 界面可直接绑定的属性与命令。刻意不复用旧的 Pro2BleInputSource，
/// 旧实现只作为协议参考保留。
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Pro2ConnectionService linkService = new();
    private ICommand? connectLinkCommand;
    private ICommand? rescanLinkCommand;
    private bool linkHooked;

    private static readonly System.Windows.Media.Brush LinkDotIdle =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5A, 0x63, 0x79));
    private static readonly System.Windows.Media.Brush LinkDotActive =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5C, 0x9C, 0xFF));

    public Pro2ConnectionService LinkService => linkService;

    public ICommand ConnectLinkCommand => connectLinkCommand ??=
        new ActionCommand(_ => _ = ToggleLinkAsync());

    public ICommand RescanLinkCommand => rescanLinkCommand ??=
        new ActionCommand(_ =>
        {
            linkService.Disconnect();
            _ = linkService.ConnectAsync();
        });

    private async System.Threading.Tasks.Task ToggleLinkAsync()
    {
        if (linkService.IsConnected) linkService.Disconnect();
        else await linkService.ConnectAsync();
    }

    // ── 连接 → 输出的衔接 ───────────────────────────────────────────
    // 连接就绪才创建虚拟手柄，断开就收掉；中间的插拔抖动用 2.5 秒去抖，
    // 避免虚拟设备在系统里反复插拔。

    private bool linkOutputStarting;
    private System.Threading.CancellationTokenSource? linkOutputStopCts;

    private async System.Threading.Tasks.Task EnsureLinkOutputAsync()
    {
        if (shuttingDown || Running || linkOutputStarting) return;
        // 没有真实输入帧就不要建虚拟手柄，否则又是一个空设备。
        if (!linkService.HasLiveInput) return;

        linkOutputStarting = true;
        try
        {
            await RunExclusiveAsync("启动虚拟手柄", async token =>
            {
                if (Running || shuttingDown) return;
                if (!linkService.HasLiveInput) return;

                // 不在这里清残留：那会重新枚举 USB，蓝牙适配器跟着抖一下就断链。
                await EnsureViiperReadyAsync(token);
                await StartAsync(SelectedProfile, token);

                Status = Running
                    ? "手柄已连接，虚拟手柄已就绪。"
                    : "手柄已连接，但虚拟手柄没有启动成功，请查看日志。";
            });
        }
        catch (Exception ex)
        {
            AppendLog("[LINK_OUTPUT] start failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            linkOutputStarting = false;
        }
    }

    private async System.Threading.Tasks.Task ScheduleStopLinkOutputAsync()
    {
        linkOutputStopCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        linkOutputStopCts = cts;

        try
        {
            await System.Threading.Tasks.Task.Delay(2500, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (linkService.IsConnected || shuttingDown) return;

        try
        {
            if (Running) await StopSessionAsync(updateStatus: false);
        }
        catch (Exception ex)
        {
            AppendLog("[LINK_OUTPUT] stop failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>在第一页切换输出模式：直接重建虚拟手柄，不走旧连接门槛。</summary>
    private async System.Threading.Tasks.Task RestartLinkOutputWithModeAsync(
        ViiperDeviceProfile profile,
        System.Threading.CancellationToken token)
    {
        SelectMode(profile.Mode);

        if (Running)
        {
            await StopSessionAsync(updateStatus: false);
            await System.Threading.Tasks.Task.Delay(650, token);
        }

        // 切换模式只重建 VIIPER 会话，不做设备残留清理：
        // 那一步会重新枚举 USB 设备，蓝牙适配器跟着抖一下就会断链。
        await EnsureViiperReadyAsync(token);
        await StartAsync(profile, token);
        Status = Running ? profile.Label + " 已就绪。" : profile.Label + " 启动失败，请查看日志。";
    }

    /// <summary>幂等：第一次取值时把服务事件接到 UI 线程上，并开始安静地等插线。</summary>
    private void EnsureLinkHooked()
    {
        if (linkHooked) return;
        linkHooked = true;
        linkService.Changed += OnLinkChanged;
        linkService.StartWatching();
    }

    private void OnLinkChanged()
    {
        void Notify()
        {
            OnPropertyChanged(nameof(LinkTitle));
            OnPropertyChanged(nameof(LinkDetail));
            OnPropertyChanged(nameof(LinkDetailSecondary));
            OnPropertyChanged(nameof(LinkPhaseText));
            OnPropertyChanged(nameof(LinkRateText));
            OnPropertyChanged(nameof(LinkBatteryText));
            OnPropertyChanged(nameof(LinkDotBrush));
            OnPropertyChanged(nameof(LinkIsConnected));
            OnPropertyChanged(nameof(LinkIsBusy));
            OnPropertyChanged(nameof(LinkCanConnect));
            OnPropertyChanged(nameof(LinkConnectText));
            OnPropertyChanged(nameof(FooterLinkText));

            // 连接状态变化后同步虚拟手柄：有连接就建，没了就收。
            if (shuttingDown) return;
            if (linkService.IsConnected && linkService.HasLiveInput) _ = EnsureLinkOutputAsync();
            else if (Running) _ = ScheduleStopLinkOutputAsync();
        }

        if (uiContext != null) uiContext.Post(_ => Notify(), null);
        else Notify();
    }

    /// <summary>由主窗口的界面定时器调用：折算回报率并刷新派生文案。</summary>
    public void RefreshLinkTelemetry()
    {
        EnsureLinkHooked();
        linkService.RefreshRate();
        OnPropertyChanged(nameof(LinkRateText));
        OnPropertyChanged(nameof(LinkDetail));
        OnPropertyChanged(nameof(LinkDetailSecondary));

        // 蓝牙握手完成后帧是陆续来的：等到第一帧真到了再补建虚拟手柄。
        if (!shuttingDown && !Running && !linkOutputStarting &&
            linkService.IsConnected && linkService.HasLiveInput)
        {
            _ = EnsureLinkOutputAsync();
        }
    }

    private Pro2LinkSnapshot Link => linkService.Snapshot;

    public string LinkTitle
    {
        get { EnsureLinkHooked(); return Link.Title; }
    }

    /// <summary>状态卡第二行：链路说明或失败原因。</summary>
    public string LinkDetail
    {
        get
        {
            EnsureLinkHooked();
            Pro2LinkSnapshot snapshot = Link;
            return snapshot.Phase switch
            {
                Pro2LinkPhase.Idle => snapshot.Detail.Length > 0
                    ? snapshot.Detail
                    : "插上 USB 线，或让手柄进入蓝牙配对",
                Pro2LinkPhase.Detecting => "正在查找手柄…",
                Pro2LinkPhase.Opening => snapshot.Detail.Length > 0 ? snapshot.Detail : "正在打开设备…",
                Pro2LinkPhase.Initializing => "正在初始化输入通道…",
                // 连上但一帧没来，和真正在收输入必须区分开：
                // 前者要提示「等待手柄输入」，后者才说「正在接收输入」。
                Pro2LinkPhase.Ready => snapshot.RateHz >= 1
                    ? (snapshot.Kind == Pro2LinkKind.Wired ? "USB 有线 · 正在接收输入" : "蓝牙 · 正在接收输入")
                    : snapshot.Detail.Length > 0 ? snapshot.Detail : "已连接",
                _ => snapshot.Detail.Length > 0 ? snapshot.Detail : "连接失败"
            };
        }
    }

    /// <summary>状态卡第三行：上一次连上的时间。</summary>
    public string LinkDetailSecondary
    {
        get
        {
            EnsureLinkHooked();
            if (Link.IsReady)
            {
                return Running ? "虚拟手柄已就绪" : "正在准备虚拟手柄…";
            }

            DateTime? last = linkService.LastConnectedAt;
            if (last is null) return "本次启动后尚未连接";
            TimeSpan age = DateTime.Now - last.Value;
            string when = age.TotalMinutes < 1 ? "刚刚"
                : age.TotalHours < 1 ? (int)age.TotalMinutes + " 分钟前"
                : age.TotalDays < 1 ? (int)age.TotalHours + " 小时前"
                : (int)age.TotalDays + " 天前";
            return "上次连接 " + when;
        }
    }

    /// <summary>按钮旁的状态短语（搜索中/连接中/已连接）。</summary>
    public string LinkPhaseText => Link.Phase switch
    {
        Pro2LinkPhase.Detecting => "正在搜索…",
        Pro2LinkPhase.Opening => "正在连接…",
        Pro2LinkPhase.Initializing => "正在初始化…",
        Pro2LinkPhase.Ready => "已连接",
        _ => ""
    };

    public string LinkRateText => Link.IsReady ? linkService.RateHz.ToString("0") : "—";

    /// <summary>
    /// 手柄报的是 0–8 档电量，8 就是满电；卡片单位是 %，
    /// 所以换算成百分比显示，不再把 8 档直接当成 8%。
    /// </summary>
    public string LinkBatteryText
    {
        get
        {
            int steps = Link.BatterySteps;
            if (steps < 0) return "—";
            int percent = (int)Math.Round(Math.Clamp(steps, 0, 8) * 100.0 / 8.0);
            return percent.ToString();
        }
    }

    public System.Windows.Media.Brush LinkDotBrush => Link.IsReady || Link.IsBusy ? LinkDotActive : LinkDotIdle;

    public bool LinkIsConnected => Link.IsReady;

    public bool LinkIsBusy => Link.IsBusy;

    public bool LinkCanConnect => !Link.IsBusy;

    public string LinkConnectText => Link.IsReady ? "断开连接" : Link.IsBusy ? "连接中…" : "连接手柄";

    // ═══════ 页脚实时状态（替换原来写死的「就绪 / USB 优先」）═══════

    /// <summary>页脚左：当前实际生效的映射模式（运行中取 activeMode，否则取选中模式）。</summary>
    public string FooterModeText
    {
        get
        {
            ViiperVirtualMode mode = activeMode ?? selectedMode;
            return mode switch
            {
                ViiperVirtualMode.KeyMouse => "键鼠模式",
                ViiperVirtualMode.Xbox or ViiperVirtualMode.XboxProfessionalImuTest => "Xbox 模式",
                ViiperVirtualMode.DualSenseLike or ViiperVirtualMode.DualSenseProfessionalImuTest => "PS5 模式",
                ViiperVirtualMode.Pro1 => "Pro1 模式",
                ViiperVirtualMode.Pro2 => "Pro2 模式",
                _ => ""
            };
        }
    }

    /// <summary>冒烟截图用：非空时强制模型图高亮这些按键位（正常运行恒为 null）。</summary>
    public static ulong? SmokeHighlightButtons;

    /// <summary>冒烟截图用：强制摇杆轴值（lx,ly,rx,ry），用来核对摇杆位置点。</summary>
    public static (ushort Lx, ushort Ly, ushort Rx, ushort Ry)? SmokeAxes;

    /// <summary>冒烟截图用：强制扳机行程（lt,rt），用来核对「按下一个键」的扳机图标。</summary>
    public static (ushort Lt, ushort Rt)? SmokeTriggers;

    /// <summary>映射页手柄模型图用：最后一次推送给虚拟手柄的输出快照（按键 + 摇杆）与新鲜度。</summary>
    public OutputPadSnapshot VirtualOutput
    {
        get
        {
            if (SmokeHighlightButtons is { } smoke)
            {
                var ax = SmokeAxes;
                ushort lx = ax?.Lx ?? 2048, ly = ax?.Ly ?? 2048, rx = ax?.Rx ?? 2048, ry = ax?.Ry ?? 2048;
                ushort lt = SmokeTriggers?.Lt ?? 0, rt = SmokeTriggers?.Rt ?? 0;
                return new OutputPadSnapshot(smoke, lx, ly, rx, ry, lt, rt);
            }
            return session is { } s && s.OutputButtonsAgeMs <= 300
                ? s.ReadOutputSnapshot()
                : new OutputPadSnapshot(0, 2048, 2048, 2048, 2048);
        }
    }
    public bool VirtualOutputFresh => session is not null && session.OutputButtonsAgeMs <= 300;

    /// <summary>日志文件夹路径（与连接诊断日志一致），供连接页标出并打开。</summary>
    public static string LogFolderPath => AppPaths.LogsDirectory;

    /// <summary>页脚右：当前真实链路——USB 有线 / 蓝牙，未连接时给出对应提示。</summary>
    public string FooterLinkText
    {
        get
        {
            EnsureLinkHooked();
            Pro2LinkSnapshot snapshot = Link;
            if (snapshot.Phase == Pro2LinkPhase.Ready)
                return snapshot.Kind == Pro2LinkKind.Wired ? "USB 有线" : "蓝牙";
            if (snapshot.Phase == Pro2LinkPhase.Detecting ||
                snapshot.Phase == Pro2LinkPhase.Opening ||
                snapshot.Phase == Pro2LinkPhase.Initializing)
                return "连接中…";
            return "未连接";
        }
    }
}
