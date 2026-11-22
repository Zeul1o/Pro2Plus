using System;
using System.IO;
using System.Text.Json;
using Windows.Devices.Bluetooth;

namespace Pro2Plus.Connection;

/// <summary>
/// 记住上次成功连接的手柄地址。
///
/// 蓝牙手柄只要被系统连过一次，就不再对外广播；这时扫描 10 秒也发现不了它，
/// 但按地址直接发起 GATT 连接是可以成功的。存档只用于「直连优先」，
/// 失败仍然回退到扫描，所以换了手柄或清了配对也不会卡死。
/// </summary>
internal static class BleAddressStore
{
    private sealed record Payload(ulong Address, int AddressType, string Name);

    private static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PRO2Assistant");

    private static string FilePath => Path.Combine(Directory, "ble_last.json");

    public static bool TryLoad(out Pro2BleCandidate candidate)
    {
        candidate = default;
        try
        {
            if (!File.Exists(FilePath)) return false;

            Payload? payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(FilePath));
            if (payload is null || payload.Address == 0) return false;

            candidate = new Pro2BleCandidate(
                payload.Address,
                (BluetoothAddressType)payload.AddressType,
                0,
                payload.Name ?? "");
            return true;
        }
        catch
        {
            // 存档损坏就当没有，走扫描。
            return false;
        }
    }

    public static void Save(Pro2BleCandidate candidate)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(new Payload(
                    candidate.Address,
                    (int)candidate.AddressType,
                    candidate.Name ?? "")));
        }
        catch
        {
            // 存档失败不影响连接。
        }
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { }
    }
}
