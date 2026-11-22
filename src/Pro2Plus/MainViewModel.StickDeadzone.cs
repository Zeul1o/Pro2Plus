using System.Windows.Threading;

namespace Pro2Plus;

/// <summary>
/// 摇杆死区设置，按链路分开：有线默认 2%，蓝牙默认 0。
/// 两条链路的解析器各读自己那份，改完立刻生效，不用重连。
///
/// 死区越小越跟手，但过小的死区挡不住摇杆本身的回中残差，
/// 会产生不可避免的漂移；调到能压住漂移的最小值最合适。
/// </summary>
public partial class MainViewModel
{
    private DispatcherTimer? stickDeadzoneSaveTimer;

    /// <summary>有线死区原始值（0–204，约 0–10%）。默认 41 ≈ 2%。</summary>
    public int WiredStickDeadzoneRaw
    {
        get => userSettings.EffectiveWiredStickDeadzone;
        set => SetStickDeadzone(value, wired: true, nameof(WiredStickDeadzoneRaw), nameof(WiredStickDeadzoneText));
    }

    /// <summary>蓝牙死区原始值（0–204，约 0–10%）。默认 0。</summary>
    public int BleStickDeadzoneRaw
    {
        get => userSettings.EffectiveBleStickDeadzone;
        set => SetStickDeadzone(value, wired: false, nameof(BleStickDeadzoneRaw), nameof(BleStickDeadzoneText));
    }

    private void SetStickDeadzone(int value, bool wired, string rawProperty, string textProperty)
    {
        int next = AppSettings.NormalizeStickDeadzone(value);
        int current = wired
            ? userSettings.EffectiveWiredStickDeadzone
            : userSettings.EffectiveBleStickDeadzone;
        if (next == current) return;

        if (wired) userSettings.WiredStickDeadzone = next;
        else userSettings.BleStickDeadzone = next;
        Pro2GyroBiasStore.SetStickDeadzone(next, wired);
        OnPropertyChanged(rawProperty);
        OnPropertyChanged(textProperty);
        ScheduleStickDeadzoneSave();
    }

    public string WiredStickDeadzoneText => ViewModelFormatting.PercentText(userSettings.EffectiveWiredStickDeadzone);
    public string BleStickDeadzoneText => ViewModelFormatting.PercentText(userSettings.EffectiveBleStickDeadzone);

    /// <summary>
    /// 拖滑块会连续触发写入，这里按 500ms 合并一次落盘，
    /// 免得一秒钟把配置写几十遍；内存里的值始终是实时的。
    /// </summary>
    private void ScheduleStickDeadzoneSave()
    {
        try
        {
            if (stickDeadzoneSaveTimer is null)
            {
                stickDeadzoneSaveTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                stickDeadzoneSaveTimer.Tick += (_, _) =>
                {
                    stickDeadzoneSaveTimer?.Stop();
                    SaveUserSettings("[STICK_DZ]");
                };
            }

            stickDeadzoneSaveTimer.Stop();
            stickDeadzoneSaveTimer.Start();
        }
        catch
        {
            SaveUserSettings("[STICK_DZ]");
        }
    }
}
