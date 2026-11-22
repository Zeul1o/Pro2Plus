using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pro2Plus.Mapping;

/// <summary>一份有名字的配置。</summary>
public sealed class ProfileEntry
{
    public string Name { get; set; } = "默认";
    public MappingProfile Bindings { get; set; } = new();
}

/// <summary>一个输出模式下的全部配置，以及当前启用的那一份。</summary>
public sealed class ProfileSet
{
    public string Active { get; set; } = "默认";
    public List<ProfileEntry> Profiles { get; set; } = new();

    public ProfileEntry Current
    {
        get
        {
            ProfileEntry? hit = Profiles.FirstOrDefault(p => p.Name == Active);
            if (hit is not null) return hit;
            if (Profiles.Count == 0) Profiles.Add(new ProfileEntry { Name = "默认", Bindings = DefaultFor(mode) });
            Active = Profiles[0].Name;
            return Profiles[0];
        }
    }

    /// <summary>建这份集合时用的模式，用来生成默认表；不参与序列化。</summary>
    [JsonIgnore] public string mode = "xbox";

    public void Add(string name, MappingProfile? bindings = null)
    {
        string unique = UniqueName(name);
        Profiles.Add(new ProfileEntry { Name = unique, Bindings = bindings ?? DefaultFor(mode).Clone() });
        Active = unique;
    }

    public void Remove(string name)
    {
        if (Profiles.Count <= 1) return;                 // 至少留一份，否则没有可编辑的草稿
        ProfileEntry? hit = Profiles.FirstOrDefault(p => p.Name == name);
        if (hit is null) return;
        Profiles.Remove(hit);
        if (Active == name) Active = Profiles[0].Name;
    }

    public void Rename(string from, string to)
    {
        ProfileEntry? hit = Profiles.FirstOrDefault(p => p.Name == from);
        if (hit is null) return;
        string unique = UniqueName(to, from);
        hit.Name = unique;
        if (Active == from) Active = unique;
    }

    public string UniqueName(string wanted, string? ignore = null)
    {
        string name = string.IsNullOrWhiteSpace(wanted) ? "配置" : wanted.Trim();
        if (name.Length > 16) name = name[..16];
        if (!Profiles.Any(p => p.Name == name && p.Name != ignore)) return name;
        for (int i = 2; ; i++)
        {
            string candidate = name + " " + i;
            if (!Profiles.Any(p => p.Name == candidate && p.Name != ignore)) return candidate;
        }
    }

    public static MappingProfile DefaultFor(string mode) => mode switch
    {
        "kbm" => MappingProfile.CreateKeyboardDefault(),
        "xbox" => MappingProfile.CreateIdentityDefault("xbox"),
        "ps5" => MappingProfile.CreateIdentityDefault("ps5"),
        _ => MappingProfile.CreateIdentityDefault("pro2")
    };
}

/// <summary>
/// 三套模式（Xbox / PS5 / 键鼠）各自的配置库，落到 bindings.json。
/// 旧文件是单份 MappingProfile，读到时迁移成「默认」配置，不丢用户已有设置。
/// </summary>
public sealed class MappingLibrary
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public Dictionary<string, ProfileSet> Modes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>运行时要用的模式键：xbox / ps5 / kbm。</summary>
    public static string ModeKey(string? layout) => layout switch
    {
        "ps5" => "ps5",
        "kbm" => "kbm",
        _ => "xbox"
    };

    public ProfileSet For(string mode)
    {
        if (!Modes.TryGetValue(mode, out ProfileSet? set) || set is null)
        {
            set = new ProfileSet { mode = mode };
            set.Profiles.Add(new ProfileEntry { Name = "默认", Bindings = ProfileSet.DefaultFor(mode) });
            Modes[mode] = set;
        }
        set.mode = mode;
        if (set.Profiles.Count == 0)
            set.Profiles.Add(new ProfileEntry { Name = "默认", Bindings = ProfileSet.DefaultFor(mode) });
        return set;
    }

    public MappingProfile ActiveBindings(string mode) => For(mode).Current.Bindings;

    public static string FilePath => MappingStore.FilePath;

    public static MappingLibrary Load(string? path = null)
    {
        string file = path ?? FilePath;
        try
        {
            if (!File.Exists(file)) return CreateDefault();
            string json = File.ReadAllText(file);

            // 旧格式：根节点直接是 { "Bindings": { ... } }
            if (json.Contains("\"Bindings\"", StringComparison.Ordinal) &&
                !json.Contains("\"Modes\"", StringComparison.Ordinal))
            {
                MappingProfile legacy = JsonSerializer.Deserialize<MappingProfile>(json) ?? new MappingProfile();
                MappingLibrary migrated = CreateDefault();
                // 手柄两套沿用旧表，但把旧表里没写的键补成透传——否则升级后这些键会突然没输出。
                foreach (string mode in new[] { "xbox", "ps5" })
                    migrated.For(mode).Profiles[0].Bindings = legacy.Clone().FillIdentity(mode);
                // 键鼠模式不沿用旧表（里面是手柄目标），直接给键鼠出厂表。
                migrated.For("kbm").Profiles[0].Bindings = MappingProfile.CreateKeyboardDefault();
                return migrated;
            }

            MappingLibrary? lib = JsonSerializer.Deserialize<MappingLibrary>(json);
            if (lib is null) return CreateDefault();
            foreach (string mode in new[] { "xbox", "ps5" }) lib.For(mode).Current.Bindings.FillIdentity(mode);
            lib.For("kbm");
            return lib;
        }
        catch
        {
            return CreateDefault();
        }
    }

    public bool Save(string? path = null)
    {
        string file = path ?? FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            Version = CurrentVersion;
            File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static MappingLibrary CreateDefault()
    {
        var lib = new MappingLibrary();
        foreach (string mode in new[] { "xbox", "ps5", "kbm" }) lib.For(mode);
        return lib;
    }
}
