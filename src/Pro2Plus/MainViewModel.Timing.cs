using System;

namespace Pro2Plus;

/// <summary>
/// 按键时序：轻按 / 长按 / 双击的判定窗口。
/// 只影响「同一个键同时绑定了轻按与双击或长按」的情形：
/// 按下后要等长按判定结束才能确认是轻按，所以这两个值直接决定轻按的手感延迟。
/// 双击间隔同时还兼任 L1+R1 组合键的等待窗口（不另设参数）。
/// </summary>
public partial class MainViewModel
{
    private int doubleTapIntervalMs = 100;
    private int holdThresholdMs = 250;
    private int comboWindowMs = 150;

    public int DoubleTapIntervalMs
    {
        get => doubleTapIntervalMs;
        set
        {
            int next = Math.Clamp(value, 40, 400);
            if (next == doubleTapIntervalMs) return;
            doubleTapIntervalMs = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DoubleTapIntervalText));
            OnPropertyChanged(nameof(LightPressDelayText));
            PushTiming();
        }
    }

    public string DoubleTapIntervalText => doubleTapIntervalMs + " ms";

    public int HoldThresholdMs
    {
        get => holdThresholdMs;
        set
        {
            int next = Math.Clamp(value, 100, 800);
            if (next == holdThresholdMs) return;
            holdThresholdMs = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HoldThresholdText));
            OnPropertyChanged(nameof(LightPressDelayText));
            PushTiming();
        }
    }

    /// <summary>组合键的等待窗口：两颗键先后按下的时间差在这个范围内才算组合。</summary>
    public int ComboWindowMs
    {
        get => comboWindowMs;
        set
        {
            int next = Math.Clamp(value, 40, 600);
            if (next == comboWindowMs) return;
            comboWindowMs = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ComboWindowText));
            PushTiming();
        }
    }

    public string ComboWindowText => comboWindowMs + " ms";

    /// <summary>两个判定窗口直接决定键鼠模式的轻按手感，改完立刻生效，不用重连。</summary>
    private void PushTiming() =>
        Mapping.KbmMappingEngine.Shared.SetTiming(holdThresholdMs, doubleTapIntervalMs, comboWindowMs, 28);

    public string HoldThresholdText => holdThresholdMs + " ms";

    /// <summary>轻按最长要等多久才确认：取两项里更长的那个。</summary>
    public string LightPressDelayText =>
        "轻按确认约 " + Math.Max(doubleTapIntervalMs, holdThresholdMs) + " ms";
}
