using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Pro2Plus;

public enum ViiperVirtualMode
{
    DualSenseLike,
    Pro1,
    Pro2,
    Xbox,
    DualSenseProfessionalImuTest,
    XboxProfessionalImuTest,
    // 键鼠模式：不创建游戏手柄，改为创建虚拟 USB 键盘 + 鼠标，
    // 由 KeyMouseMapper 把摇杆/陀螺/按键翻译成键鼠事件。
    KeyMouse
}

public sealed record ViiperDeviceProfile(
    ViiperVirtualMode Mode,
    string Label,
    string DeviceType,
    string ExpectedVid,
    string ExpectedPid,
    int InputSize,
    int FeedbackSize,
    TimeSpan SendInterval)
{
    public string DeviceSpecificSerialNumber { get; init; } = "";
    public bool SourcePaced { get; init; }

    public static ViiperDeviceProfile DualSenseLike { get; } = new(
        ViiperVirtualMode.DualSenseLike,
        "PS5 兼容模式",
        "dualsensehaptic",
        "054c",
        "0ce6",
        PadFrameEncoder.DualSenseSize,
        DualSenseHapticFrame.WireSize,
        TimeSpan.FromMilliseconds(4));

    public static ViiperDeviceProfile Pro1 { get; } = new(
        ViiperVirtualMode.Pro1,
        "Pro1 / Switch Pro",
        "nspro",
        "057e",
        "2009",
        PadFrameEncoder.SwitchSize,
        10,
        TimeSpan.FromMilliseconds(4));

    public static ViiperDeviceProfile Pro2 { get; } = new(
        ViiperVirtualMode.Pro2,
        "Pro2 / Nintendo",
        "ns2pro",
        "057e",
        "2069",
        PadFrameEncoder.SwitchSize,
        34,
        TimeSpan.FromMilliseconds(4));

    public static ViiperDeviceProfile Xbox { get; } = new(
        ViiperVirtualMode.Xbox,
        "Xbox / XInput",
        "xbox360",
        "045e",
        "028e",
        PadFrameEncoder.XboxSize,
        2,
        TimeSpan.FromMilliseconds(4));

    public static ViiperDeviceProfile DualSenseProfessionalImuTest { get; } = new(
        ViiperVirtualMode.DualSenseProfessionalImuTest,
        "PS5 / Professional IMU Test",
        "dualsensehaptic",
        "054c",
        "0ce6",
        PadFrameEncoder.DualSenseSize,
        DualSenseHapticFrame.WireSize,
        TimeSpan.FromMilliseconds(4));

    public static ViiperDeviceProfile XboxProfessionalImuTest { get; } = new(
        ViiperVirtualMode.XboxProfessionalImuTest,
        "Xbox / Professional IMU Test",
        "xbox360",
        "045e",
        "028e",
        PadFrameEncoder.XboxSize,
        2,
        TimeSpan.FromMilliseconds(4));

    // 键鼠模式的主设备身份取虚拟键盘（VIIPER 内置 keyboard 设备）。
    // 鼠标设备在同一 bus 上第二个创建，identity 校验在会话里单独处理。
    public static ViiperDeviceProfile KeyMouse { get; } = new(
        ViiperVirtualMode.KeyMouse,
        "键鼠 / WASD + 鼠标",
        "keyboard",
        "2e8a",
        "0010",
        64,
        1,
        TimeSpan.FromMilliseconds(1));

    public bool IsPs5Family =>
        Mode is ViiperVirtualMode.DualSenseLike
            or ViiperVirtualMode.DualSenseProfessionalImuTest;

    public bool UsesDualSenseHaptics =>
        Mode is ViiperVirtualMode.DualSenseLike
            or ViiperVirtualMode.DualSenseProfessionalImuTest;

    public bool IsProfessionalImuTest =>
        Mode is ViiperVirtualMode.DualSenseProfessionalImuTest
            or ViiperVirtualMode.XboxProfessionalImuTest;

    public bool MatchesIdentity(BridgeDeviceInfo device)
    {
        bool typeOk = string.Equals(
            DeviceType,
            device.Type,
            StringComparison.OrdinalIgnoreCase);
        bool vidOk = string.Equals(
            NormalizeHex(ExpectedVid),
            NormalizeHex(device.Vid),
            StringComparison.OrdinalIgnoreCase);
        bool pidOk = string.Equals(
            NormalizeHex(ExpectedPid),
            NormalizeHex(device.Pid),
            StringComparison.OrdinalIgnoreCase);
        return typeOk && vidOk && pidOk;
    }

    public IReadOnlyDictionary<string, object?> DeviceSpecificArguments()
    {
        var arguments = new Dictionary<string, object?>();
        // 后端需要知道报文节奏由物理输入驱动还是由主机轮询超时驱动。
        // 不能写死 true：显式固定推送率的客户端要保留自己的策略。
        arguments["source_paced"] = SourcePaced;
        if (!string.IsNullOrWhiteSpace(DeviceSpecificSerialNumber))
        {
            arguments["serial_number"] = DeviceSpecificSerialNumber.Trim();
            if (IsPs5Family)
            {
                // dualsensehaptic 自己分配身份，不走旧后端的 MAC 生成器。
                // Steam/SDL 可能读 feature 0x09 当作身份而不是 USB 序列号：
                // 1 号槽保留历史 MAC，其余槽按序列号哈希出稳定的本地管理单播地址。
                string serial = DeviceSpecificSerialNumber.Trim();
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(serial));
                hash[0] = (byte)((hash[0] & 0xFC) | 2);
                arguments["mac_address"] = serial == SlotSerialNumber(ViiperVirtualMode.DualSenseLike, 1)
                    ? "A5:FA:9C:CF:92:00" : string.Join(":", Array.ConvertAll(hash[..6], b => b.ToString("X2")));
            }
        }

        if (Mode is ViiperVirtualMode.Pro2 or ViiperVirtualMode.Pro1)
        {
            arguments["input_interval_ms"] = Math.Clamp(
                (int)Math.Round(SendInterval.TotalMilliseconds),
                1,
                255);
        }
        if (Mode == ViiperVirtualMode.Pro2)
        {
            // Pro2 协议假定 4 ms 端点时钟。应用层保持按源节奏推送，
            // 由 USB 端点单独做零阶保持重采样；重复的报告不计入新采样。
            arguments["protocol_clock_resample"] = SourcePaced;
        }

        return arguments;
    }

    public static string SlotSerialNumber(ViiperVirtualMode mode, int slotIndex)
    {
        int normalizedSlot = Math.Clamp(slotIndex, 1, 4);
        return mode switch
        {
            ViiperVirtualMode.Pro2 => "LC-V624-NS2PRO-S" + normalizedSlot,
            ViiperVirtualMode.DualSenseLike => "LC-V624-DS5-S" + normalizedSlot,
            ViiperVirtualMode.Pro1 => "PRO2-NSPRO-S" + normalizedSlot,
            ViiperVirtualMode.Xbox => "LC-V624-XBOX-S" + normalizedSlot,
            _ => "LC-V624-VPAD-S" + normalizedSlot
        };
    }

    private static string NormalizeHex(string value)
    {
        string text = (value ?? "").Trim().ToLowerInvariant();
        if (text.StartsWith("0x", StringComparison.Ordinal))
        {
            text = text[2..];
        }
        if (text.StartsWith("vid_", StringComparison.Ordinal) ||
            text.StartsWith("pid_", StringComparison.Ordinal))
        {
            text = text[4..];
        }
        return text.PadLeft(4, '0');
    }
}

