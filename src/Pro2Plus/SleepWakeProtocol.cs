using System;
using System.Security.Cryptography;

namespace Pro2Plus;

/// <summary>
/// 配对与休眠流程的纯字节构造，不含任何 IO。命令布局按手柄固件的实测报文顺序，
/// 地址按手柄要求以低字节在前传输；密钥交换结果只做字节级校验。
/// </summary>
public static class SleepWakeProtocol
{
    public const uint PairingFlashAddress = 0x1FA000;
    public const ushort NintendoCompanyId = 0x0553;

    /// <summary>06/02：断开链路并熄灭指示灯；已配对时按键会重新广播。</summary>
    public static byte[] SleepCommand()
    {
        byte[] request = new byte[20];
        new byte[] { 0x06, 0x91, 0x01, 0x02, 0x00, 0x0C, 0x00, 0x00 }.CopyTo(request, 0);
        return request;
    }

    /// <summary>手柄使用的倒序（小端）蓝牙地址字节。</summary>
    public static byte[] ReversedAddress(ulong address)
    {
        byte[] bytes = new byte[6];
        for (int i = 0; i < 6; i++) bytes[i] = (byte)(address >> (8 * i));
        return bytes;
    }

    /// <summary>15/01：带两个主机地址（原地址与最低字节减一的地址）。</summary>
    public static byte[] ExchangeAddressesCommand(ulong host)
    {
        byte[] first = ReversedAddress(host);
        byte[] second = (byte[])first.Clone();
        second[0] = (byte)(second[0] - 1);
        byte[] request = new byte[8 + 14];
        new byte[] { 0x15, 0x91, 0x01, 0x01, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x02 }.CopyTo(request, 0);
        first.CopyTo(request, 10);
        second.CopyTo(request, 16);
        return request;
    }

    public static byte[] ExchangeKeysCommand(byte[] a1) => KeyedCommand(0x04, a1);
    public static byte[] ConfirmCommand(byte[] a2) => KeyedCommand(0x02, a2);
    public static byte[] FinaliseCommand() => [0x15, 0x91, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00];

    private static byte[] KeyedCommand(byte subcommand, byte[] value)
    {
        if (value.Length != 16) throw new ArgumentException("16-byte value required", nameof(value));
        byte[] request = new byte[8 + 17];
        new byte[] { 0x15, 0x91, 0x01, subcommand, 0x00, 0x11, 0x00, 0x00, 0x00 }.CopyTo(request, 0);
        value.CopyTo(request, 9);
        return request;
    }

    /// <summary>LTK = A1 XOR B1（均按传输时的字节序）。</summary>
    public static byte[] ComputeLtk(ReadOnlySpan<byte> a1, ReadOnlySpan<byte> b1)
    {
        byte[] ltk = new byte[16];
        for (int i = 0; i < 16; i++) ltk[i] = (byte)(a1[i] ^ b1[i]);
        return ltk;
    }

    /// <summary>预期 15/02 响应：AES128-ECB（密钥 = 反转的 LTK，数据块 = 反转的 A2）。</summary>
    public static byte[] ExpectedConfirmResponse(byte[] ltk, byte[] a2)
    {
        byte[] key = (byte[])ltk.Clone();
        Array.Reverse(key);
        byte[] block = (byte[])a2.Clone();
        Array.Reverse(block);
        using Aes aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(block, PaddingMode.None);
    }

    /// <summary>配对区（起始 0x1FA000）内有 1–2 条记录且首条是本机时为 true。</summary>
    public static bool IsHostStored(ReadOnlySpan<byte> pairingArea, ulong host) =>
        pairingArea.Length >= 14 && pairingArea[0] is >= 1 and <= 2 &&
        Convert.ToHexString(pairingArea.Slice(8, 6)) == host.ToString("X12");
}

/// <summary>
/// 只用解析后的按键、摇杆、扳机判断用户是否活动；忽略 IMU，
/// 因此手柄平放在桌上仍算静止。调用方负责串行访问。
/// </summary>
public sealed class UserActivityTracker
{
    public const int AxisDelta = 320;
    public const int AxisHeld = 900;
    public const int TriggerDelta = 320;
    public const int TriggerHeld = 900;

    private bool initialised;
    private ulong buttons;
    private int lx = GamepadState.AxisCenter, ly = GamepadState.AxisCenter;
    private int rx = GamepadState.AxisCenter, ry = GamepadState.AxisCenter;
    private int l2, r2;

    public long LastActivityTicks { get; private set; }

    public void Reset(long nowTicks) => LastActivityTicks = nowTicks;

    /// <returns>这帧算作用户活动时返回 true。</returns>
    public bool Note(GamepadState s, long nowTicks)
    {
        ulong pressed = (ulong)s.Buttons;
        bool active = !initialised ||
            pressed != 0 || pressed != buttons ||
            Math.Abs(s.Lx - lx) > AxisDelta || Math.Abs(s.Ly - ly) > AxisDelta ||
            Math.Abs(s.Rx - rx) > AxisDelta || Math.Abs(s.Ry - ry) > AxisDelta ||
            Math.Abs(s.Lx - GamepadState.AxisCenter) > AxisHeld || Math.Abs(s.Ly - GamepadState.AxisCenter) > AxisHeld ||
            Math.Abs(s.Rx - GamepadState.AxisCenter) > AxisHeld || Math.Abs(s.Ry - GamepadState.AxisCenter) > AxisHeld ||
            Math.Abs(s.L2 - l2) > TriggerDelta || Math.Abs(s.R2 - r2) > TriggerDelta ||
            s.L2 > TriggerHeld || s.R2 > TriggerHeld;
        if (!active) return false;
        initialised = true;
        LastActivityTicks = nowTicks;
        buttons = pressed;
        lx = s.Lx; ly = s.Ly; rx = s.Rx; ry = s.Ry; l2 = s.L2; r2 = s.R2;
        return true;
    }
}
