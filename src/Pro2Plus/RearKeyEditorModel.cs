using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Pro2Plus;

public abstract class ObservableEditorBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>按键候选。标签按输出模式预建，选中状态仍由每个实例独占。</summary>
public sealed class RearKeyChoice : ObservableEditorBase
{
    private readonly record struct Spec(GamepadButtons Key, string Label);

    private static readonly Spec[] XboxSpecs =
    [
        new(GamepadButtons.None, "无"),
        new(GamepadButtons.South, "A"), new(GamepadButtons.East, "B"),
        new(GamepadButtons.West, "X"), new(GamepadButtons.North, "Y"),
        new(GamepadButtons.L1, "LB"), new(GamepadButtons.R1, "RB"),
        new(GamepadButtons.L2, "LT"), new(GamepadButtons.R2, "RT"),
        new(GamepadButtons.LeftStick, "L3"), new(GamepadButtons.RightStick, "R3"),
        new(GamepadButtons.Back, "View"), new(GamepadButtons.Start, "Menu"),
        new(GamepadButtons.Home, "Xbox"),
        new(GamepadButtons.DPadUp, "↑"), new(GamepadButtons.DPadDown, "↓"),
        new(GamepadButtons.DPadLeft, "←"), new(GamepadButtons.DPadRight, "→")
    ];

    private static readonly Spec[] Ps5Specs =
    [
        new(GamepadButtons.None, "无"),
        new(GamepadButtons.South, "×"), new(GamepadButtons.East, "○"),
        new(GamepadButtons.West, "□"), new(GamepadButtons.North, "△"),
        new(GamepadButtons.L1, "L1"), new(GamepadButtons.R1, "R1"),
        new(GamepadButtons.L2, "L2"), new(GamepadButtons.R2, "R2"),
        new(GamepadButtons.LeftStick, "L3"), new(GamepadButtons.RightStick, "R3"),
        new(GamepadButtons.Back, "Create"), new(GamepadButtons.Start, "Options"),
        new(GamepadButtons.Home, "PS"),
        new(GamepadButtons.DPadUp, "↑"), new(GamepadButtons.DPadDown, "↓"),
        new(GamepadButtons.DPadLeft, "←"), new(GamepadButtons.DPadRight, "→")
    ];

    private static readonly Spec[] NintendoSpecs =
    [
        new(GamepadButtons.None, "无"),
        new(GamepadButtons.South, "B"), new(GamepadButtons.East, "A"),
        new(GamepadButtons.West, "Y"), new(GamepadButtons.North, "X"),
        new(GamepadButtons.L1, "L"), new(GamepadButtons.R1, "R"),
        new(GamepadButtons.L2, "ZL"), new(GamepadButtons.R2, "ZR"),
        new(GamepadButtons.LeftStick, "L3"), new(GamepadButtons.RightStick, "R3"),
        new(GamepadButtons.Back, "−"), new(GamepadButtons.Start, "+"),
        new(GamepadButtons.Home, "Home"),
        new(GamepadButtons.DPadUp, "↑"), new(GamepadButtons.DPadDown, "↓"),
        new(GamepadButtons.DPadLeft, "←"), new(GamepadButtons.DPadRight, "→")
    ];

    public GamepadButtons Key { get; }
    public string Label { get; }
    private bool selected;
    public bool Selected
    {
        get => selected;
        set { if (selected == value) return; selected = value; Changed(); }
    }

    public RearKeyChoice(GamepadButtons key, string label, bool selected = false)
    {
        Key = key;
        Label = label;
        this.selected = selected;
    }

    public static IReadOnlyList<RearKeyChoice> Choices(bool ps5, bool nintendo = false)
    {
        Spec[] table = ps5 ? Ps5Specs : nintendo ? NintendoSpecs : XboxSpecs;
        bool withCapture = ps5 || nintendo;
        var result = new List<RearKeyChoice>(table.Length + (withCapture ? 1 : 0));
        foreach (Spec spec in table)
        {
            result.Add(new RearKeyChoice(spec.Key, spec.Label));
        }

        if (withCapture)
        {
            result.Add(new RearKeyChoice(GamepadButtons.Capture, ps5 ? "触摸板点击" : "截图"));
        }

        return result;
    }
}

