using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Pro2Plus;

public enum StackEvidenceLevel
{
    Blocked,
    ExperimentalNoParameterNames,
    ExperimentalPartialParameterNames,
    ExperimentalAllParameterNames,
    AuditedHash
}

/// <summary>
/// 可回退的 Win10 参数实验所需的只读资格审查。参数名字符串只证明「存在线索」，
/// 既不代表注册表读取器可用，也不代表链路间隔真的生效；不触碰无线电、注册表写入、
/// 进程提权或驱动加载。PE 头检查是格式契约，逐字段保持原样。
/// </summary>
public sealed record StackParameterAssessment(
    int WindowsBuild, string StackPath, string StackVersion, string StackSha256,
    StackEvidenceLevel EvidenceLevel, IReadOnlyList<string> ParameterNamesFound,
    bool ValidSystemX64Image, string Reason)
{
    public bool IsAudited => EvidenceLevel == StackEvidenceLevel.AuditedHash;
    public bool CanExperiment => ValidSystemX64Image && EvidenceLevel != StackEvidenceLevel.Blocked;
    public bool RequiresExperimentalConsent => CanExperiment && !IsAudited;
    public bool CanApply(bool experimentalConsent) => CanExperiment && (IsAudited || experimentalConsent);

    public string EvidenceSummary => IsAudited
        ? "已审查系统栈；仍须以真实 FD2 实测确认当前硬件是否采用参数。"
        : CanExperiment
            ? "未审查系统栈（实验路线）；参数名线索 " + ParameterNamesFound.Count + "/4，不证明驱动读取这些值，也不保证 7.5ms。"
            : "不能执行此 Win10 实验：" + Reason;

    // 参数名的 ASCII / UTF-16 字节序列在启动时预建，避免每次审查都重新编码。
    private static readonly (string Name, byte[] Ascii, byte[] Utf16)[] ParameterNames = BuildParameterNames();

    private static (string, byte[], byte[])[] BuildParameterNames()
    {
        var result = new List<(string, byte[], byte[])>(4);
        foreach (string name in BleStackProfile.Values.Keys)
        {
            result.Add((name, Encoding.ASCII.GetBytes(name), Encoding.Unicode.GetBytes(name)));
        }
        return result.ToArray();
    }

    public static StackParameterAssessment ReadCurrent()
    {
        string path = Path.Combine(Environment.SystemDirectory, "drivers", "bthport.sys");
        int build = Environment.OSVersion.Version.Build;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 512 || info.Length > 64 * 1024 * 1024)
                return Blocked(build, path, "系统 bthport.sys 缺失或大小异常。");
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                return Blocked(build, path, "系统 bthport.sys 是重解析点，不在本工具的可确认范围内。");
            string version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown";
            return Analyze(build, OperatingSystem.IsWindows() && Environment.Is64BitOperatingSystem &&
                Environment.Is64BitProcess && RuntimeInformation.ProcessArchitecture == Architecture.X64,
                path, path, File.ReadAllBytes(path), version);
        }
        catch (Exception ex)
        {
            return Blocked(build, path, "无法只读检查系统 bthport.sys：" + ex.Message);
        }
    }

    /// <summary>纯 PE / 路径检查，供离线回归用例确定性验证。</summary>
    public static StackParameterAssessment Analyze(int windowsBuild, bool windowsX64,
        string expectedSystemPath, string actualPath, byte[] image, string stackVersion)
    {
        string path = actualPath ?? "";
        if (windowsBuild < 19041 || windowsBuild >= 22000)
            return Blocked(windowsBuild, path, "仅支持 Windows 10 build 19041–21999；Win11 使用连接参数 API。");
        if (!windowsX64)
            return Blocked(windowsBuild, path, "仅支持 Windows x64 系统及 x64 程序。");
        try
        {
            if (!string.Equals(Path.GetFullPath(expectedSystemPath), Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(path), "bthport.sys", StringComparison.OrdinalIgnoreCase))
                return Blocked(windowsBuild, path, "目标不是指定的系统 bthport.sys。");
        }
        catch (Exception ex) { return Blocked(windowsBuild, path, "系统路径无效：" + ex.Message); }
        if (image == null || image.Length < 512 || image.Length > 64 * 1024 * 1024 || !IsValidX64Driver(image))
            return Blocked(windowsBuild, path, "bthport.sys 不是结构有效的 x64 PE 系统驱动。");

        string hash = Convert.ToHexString(SHA256.HashData(image));
        var found = new List<string>(4);
        ReadOnlySpan<byte> data = image;
        foreach ((string name, byte[] ascii, byte[] utf16) in ParameterNames)
        {
            if (data.IndexOf(ascii) >= 0 || data.IndexOf(utf16) >= 0) found.Add(name);
        }
        bool audited = BleStackProfile.IsAudited(windowsBuild, hash);
        StackEvidenceLevel level = audited ? StackEvidenceLevel.AuditedHash : found.Count switch
        {
            0 => StackEvidenceLevel.ExperimentalNoParameterNames,
            4 => StackEvidenceLevel.ExperimentalAllParameterNames,
            _ => StackEvidenceLevel.ExperimentalPartialParameterNames
        };
        return new(windowsBuild, path, stackVersion ?? "unknown", hash, level,
            Array.AsReadOnly(found.ToArray()), true, audited
                ? "精确匹配既有已审查系统栈；不同适配器仍需测量。"
                : "结构有效但此系统栈未审查。字符串只作线索；需明确同意可恢复实验，不作为驱动支持证明。");
    }

    private static bool IsValidX64Driver(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data[0] != 'M' || data[1] != 'Z') return false;
        int pe = BitConverter.ToInt32(data.Slice(0x3C, 4));
        if (pe < 0x40 || pe > data.Length - 24 ||
            BitConverter.ToUInt32(data.Slice(pe, 4)) != 0x00004550 ||
            BitConverter.ToUInt16(data.Slice(pe + 4, 2)) != 0x8664) return false;
        int sections = BitConverter.ToUInt16(data.Slice(pe + 6, 2));
        int optionalSize = BitConverter.ToUInt16(data.Slice(pe + 20, 2));
        int optional = pe + 24;
        if (sections < 1 || sections > 96 || optionalSize < 112 ||
            optional > data.Length - optionalSize ||
            BitConverter.ToUInt16(data.Slice(optional, 2)) != 0x20B ||
            BitConverter.ToUInt16(data.Slice(optional + 68, 2)) != 1 ||
            (BitConverter.ToUInt16(data.Slice(pe + 22, 2)) & 2) == 0) return false;
        int table = optional + optionalSize;
        if (table > data.Length - sections * 40) return false;
        uint headers = BitConverter.ToUInt32(data.Slice(optional + 60, 4));
        if (headers < table + sections * 40 || headers > data.Length) return false;
        for (int i = 0; i < sections; ++i)
        {
            int entry = table + i * 40;
            uint length = BitConverter.ToUInt32(data.Slice(entry + 16, 4));
            uint offset = BitConverter.ToUInt32(data.Slice(entry + 20, 4));
            if (length != 0 && (offset < headers || (ulong)offset + length > (ulong)data.Length)) return false;
        }
        return true;
    }

    private static StackParameterAssessment Blocked(int build, string path, string reason) =>
        new(build, path, "unknown", "", StackEvidenceLevel.Blocked,
            Array.Empty<string>(), false, reason);
}
