using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Pro2Plus;

/// <summary>一次无线电枚举的结果：能拿到的接口 + 逐条失败原因。</summary>
internal sealed record BluetoothRadioScan(IReadOnlyList<RadioDiagnosticInfo> Radios, IReadOnlyList<string> Warnings);

/// <summary>
/// 只做枚举与身份读取：不开 BLE 连接、不读 GATT、不写注册表。
/// 整体 8 秒预算，超时后保留已拿到的接口而不是让整块信息空白。
/// </summary>
internal static class BluetoothRadioInventory
{
    private const int MaximumRadios = 16;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);

    internal static async Task<BluetoothRadioScan> ScanAsync(CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var radios = new List<RadioDiagnosticInfo>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        try
        {
            BluetoothAdapter? preferred = await BluetoothAdapter.GetDefaultAsync().AsTask(budget.Token);
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(
                BluetoothAdapter.GetDeviceSelector()).AsTask(budget.Token);

            // 默认无线电优先，其余按枚举顺序；同一接口只读一次。
            var interfaceIds = new List<string>();
            if (preferred != null) interfaceIds.Add(preferred.DeviceId);
            foreach (DeviceInformation device in devices)
            {
                if (!interfaceIds.Contains(device.Id, StringComparer.OrdinalIgnoreCase)) interfaceIds.Add(device.Id);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int attempted = 0;
            foreach (string id in interfaceIds)
            {
                if (attempted >= MaximumRadios) break;
                if (!seen.Add(id)) continue;
                attempted++;
                budget.Token.ThrowIfCancellationRequested();
                try
                {
                    RadioDiagnosticInfo? radio = await ReadRadioAsync(id, preferred, budget.Token);
                    if (radio == null)
                    {
                        warnings.Add("适配器接口不可用");
                        continue;
                    }

                    radios.Add(radio);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    warnings.Add("适配器读取失败：" + ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            warnings.Add("蓝牙信息读取超时，保留已获取的设备");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add("蓝牙信息不可用：" + ex.GetType().Name);
        }

        return new BluetoothRadioScan(radios, warnings);
    }

    private static async Task<RadioDiagnosticInfo?> ReadRadioAsync(string id, BluetoothAdapter? preferred,
        CancellationToken token)
    {
        bool isDefault = preferred != null && string.Equals(id, preferred.DeviceId, StringComparison.OrdinalIgnoreCase);
        BluetoothAdapter? radio = isDefault ? preferred : await BluetoothAdapter.FromIdAsync(id).AsTask(token);
        if (radio == null) return null;

        BluetoothAdapterDiagnosticInfo driver = await Win10BleDiagnostic.CaptureAdapterAsync(radio).WaitAsync(token);
        return new RadioDiagnosticInfo(driver, isDefault, FormatAddress(radio.BluetoothAddress),
            radio.IsLowEnergySupported, radio.IsCentralRoleSupported, radio.IsPeripheralRoleSupported,
            radio.IsClassicSupported);
    }

    /// <summary>48 位地址按 AA:BB:CC:DD:EE:FF 输出，与系统「设备管理器」里看到的一致。</summary>
    internal static string FormatAddress(ulong address)
    {
        string hex = address.ToString("X12");
        var text = new StringBuilder(17);
        for (int i = 0; i < hex.Length; i += 2)
        {
            if (i > 0) text.Append(':');
            text.Append(hex, i, 2);
        }

        return text.ToString();
    }
}