/// <summary>
/// 虚拟手柄报文的对外门面。编码逻辑在 <see cref="PadFrameEncoder"/>；
/// 这里保留按需分配的便捷入口，热路径请用 <see cref="WriteFrame"/> 复用缓冲区。
/// </summary>
public static class VirtualPadPackets
{
    /// <summary>分配一帧并写入中立状态；会话建连与断开兜底使用。</summary>
    public static byte[] NeutralInput(ViiperDeviceProfile profile)
    {
        int size = PadFrameEncoder.SizeOf(profile.Mode);
        if (size == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }
        byte[] frame = new byte[size];
        PadFrameEncoder.WriteNeutral(frame, profile.Mode);
        return frame;
    }

    /// <summary>热路径入口：写进调用方缓冲区，返回本次报文长度。</summary>
    public static int WriteFrame(
        Span<byte> frame,
        ViiperDeviceProfile profile,
        GamepadState? state,
        GyroAxisInversion gyroAxisInversion = default,
        Ps5ImuMapping? ps5ImuMapping = null,
        Ps5OutputImuTuning? ps5OutputImuTuning = null,
        DualSenseImuRawSample? professionalDualSenseImu = null,
        GamepadState? professionalXboxState = null)
    {
        int size = PadFrameEncoder.SizeOf(profile.Mode);
        if (size == 0 || frame.Length < size)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }
        // ps5ImuMapping 目前不参与字节生成，IMU 轴序由固定协议映射决定。
        _ = ps5ImuMapping;
        PadFrameEncoder.Write(
            frame,
            profile,
            state ?? GamepadState.Neutral(),
            gyroAxisInversion,
            (ps5OutputImuTuning ?? Ps5OutputImuTuning.Default).Normalize(),
            professionalDualSenseImu,
            professionalXboxState);
        return size;
    }

    /// <summary>分配式入口，供诊断、审计与一次性调用使用。</summary>
    public static byte[] FromGamepad(
        ViiperDeviceProfile profile,
        GamepadState? state,
        GyroAxisInversion gyroAxisInversion = default,
        Ps5ImuMapping? ps5ImuMapping = null,
        Ps5OutputImuTuning? ps5OutputImuTuning = null,
        DualSenseImuRawSample? professionalDualSenseImu = null,
        GamepadState? professionalXboxState = null)
    {
        int size = PadFrameEncoder.SizeOf(profile.Mode);
        if (size == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }
        byte[] frame = new byte[size];
        WriteFrame(
            frame,
            profile,
            state,
            gyroAxisInversion,
            ps5ImuMapping,
            ps5OutputImuTuning,
            professionalDualSenseImu,
            professionalXboxState);
        return frame;
    }

    public static string FeedbackSummary(ViiperDeviceProfile profile, byte[] data)
    {
        return profile.Mode switch
        {
            ViiperVirtualMode.DualSenseLike when
                DualSenseHapticFrame.TryParse(data, out DualSenseHapticFrame frame, out _) =>
                $"DualSense haptic frame: kind={frame.Kind}, bytes={frame.Payload.Length}",
            ViiperVirtualMode.DualSenseProfessionalImuTest when
                DualSenseHapticFrame.TryParse(data, out DualSenseHapticFrame frame, out _) =>
                $"DualSense Professional haptic frame: kind={frame.Kind}, bytes={frame.Payload.Length}",
            ViiperVirtualMode.Pro1 when data.Length >= 10 =>
                $"Switch Pro output: flags=0x{data[8]:X2}, player_led=0x{data[9]:X2}, L={Hex(data.AsSpan(0, 4))}, R={Hex(data.AsSpan(4, 4))}",
            ViiperVirtualMode.Pro2 when data.Length >= 34 =>
                $"NS2Pro output: flags=0x{data[32]:X2}, player_led=0x{data[33]:X2}, L={Hex(data.AsSpan(0, 6))}, R={Hex(data.AsSpan(16, 6))}",
            ViiperVirtualMode.Xbox when data.Length >= 2 =>
                $"XInput output: left={data[0]}, right={data[1]}",
            ViiperVirtualMode.XboxProfessionalImuTest when data.Length >= 2 =>
                $"XInput Professional output: left={data[0]}, right={data[1]}",
            _ => profile.Label + " output: " + Hex(data)
        };
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
