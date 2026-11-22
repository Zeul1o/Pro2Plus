using System;
using Pro2Plus.Mapping;

namespace Pro2Plus;

/// <summary>映射配置与运行时之间的桥：切换模式、保存后重新下发。</summary>
public sealed partial class MainViewModel
{
    private MappingLibrary? mappingLibrary;

    public MappingLibrary MappingLibrary => mappingLibrary ??= MappingLibrary.Load();

    /// <summary>当前输出模式对应的配置键：xbox / ps5 / kbm。</summary>
    public string CurrentMappingMode =>
        IsKeyMouseSelected ? "kbm" : (CompactPs5Selected ? "ps5" : "xbox");

    /// <summary>把当前模式的启用配置推给输出通路。模式切换、保存、启动各调一次。</summary>
    public void RefreshMappingRuntime()
    {
        string mode = CurrentMappingMode;
        MappingProfile active = MappingLibrary.ActiveBindings(mode);

        // 判定窗口就是设置页里那两个值，键鼠模式的轻按延时由它们决定。
        KbmMappingEngine.Shared.SetTiming(HoldThresholdMs, DoubleTapIntervalMs, ComboWindowMs, 28);

        if (mode == "kbm")
        {
            // 键鼠模式：按键由 KbmMappingEngine 仲裁，手柄通路不参与。
            PadMappingEngine.SetProfile(null);
            KbmMappingEngine.Shared.SetProfile(active);
        }
        else
        {
            // 手柄模式只映射轻按。
            KbmMappingEngine.Shared.SetProfile(null);
            // 面键位置语义：输出平台决定字母落在哪，物理布局决定按下的是哪颗（ABXY 对调）
            string platform = mode == "ps5" ? "ps5" : IsPro2Selected ? "pro2" : "xbox";
            PadMappingEngine.SetPlatform(platform);
            if (active.FaceLayout is not ("xbox" or "pro2"))
                PadMappingEngine.SetFaceLayout(IsPro2Selected ? "pro2" : "xbox");
            PadMappingEngine.SetProfile(active);
        }

        AppendLog("[MAP] 下发 mode=" + mode + " profile=" + MappingLibrary.For(mode).Active);
    }

    /// <summary>映射页保存后调用：重新读盘并立刻生效。</summary>
    public void ApplyMappingFromDisk()
    {
        mappingLibrary = MappingLibrary.Load();
        RefreshMappingRuntime();
    }
}
