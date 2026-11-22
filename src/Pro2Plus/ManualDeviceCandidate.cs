using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Devices.Bluetooth;

namespace Pro2Plus;

/// <summary>
/// 手动连接列表里的广播候选。MAC 文本与控制字符过滤都手写完成，
/// 不为每个候选分配 6 个临时字符串和迭代器；排序仍沿用稳定排序语义。
/// </summary>
public sealed record ManualDeviceCandidate(ulong Address, BluetoothAddressType AddressType,
    string Name, short Rssi, bool NintendoManufacturer, int Score)
{
    private const string HexDigits = "0123456789ABCDEF";

    public string AddressText
    {
        get
        {
            Span<char> chars = stackalloc char[17];
            for (int i = 0; i < 6; i++)
            {
                int value = (int)((Address >> ((5 - i) * 8)) & 0xFF);
                if (i > 0) chars[i * 3 - 1] = ':';
                chars[i * 3] = HexDigits[value >> 4];
                chars[i * 3 + 1] = HexDigits[value & 0xF];
            }
            return new string(chars);
        }
    }

    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name)) return "未提供设备名称";
            var buffer = new char[Math.Min(80, Name.Length)];
            int count = 0;
            foreach (char c in Name)
            {
                if (char.IsControl(c)) continue;
                if (count >= 80) break;
                buffer[count++] = c;
            }
            return new string(buffer, 0, count);
        }
    }

    public string AddressDetail => AddressText +
        (AddressType == BluetoothAddressType.Random ? " · 随机地址" : " · 公共地址");
    public string Confidence => Score >= 60 ? "高匹配" : Score >= 30 ? "可能是 Pro2" : "低匹配";
    public string Signal => Rssi + " dBm";
    public string Evidence => NintendoManufacturer ? "Nintendo 广播标识" :
        Score >= 30 ? "设备名称匹配" : "未匹配 Pro2 标识";
    public bool IsValid => Address > 0 && Address <= 0xFFFFFFFFFFFF &&
        AddressType is BluetoothAddressType.Public or BluetoothAddressType.Random;

    public static IReadOnlyList<ManualDeviceCandidate> Sort(IEnumerable<ManualDeviceCandidate> candidates) =>
        candidates.Where(c => c.IsValid).OrderByDescending(c => c.Score).ThenByDescending(c => c.Rssi)
            .ThenBy(c => c.Address).ThenBy(c => c.AddressType).ToArray();
}

public sealed record ManualConnectionResult(bool Connected, string Message);
