using System;
using System.Collections.Generic;
using System.Text;

namespace Pro2Plus.Mapping;

/// <summary>
/// 键鼠仲裁器的离线自检：不接手柄，直接喂合成帧，核对
/// 「只轻按 / 轻按+双击 / 轻按+长按 / 未绑轻按 / 手柄键目标」几种情形。
/// 用 PRO2_ENGINE_TEST=1 启动，结果写到 PRO2_ENGINE_TEST_OUT。
/// </summary>
public static class KbmMappingEngineSmoke
{
    private static readonly StringBuilder Log = new();

    public static string Run()
    {
        Log.Clear();
        TapOnly();
        TapAndDouble();
        TapAndLong();
        NoTap();
        Combo();
        ComboAnyPair();
        return Log.ToString();
    }

    private static GamepadState Frame(GamepadButtons buttons)
    {
        GamepadState state = GamepadState.Neutral();
        state.Buttons = buttons;
        return state;
    }

    private static string Describe(KbmMappingEngine engine)
    {
        var keys = new List<string>();
        foreach (byte key in engine.Keys) keys.Add("0x" + key.ToString("X2"));
        keys.Sort(StringComparer.Ordinal);
        return "keys=[" + string.Join(",", keys) + "] mod=" + engine.Modifiers
               + " mouse=" + engine.MouseButtons;
    }

    private static void Check(string name, string expected, string actual)
    {
        bool ok = string.Equals(expected, actual, StringComparison.Ordinal);
        Log.AppendLine((ok ? "PASS " : "FAIL ") + name);
        Log.AppendLine("  expect " + expected);
        Log.AppendLine("  actual " + actual);
    }

    private static MappingProfile Profile(Action<MappingProfile> build)
    {
        var profile = new MappingProfile();
        build(profile);
        return profile;
    }

    private static void TapOnly()
    {
        var engine = new KbmMappingEngine();
        engine.SetTiming(250, 100, 100, 28);
        engine.SetProfile(Profile(p =>
        {
            p.Set("GL", BindingTrigger.Tap, BindingTarget.Key("LeftShift"));
        }));

        engine.Update(Frame(GamepadButtons.PaddleLeft), 0);
        Check("只绑轻按：按下立刻出", "keys=[] mod=2 mouse=0", Describe(engine));

        engine.Update(Frame(GamepadButtons.PaddleLeft), 120);
        Check("只绑轻按：按住持续", "keys=[] mod=2 mouse=0", Describe(engine));

        engine.Update(Frame(GamepadButtons.None), 140);
        Check("只绑轻按：松开即收", "keys=[] mod=0 mouse=0", Describe(engine));
    }

    private static void TapAndDouble()
    {
        var engine = new KbmMappingEngine();
        engine.SetTiming(250, 100, 100, 28);
        engine.SetProfile(Profile(p =>
        {
            p.Set("GL", BindingTrigger.Tap, BindingTarget.Key("Space"));
            p.Set("GL", BindingTrigger.Double, BindingTarget.Key("Escape"));
        }));

        engine.Update(Frame(GamepadButtons.PaddleLeft), 0);
        engine.Update(Frame(GamepadButtons.None), 60);
        engine.Update(Frame(GamepadButtons.None), 100);
        Check("轻按+双击：松开后先挂起，不立刻出", "keys=[] mod=0 mouse=0", Describe(engine));

        engine.Update(Frame(GamepadButtons.None), 170);
        string at170 = Describe(engine);
        engine.Update(Frame(GamepadButtons.None), 220);
        string at220 = Describe(engine);
        Check("轻按+双击：窗口过去才补轻按", "keys=[0x2C] mod=0 mouse=0", at170);
        Check("轻按+双击：脉冲到期松开", "keys=[] mod=0 mouse=0", at220);

        // 第二下落在窗口内 → 出双击，且不能再补轻按。换一份干净状态。
        engine = new KbmMappingEngine();
        engine.SetTiming(250, 100, 100, 28);
        engine.SetProfile(Profile(p =>
        {
            p.Set("GL", BindingTrigger.Tap, BindingTarget.Key("Space"));
            p.Set("GL", BindingTrigger.Double, BindingTarget.Key("Escape"));
        }));

        engine.Update(Frame(GamepadButtons.PaddleLeft), 0);
        engine.Update(Frame(GamepadButtons.None), 60);
        engine.Update(Frame(GamepadButtons.PaddleLeft), 100);
        engine.Update(Frame(GamepadButtons.None), 260);
        string at260 = Describe(engine);
        engine.Update(Frame(GamepadButtons.None), 320);
        string at320 = Describe(engine);
        Check("双击：第二下收双击", "keys=[0x29] mod=0 mouse=0", at260);
        Check("双击：双击脉冲到期松开", "keys=[] mod=0 mouse=0", at320);
    }

