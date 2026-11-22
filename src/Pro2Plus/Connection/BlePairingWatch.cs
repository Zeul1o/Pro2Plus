using System;
using System.Threading;
using Pro2Plus.Connection;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace Pro2Plus;

/// <summary>
/// 常驻广播监听：手柄长按 2 秒进入配对后会开始广播，这里收到任天堂厂商数据就交给上层去连。
/// 只在未连接时运行——扫描和连接共用同一块无线电，边扫边连会把 7.5 ms 间隔拉坏。
/// </summary>
public sealed class BlePairingWatch : IDisposable
{
    private readonly ulong savedAddress;
    private readonly Action<Pro2BleCandidate> onMatch;
    private BluetoothLEAdvertisementWatcher? watcher;
    private int matched;

    public BlePairingWatch(ulong savedAddress, Action<Pro2BleCandidate> onMatch)
    {
        this.savedAddress = savedAddress;
        this.onMatch = onMatch;
    }

    public bool IsRunning =>
        watcher is not null && watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    public bool Start()
    {
        if (IsRunning) return true;
        var created = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        created.Received += OnReceived;
        try
        {
            created.Start();
        }
        catch
        {
            try { created.Received -= OnReceived; } catch { }
            return false;
        }

        watcher = created;
        return true;
    }

    public void Stop()
    {
        BluetoothLEAdvertisementWatcher? current = watcher;
        watcher = null;
        if (current is null) return;
        current.Received -= OnReceived;
        try { current.Stop(); } catch { }
    }

    public void Dispose() => Stop();

    private void OnReceived(
        BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        // 只要一台：命中后立刻停表，后续广播一律忽略。
        if (Interlocked.Exchange(ref matched, 1) != 0) return;

        bool nintendo = BlePairingWatchPolicy.IsNintendoAdvertisement(args.Advertisement.ManufacturerData);
        string name = args.Advertisement.LocalName ?? "";
        if (!nintendo || !BlePairingWatchPolicy.Accepts(args.BluetoothAddress, savedAddress, name))
        {
            Volatile.Write(ref matched, 0);
            return;
        }

        Stop();
        onMatch(new Pro2BleCandidate(
            args.BluetoothAddress, args.BluetoothAddressType, args.RawSignalStrengthInDBm, name));
    }
}

/// <summary>
/// 监听判据，纯逻辑可离线验证。有保存地址时只认该地址——
/// 配对过的设备地址稳定，比按名字猜可靠得多；没有保存地址时退化为
/// 「任天堂厂商数据 + 名字里带 Pro」。
/// </summary>
public static class BlePairingWatchPolicy
{
    internal const ushort NintendoCompanyId = 0x0553;

    public static bool IsNintendoAdvertisement(
        System.Collections.Generic.IList<BluetoothLEManufacturerData> data)
    {
        for (int i = 0; i < data.Count; i++)
        {
            if (data[i].CompanyId == NintendoCompanyId) return true;
        }
        return false;
    }

    public static bool Accepts(ulong address, ulong savedAddress, string? localName)
    {
        if (savedAddress != 0) return address == savedAddress;
        if (string.IsNullOrWhiteSpace(localName)) return false;
        return localName.Contains("pro", StringComparison.OrdinalIgnoreCase);
    }
}
