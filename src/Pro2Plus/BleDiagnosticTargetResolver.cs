using System;
using System.Collections.Generic;

namespace Pro2Plus;

/// <summary>Read-only diagnostic targeting. Never infer device identity from an arbitrary MAC.</summary>
public static class BleDiagnosticTargetResolver
{
    public static string Resolve(string? explicitAddress, IEnumerable<string?> saved)
    {
        if (!string.IsNullOrWhiteSpace(explicitAddress))
        {
            if (!TryParsePublicAddress(explicitAddress, out _))
                throw new ArgumentException("Invalid --preferred-address; expected a six-byte Bluetooth address.");
            return AppSettings.NormalizeBleAddress(explicitAddress);
        }

        foreach (string? address in saved)
            if (TryParsePublicAddress(address, out _)) return AppSettings.NormalizeBleAddress(address);
        return "";
    }

    public static bool TryParsePublicAddress(string? text, out ulong address)
    {
        address = 0;
        string normalized = AppSettings.NormalizeBleAddress(text);
        // 标准写法 17 字符：11 个十六进制位 + 5 个冒号。边解析边校验，
        // 不再先 Replace 出一个去冒号的字符串再交给 ulong.TryParse。
        if (normalized.Length != 17) return false;

        ulong value = 0;
        for (int i = 0; i < normalized.Length; i++)
        {
            if (i % 3 == 2)
            {
                if (normalized[i] != ':') return false;
                continue;
            }

            int digit = HexDigit(normalized[i]);
            if (digit < 0) return false;
            value = (value << 4) | (uint)digit;
        }

        // 全零与广播地址都不是可用的公共地址。
        if (value == 0 || value == 0xFFFFFFFFFFFF) return false;
        address = value;
        return true;
    }

    public static bool CanInitializeUnadvertisedTarget(bool exactPro2Channels) => exactPro2Channels;

    private static int HexDigit(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1
    };
}