public sealed class RearKeyStepEditor : ObservableEditorBase
{
    public IReadOnlyList<RearKeyChoice> Choices { get; }
    public bool Ps5 { get; }

    private Ps5TouchGesture touchAction;
    public Ps5TouchGesture TouchAction
    {
        get => touchAction;
        set
        {
            if (touchAction == value) return;
            touchAction = Enum.IsDefined(value) ? value : Ps5TouchGesture.None;
            if (touchAction != Ps5TouchGesture.None &&
                (!int.TryParse(Duration, out int ms) || ms < Ps5TouchGestures.MinDurationMs))
            {
                Duration = Ps5TouchGestures.DefaultDurationMs.ToString(CultureInfo.InvariantCulture);
            }
            Changed();
        }
    }

    private GamepadButtons first, second;
    public GamepadButtons First { get => first; set { if (first == value) return; first = value; Changed(); } }
    public GamepadButtons Second { get => second; set { if (second == value) return; second = value; Changed(); } }

    private string delay = "0", duration = "80";
    public string Delay { get => delay; set { if (delay == value) return; delay = value; Changed(); } }
    public string Duration { get => duration; set { if (duration == value) return; duration = value; Changed(); } }

    public ICommand RemoveCommand { get; }

    public RearKeyStepEditor(bool ps5, RearKeyStep step, Action<RearKeyStepEditor> remove, bool nintendo = false)
    {
        Choices = RearKeyChoice.Choices(ps5, nintendo);
        Ps5 = ps5;
        touchAction = ps5 ? step.TouchGesture : Ps5TouchGesture.None;
        // 只取前两个被勾选的位，避免 LINQ 物化整份候选表。
        GamepadButtons[] picked = new GamepadButtons[2];
        int found = 0;
        foreach (RearKeyChoice choice in Choices)
        {
            if (found == 2) break;
            if (choice.Key != GamepadButtons.None && (step.Buttons & choice.Key) != 0)
            {
                picked[found++] = choice.Key;
            }
        }
        first = picked[0];
        second = picked[1];
        Delay = step.DelayMs.ToString(CultureInfo.InvariantCulture);
        Duration = step.PressMs.ToString(CultureInfo.InvariantCulture);
        RemoveCommand = new ActionCommand(_ => remove(this));
    }
}

/// <summary>一条背键映射的编辑模型。校验与文案在 RearKeyFormValidation。</summary>
public sealed class RearKeyEditor : ObservableEditorBase
{
    public static IReadOnlyList<string> Actions => RearKeyFormValidation.Actions;
    public string Title { get; }
    public bool Ps5 { get; }
    public bool Nintendo { get; }

    private bool isSelected;
    public bool IsSelected { get => isSelected; set { isSelected = value; Changed(); } }

    private bool enabled;
    public bool Enabled
    {
        get => enabled;
        set { if (enabled == value) return; enabled = value; Changed(); Changed(nameof(EnabledCaption)); }
    }

    public string EnabledCaption => RearKeyFormValidation.EnabledCaption(Enabled);

    private Ps5TouchGesture touchAction;
    public Ps5TouchGesture TouchAction
    {
        get => touchAction;
        set
        {
            Ps5TouchGesture next = Ps5 && Enum.IsDefined(value) ? value : Ps5TouchGesture.None;
            if (touchAction == next) return;
            touchAction = next;
            if (next != Ps5TouchGesture.None &&
                (!int.TryParse(Duration, out int ms) || ms < Ps5TouchGestures.MinDurationMs))
            {
                Duration = Ps5TouchGestures.DefaultDurationMs.ToString(CultureInfo.InvariantCulture);
            }
            Changed();
            Changed(nameof(MappingSummary));
            Changed(nameof(IsDurationVisible));
            Changed(nameof(DurationLabel));
            Changed(nameof(BehaviorHint));
        }
    }

    public bool IsDurationVisible =>
        RearKeyFormValidation.IsDurationVisible(action, TouchAction);
    public string DurationLabel => RearKeyFormValidation.DurationLabel(TouchAction);

    public string MappingSummary
    {
        get
        {
            var labels = new List<string>();
            foreach (RearKeyChoice choice in Buttons)
            {
                if (choice.Selected) labels.Add(choice.Label);
            }
            return RearKeyFormValidation.Summary(labels, TouchAction);
        }
    }

