using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

public sealed partial class MainViewModel
{
    private int bleAutoOptimizationQueued;
    private bool win10BleHomePanelExpanded;
    public bool ShowWin10BleHomeAction => TurboConsentText.ShowHomeAction(
        OperatingSystem.IsWindows(), Environment.OSVersion.Version.Build);
    public bool Win10BleHomePanelExpanded
    {
        get => win10BleHomePanelExpanded;
        set
        {
            if (Busy || !ShowWin10BleHomeAction || value == win10BleHomePanelExpanded) return;
            win10BleHomePanelExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowWin10BleHomePanel));
        }
    }
    public bool ShowWin10BleHomePanel => ShowWin10BleHomeAction && Win10BleHomePanelExpanded;
    public void ToggleWin10BleHomePanel() => Win10BleHomePanelExpanded = !Win10BleHomePanelExpanded;
    public string Win10BleHomeHint => inputSource.IsHighRateReady && inputSource.IsTransportConnected
        ? "当前输入已达标，无需修改蓝牙参数。"
        : CanConfigureWin10Stack
            ? "优先尝试 7.5ms；需管理员授权，可能短暂断开其他蓝牙设备。"
            : "当前系统检查未通过，暂不能应用参数。请查看系统与蓝牙信息。";
    private BlePresetDefinition selectedWin10BlePreset = BlePresetCatalog.Default;
    public IReadOnlyList<BlePresetDefinition> Win10BlePresetOptions => BlePresetCatalog.Presets;
    public BlePresetDefinition SelectedWin10BlePreset
    {
        get => selectedWin10BlePreset;
        set
        {
            if (value == null || Busy || IsBleOptimizationRunning) return;
            selectedWin10BlePreset = BlePresetCatalog.Get(value.Preset);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Win10BleConfigurationConfirmation));
        }
    }

    public bool AutoOptimizeBle
    {
        get => userSettings.AutoOptimizeBle;
        set
        {
            if (value == userSettings.AutoOptimizeBle || !CanChangeBleOptimizationSettings) return;
            userSettings.AutoOptimizeBle = value;
            foreach (Pro2ControllerSlot slot in pro2Slots)
                slot.InputSource.AutoOptimizeBleEnabled = value;
            SaveUserSettings("[BLE_AUTO_OPTIMIZE]");
            OnPropertyChanged();
        }
    }

    public bool IsBleOptimizationRunning => inputSource.IsCompatibilityOptimizationRunning;
    public bool IsWin10BleExperimental => TurboConsentText.IsExperimental(EnvironmentRouteInspector.Current.Plan);
    public string Win10BlePresetSafetyNote => TurboConsentText.SafetyNote(EnvironmentRouteInspector.Current.Plan);
    public string Win10BleConfigurationConfirmation => TurboConsentText.Confirmation(
        EnvironmentRouteInspector.Current.Plan, SelectedWin10BlePreset);
    public bool CanChangeBleOptimizationSettings => !Busy && !IsBleOptimizationRunning;
    public string BleOptimizationSummary => inputSource.CompatibilityOptimizationSummary;
    public bool ShowBleOptimizationSummary => !string.IsNullOrWhiteSpace(BleOptimizationSummary);
    public string BleConnectionTitle => IsBleOptimizationRunning ? "正在优化蓝牙连接" : CompactConnectionTitle;
    public string BleConnectionHint => IsBleOptimizationRunning
        ? BleOptimizationSummary + " · 按真实输入保留较好方案。"
        : CompactConnectionHint;

    public bool ShowWin11CompatibilityAction => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
        (HasLowRateWarning || IsBleOptimizationRunning);
    public bool CanTryWin11Compatibility => !previewOnly && !Busy &&
        inputSource.CanOptimizeConnectionCompatibility;

    private void InitializeBleCompatibilitySettings()
    {
        foreach (Pro2ControllerSlot slot in pro2Slots)
            slot.InputSource.AutoOptimizeBleEnabled = userSettings.AutoOptimizeBle;
    }

    // 优化进行中每来一条进度就刷新一次，这条数组必须复用；写 new[] 等于每次刷新都重新分配一遍。
    private static readonly string[] BleCompatibilityProperties =
    [
        nameof(AutoOptimizeBle), nameof(IsBleOptimizationRunning), nameof(CanChangeBleOptimizationSettings),
        nameof(BleOptimizationSummary), nameof(ShowBleOptimizationSummary), nameof(BleConnectionTitle),
        nameof(BleConnectionHint), nameof(ShowWin11CompatibilityAction), nameof(CanTryWin11Compatibility),
        nameof(IsWin10BleExperimental), nameof(Win10BlePresetSafetyNote), nameof(Win10BleConfigurationConfirmation),
        nameof(CanConfigureWin10Stack), nameof(ShowWin10BleHomeAction), nameof(ShowWin10BleHomePanel),
        nameof(Win10BleHomeHint), nameof(Win10StackConfigured), nameof(ShowWin10OneClick), nameof(Win10OneClickHint)
    ];

    private void RefreshBleCompatibilityPresentation()
    {
        for (int i = 0; i < BleCompatibilityProperties.Length; i++)
            OnPropertyChanged(BleCompatibilityProperties[i]);
    }

    private void QueueLowRateAutomaticOptimization()
    {
        if (pro2Slots[0].AutoReconnectEnabled) return; // Per-slot loop owns serialized optimization.
        if (previewOnly || shuttingDown || !AutoOptimizeBle || Busy || !HasLowRateWarning ||
            !inputSource.CanOptimizeConnectionCompatibility ||
            Interlocked.Exchange(ref bleAutoOptimizationQueued, 1) != 0) return;
        if (uiContext != null) uiContext.Post(async _ => await RunQueuedLowRateOptimizationAsync(), null);
        else _ = Task.Run(RunQueuedLowRateOptimizationAsync);
    }

    // 排队期间状态可能已经变了（用户关了自动优化、连接已经正常），执行前再确认一次。
    private async Task RunQueuedLowRateOptimizationAsync()
    {
        try
        {
            if (shuttingDown || !AutoOptimizeBle || !inputSource.CanOptimizeConnectionCompatibility) return;
            AppendLog("[BLE_AUTO_PRESET] trigger=confirmed_live_low_rate once_per_connection=true");
            await TryWin11CompatibilityAsync();
        }
        finally
        {
            Interlocked.Exchange(ref bleAutoOptimizationQueued, 0);
        }
    }

    public Task TryWin11CompatibilityAsync() => RunExclusiveAsync("自动优化蓝牙", async token =>
    {
        if (!inputSource.CanOptimizeConnectionCompatibility)
        {
            Status = "当前连接无需优化、正在测试或本轮方案已测完；正常高速连接不会被修改。";
            return;
        }
        Status = "正在自动对比蓝牙方案；请保持手柄开机，实测速率持续显示。";
        Pro2CompatibilityTrialOutcome outcome;
        await bleOperationGate.WaitAsync(token);
        try
        {
            outcome = await inputSource.OptimizeConnectionCompatibilityAsync(new Progress<string>(message =>
            {
                AppendLog(message);
                RefreshBleCompatibilityPresentation();
            }), token);
        }
        finally { bleOperationGate.Release(); }
        Status = outcome.Summary;
        await RefreshEnvironmentInfoAsync();
        RefreshCompactPresentation();
    });
}