    private static void TapAndLong()
    {
        var engine = new KbmMappingEngine();
        engine.SetTiming(250, 100, 100, 28);
        engine.SetProfile(Profile(p =>
        {
            p.Set("GL", BindingTrigger.Tap, BindingTarget.Key("Space"));
            p.Set("GL", BindingTrigger.Long, BindingTarget.Key("Escape"));
        }));

        engine.Update(Frame(GamepadButtons.PaddleLeft), 0);
        engine.Update(Frame(GamepadButtons.PaddleLeft), 240);
        Check("轻按+长按：未到阈值不出", "keys=[] mod=0 mouse=0", Describe(engine));

        engine.Update(Frame(GamepadButtons.PaddleLeft), 260);
        Check("轻按+长按：到点立刻出长按", "keys=[0x29] mod=0 mouse=0", Describe(engine));

        engine.Update(Frame(GamepadButtons.PaddleLeft), 400);
        Check("轻按+长按：长按按住持续", "keys=[0x29] mod=0 mouse=0", Describe(engine));

        engine.Update(Frame(GamepadButtons.None), 420);
        Check("轻按+长按：松开不补轻按", "keys=[] mod=0 mouse=0", Describe(engine));

        // 短按：松开即可确认，不必等双击窗口。
        engine.Update(Frame(GamepadButtons.PaddleLeft), 500);
        engine.Update(Frame(GamepadButtons.None), 560);
        Check("轻按+长按：短按松开即出轻按", "keys=[0x2C] mod=0 mouse=0", Describe(engine));
    }

    private static void Combo()
    {
        var engine = new KbmMappingEngine();
        engine.SetTiming(250, 100, 100, 28);
        engine.SetProfile(Profile(p =>
        {
            p.Set("L", BindingTrigger.Tap, BindingTarget.Key("C"));
            p.Set("R", BindingTrigger.Tap, BindingTarget.Key("E"));
            p.SetCombo("L", BindingTarget.Key("X"), "R");
        }));

        engine.Update(Frame(GamepadButtons.L1), 0);
        Check("组合：按下第一颗先让位", "keys=[] mod=0 mouse=0", Describe(engine));

        engine.Update(Frame(GamepadButtons.L1 | GamepadButtons.R1), 50);
        Check("组合：窗口内第二颗 → 出组合键", "keys=[0x1B] mod=0 mouse=0", Describe(engine));

        engine.Update(Frame(GamepadButtons.L1), 200);
        Check("组合：松开一颗，单键仍让位", "keys=[] mod=0 mouse=0", Describe(engine));

        engine.Update(Frame(GamepadButtons.L1), 260);
        Check("组合：组合手势内不补单键", "keys=[] mod=0 mouse=0", Describe(engine));

        // 单独按搭子（R）：它没配组合，必须立刻走自己的映射，不能被 L+R 拖慢。
        engine.Update(Frame(GamepadButtons.None), 300);
        engine.Update(Frame(GamepadButtons.R1), 400);
        Check("组合：单独按搭子立即出单键", "keys=[0x08] mod=0 mouse=0", Describe(engine));

        engine.Update(Frame(GamepadButtons.None), 440);
        Check("组合：搭子松开即收", "keys=[] mod=0 mouse=0", Describe(engine));

        // 单独按主键：仍然要让位等窗口，确认不是组合才出。
        engine.Update(Frame(GamepadButtons.L1), 500);
        engine.Update(Frame(GamepadButtons.L1), 560);
        Check("组合：主键未过窗口不出", "keys=[] mod=0 mouse=0", Describe(engine));
        engine.Update(Frame(GamepadButtons.L1), 620);
        Check("组合：主键过窗口出单键", "keys=[0x06] mod=0 mouse=0", Describe(engine));
        engine.Update(Frame(GamepadButtons.None), 660);

        // 先按搭子再按主键：不算组合，各自出自己的映射。
        engine.Update(Frame(GamepadButtons.R1), 700);
        engine.Update(Frame(GamepadButtons.R1 | GamepadButtons.L1), 740);
        Check("组合：先按搭子不出组合", "keys=[0x08] mod=0 mouse=0", Describe(engine));
        engine.Update(Frame(GamepadButtons.None), 820);
    }

    /// <summary>组合不限于肩键：任意两颗都能配。</summary>
    private static void ComboAnyPair()
    {
        var engine = new KbmMappingEngine();
        engine.SetTiming(250, 100, 100, 28);
        engine.SetProfile(Profile(p =>
        {
            p.Set("A", BindingTrigger.Tap, BindingTarget.Key("Space"));
            p.Set("Y", BindingTrigger.Tap, BindingTarget.Key("R"));
            p.SetCombo("A", BindingTarget.Key("Escape"), "Y");
        }));

        engine.Update(Frame(GamepadButtons.South), 0);
        engine.Update(Frame(GamepadButtons.South | GamepadButtons.North), 40);
        Check("任意两键：A+Y 出组合键", "keys=[0x29] mod=0 mouse=0", Describe(engine));

        engine.Update(Frame(GamepadButtons.South), 120);
        engine.Update(Frame(GamepadButtons.None), 160);
        Check("任意两键：组合手势内不补单键", "keys=[] mod=0 mouse=0", Describe(engine));

        engine.Update(Frame(GamepadButtons.South), 260);
        engine.Update(Frame(GamepadButtons.South), 380);
        Check("任意两键：单按 A 过窗口出轻按", "keys=[0x2C] mod=0 mouse=0", Describe(engine));
    }

    private static void NoTap()
    {
        var engine = new KbmMappingEngine();
        engine.SetTiming(250, 100, 100, 28);
        engine.SetProfile(Profile(p =>
        {
            p.Set("GL", BindingTrigger.Long, BindingTarget.Key("Escape"));
            p.Set("GL", BindingTrigger.Double, BindingTarget.Key("Space"));
        }));

        engine.Update(Frame(GamepadButtons.PaddleLeft), 0);
        engine.Update(Frame(GamepadButtons.PaddleLeft), 400);
        engine.Update(Frame(GamepadButtons.None), 420);
        engine.Update(Frame(GamepadButtons.None), 600);
        Check("未绑轻按：双击长按都不出", "keys=[] mod=0 mouse=0", Describe(engine));
    }
}