    private string action = "";
    public string Action
    {
        get => action;
        set
        {
            string next = RearKeyFormValidation.NormalizeAction(value);
            if (action == next) return;
            action = next;
            Changed();
            Changed(nameof(IsTimed));
            Changed(nameof(IsDurationVisible));
            Changed(nameof(IsRepeat));
            Changed(nameof(IsSequence));
            Changed(nameof(BehaviorHint));
        }
    }

    public bool IsTimed => RearKeyFormValidation.IsTimed(action);
    public bool IsRepeat => RearKeyFormValidation.IsRepeat(action);
    public bool IsSequence => RearKeyFormValidation.IsSequence(action);
    public string BehaviorHint => RearKeyFormValidation.BehaviorHint(action, TouchAction);

    private string delay = "0", duration = "80", gap = "80", count = "0";
    public string Delay { get => delay; set { if (delay == value) return; delay = value; Changed(); } }
    public string Duration { get => duration; set { if (duration == value) return; duration = value; Changed(); } }
    public string Gap { get => gap; set { if (gap == value) return; gap = value; Changed(); } }
    public string Count { get => count; set { if (count == value) return; count = value; Changed(); } }

    public ObservableCollection<RearKeyChoice> Buttons { get; }
    public ObservableCollection<RearKeyStepEditor> Steps { get; } = [];
    public ICommand AddStepCommand { get; }

    public RearKeyEditor(string title, bool ps5, RearKeyBinding binding, bool nintendo = false)
    {
        Title = title;
        Ps5 = ps5;
        Nintendo = nintendo;
        binding = binding.Normalize();
        touchAction = ps5 ? binding.TouchGesture : Ps5TouchGesture.None;
        enabled = binding.Enabled;
        action = Actions[(int)binding.Action];
        Delay = binding.DelayMs.ToString(CultureInfo.InvariantCulture);
        Duration = binding.PressMs.ToString(CultureInfo.InvariantCulture);
        Gap = binding.GapMs.ToString(CultureInfo.InvariantCulture);
        Count = binding.RepeatCount.ToString(CultureInfo.InvariantCulture);

        IReadOnlyList<RearKeyChoice> candidates = RearKeyChoice.Choices(ps5, nintendo);
        Buttons = new ObservableCollection<RearKeyChoice>();
        foreach (RearKeyChoice choice in candidates)
        {
            if (choice.Key == GamepadButtons.None) continue;
            choice.Selected = (binding.Buttons & choice.Key) != 0;
            choice.PropertyChanged += (_, _) => Changed(nameof(MappingSummary));
            Buttons.Add(choice);
        }

        Steps.CollectionChanged += (_, _) => Changed(nameof(Steps));
        foreach (RearKeyStep step in binding.Steps) AddStep(step);
        AddStepCommand = new ActionCommand(_ =>
        {
            if (Steps.Count < 16) AddStep(new() { Buttons = GamepadButtons.South, DelayMs = 100 });
        });
    }

    private void AddStep(RearKeyStep step)
    {
        var editor = new RearKeyStepEditor(Ps5, step, removed => Steps.Remove(removed), Nintendo);
        editor.PropertyChanged += (_, _) => Changed(nameof(Steps));
        Steps.Add(editor);
    }

    public bool TryBuild(out RearKeyBinding binding, out string error)
    {
        RearKeyBuildResult result = RearKeyFormValidation.Build(Snapshot());
        binding = result.Binding;
        error = result.Error;
        return result.Ok;
    }

    private RearKeyForm Snapshot()
    {
        GamepadButtons keys = GamepadButtons.None;
        foreach (RearKeyChoice choice in Buttons)
        {
            if (choice.Selected) keys |= choice.Key;
        }

        var steps = new List<RearKeyStepForm>(Steps.Count);
        foreach (RearKeyStepEditor step in Steps)
        {
            steps.Add(new RearKeyStepForm(
                step.First | step.Second,
                Ps5 ? step.TouchAction : Ps5TouchGesture.None,
                step.Delay,
                step.Duration));
        }

        return new RearKeyForm(
            Title, Enabled, action, TouchAction,
            Delay, Duration, Gap, Count, keys, steps);
    }
}
