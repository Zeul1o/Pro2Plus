using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Pro2Plus.Connection;

/// <summary>扫描到的一台候选手柄。</summary>
public readonly record struct Pro2BleCandidate(
    ulong Address,
    BluetoothAddressType AddressType,
    short Rssi,
    string Name);

/// <summary>
/// Switch 2 Pro 的蓝牙（BLE GATT）链路。
///
/// 与有线不同，蓝牙是标准 GATT 通道，四个特征各管一件事：
///   * cmd   649d4ac9… 写命令
///   * ack   c765a961… 命令应答（必须订阅，否则初始化会盲发）
///   * fd2   ab7de9be… 高回报率输入帧
///   * legacy 7492866c… 旧格式报告 0x09，里面带电量档位
///
/// 这一层只负责「连上 + 收帧 + 报电量」，不解析按键。
/// </summary>
public sealed class Pro2BleLink : IDisposable
{
    private const ushort NintendoCompanyId = 0x0553;

    private static readonly Guid CmdUuid = Guid.Parse("649d4ac9-8eb7-4e6c-af44-1ea54fe5f005");
    private static readonly Guid AckUuid = Guid.Parse("c765a961-d9d8-4d36-a20a-5315b111836a");
    private static readonly Guid Fd2Uuid = Guid.Parse("ab7de9be-89fe-49ad-828f-118f09df7fd2");
    private static readonly Guid LegacyUuid = Guid.Parse("7492866c-ec3e-4619-8258-32755ffcc0f8");
    private static readonly Guid RumbleUuid = Guid.Parse("cc483f51-9258-427d-a939-630c31f72b05");
    private static readonly Guid ClientConfigUuid = Guid.Parse("00002902-0000-1000-8000-00805f9b34fb");

    /// <summary>FD2 的报告率，写进特征自带的自定义描述符（单位 Hz）。</summary>
    private const ushort Fd2ReportRateHz = 133;

    /// <summary>蓝牙初始化命令（0x91 系列，端口号是 0x01 而 USB 是 0x00）。</summary>
    private static readonly byte[][] InitCommands =
    [
        [0x03, 0x91, 0x01, 0x0d, 0x00, 0x08, 0x00, 0x00, 0x01, 0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff],
        [0x07, 0x91, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00],
        [0x16, 0x91, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00],
        [0x15, 0x91, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00],
        [0x0c, 0x91, 0x01, 0x02, 0x00, 0x04, 0x00, 0x00, 0xff, 0x00, 0x00, 0x00],
        [0x11, 0x91, 0x01, 0x03, 0x00, 0x00, 0x00, 0x00],
        [0x0a, 0x91, 0x01, 0x08, 0x00, 0x14, 0x00, 0x00, 0x01, 0xff, 0xff, 0xff, 0xff,
         0xff, 0xff, 0xff, 0xff, 0x35, 0x00, 0x46, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x0c, 0x91, 0x01, 0x04, 0x00, 0x04, 0x00, 0x00, 0xff, 0x00, 0x00, 0x00],
        [0x03, 0x91, 0x01, 0x0a, 0x00, 0x04, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00],
        [0x10, 0x91, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00],
        [0x01, 0x91, 0x01, 0x0c, 0x00, 0x00, 0x00, 0x00],
        [0x01, 0x91, 0x01, 0x01, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x09, 0x91, 0x01, 0x07, 0x00, 0x08, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x02, 0x91, 0x01, 0x04, 0x00, 0x08, 0x00, 0x00, 0x09, 0x7e, 0x00, 0x00, 0xa8, 0x30, 0x01, 0x00],
        [0x02, 0x91, 0x01, 0x04, 0x00, 0x08, 0x00, 0x00, 0x09, 0x7e, 0x00, 0x00, 0xe8, 0x30, 0x01, 0x00],
    ];

    private readonly List<GattDeviceService> _ownedServices = new();
    private BluetoothLEDevice? _device;

    // 旧版的两条关键经验：
    //   1) GATT 会话必须由我们持有（MaintainConnection），否则 Windows 把它
    //      当成一次性查询，订阅完第一帧之后就不再维持链路，FD2 直接静默；
    //   2) 连接参数按吞吐优化申请，否则默认的省电间隔会把 133Hz 压到个位数。
    private GattSession? _session;
    private BluetoothLEPreferredConnectionParametersRequest? _preferenceRequest;
    private GattCharacteristic? _cmd;
    private GattCharacteristic? _ack;
    private GattCharacteristic? _fd2;
    private GattCharacteristic? _legacy;
    private GattCharacteristic? _rumble;
    private TaskCompletionSource<byte[]>? _ackTcs;
    private volatile bool _closing;

    private long _reportCount;
    private long _legacyCount;
    private readonly SemaphoreSlim _rumbleSignal = new(0, 1);
    private byte[]? _pendingRumble;
    // 与有线一致的「持续刷新」模型：HD 马达一包只覆盖约 36 ms，主机只在
    // 强度变化时给一帧，必须按 12 ms 重发最新帧，否则听感是断续的咔哒。
    private byte[]? _heldRumble;
    private long _heldRumbleTicks;
    private readonly RumbleWritePacingProbe _rumblePace = new();
    private long _rumbleRefreshGenerated;
    private Task? _rumbleLoop;
    private int _rumblePacketId;
    private long _rumbleQueued;
    private long _rumbleWritten;
    private long _rumbleEncodeFail;
    private const int RumbleIntervalMs = 12;
    private const int RumbleHoldMs = 400;
    private long _legacyLogged;
    private string _rateStatus = "not_requested";
    private string _sessionStatus = "not_opened";
    private string _intervalStatus = "not_requested";

    /// <summary>诊断日志出口。</summary>
    public Action<string>? Log { get; set; }

    public long LegacyCount => Interlocked.Read(ref _legacyCount);

    public string SessionStatus => _sessionStatus;

    /// <summary>FD2 报告率协商结果，便于日志核对。</summary>
    public string LastReportRateStatus => _rateStatus;

    public event Action<byte[], long>? ReportReceived;

    /// <summary>电量档位 0–9（-1 表示没读到），第二参数是是否在充电。</summary>
    public event Action<int, bool>? BatteryChanged;

    public event Action<string>? Closed;

    public bool IsOpen => _fd2 is not null;

    public long ReportCount => Interlocked.Read(ref _reportCount);

    /// <summary>扫一段时间，返回所有 Nintendo 厂商广播的候选。</summary>
    public static async Task<List<Pro2BleCandidate>> ScanAsync(TimeSpan timeout, CancellationToken token)
    {
        var found = new List<Pro2BleCandidate>();
        var seen = new HashSet<ulong>();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, args) =>
        {
            bool nintendo = false;
            foreach (BluetoothLEManufacturerData data in args.Advertisement.ManufacturerData)
            {
                if (data.CompanyId == NintendoCompanyId)
                {
                    nintendo = true;
                    break;
                }
            }

            if (!nintendo || !seen.Add(args.BluetoothAddress)) return;

            string name = args.Advertisement.LocalName ?? "";
            found.Add(new Pro2BleCandidate(args.BluetoothAddress, args.BluetoothAddressType, args.RawSignalStrengthInDBm, name));
        };

        watcher.Stopped += (_, _) => done.TrySetResult(true);
        watcher.Start();

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        timer.CancelAfter(timeout);
        using (timer.Token.Register(() => done.TrySetResult(true)))
        {
            await done.Task.ConfigureAwait(false);
        }

        try { watcher.Stop(); } catch { }
        return found;
    }

    /// <summary>
    /// 连接并跑完初始化。失败时 detail 给出可读原因。
    /// <paramref name="knownAddress"/> 为 true 表示这次是「按上次地址直连」，
    /// 不依赖手柄处于广播状态。
    /// </summary>
    public async Task<(bool Ok, string Detail)> OpenAsync(
        Pro2BleCandidate candidate,
        CancellationToken token,
        bool knownAddress = false)
    {
        try
        {
            Log?.Invoke("[BLE] open address=" + candidate.Address.ToString("X12") +
                        " source=" + (knownAddress ? "known_address" : "scan"));

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(candidate.Address, candidate.AddressType)
                .AsTask(token).ConfigureAwait(false);

            if (_device is null)
            {
                return (false, "手柄没有响应，可能被其它程序占用，或者已经休眠。");
            }

            _device.ConnectionStatusChanged += OnConnectionStatusChanged;

            // 先拿到 GATT 会话并声明「由我维持连接」，再发现服务。
            await AttachSessionAsync(_device, token).ConfigureAwait(false);

            // 刚建立链路时 GATT 还没稳定，直接发现服务容易拿到半成品。
            await Task.Delay(250, token).ConfigureAwait(false);

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                RequestThroughputMode(_device);
            }

            GattDeviceServicesResult services;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                services = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached)
                    .AsTask(deadline.Token).ConfigureAwait(false);
            }

            if (services.Status != GattCommunicationStatus.Success)
            {
                return (false, "读不到手柄的蓝牙服务：" + DescribeStatus(services.Status));
            }

            _ownedServices.AddRange(services.Services);

            foreach (GattDeviceService service in services.Services)
            {
                GattCharacteristicsResult chars;
                try
                {
                    chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                        .AsTask(token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (chars.Status != GattCommunicationStatus.Success) continue;

                foreach (GattCharacteristic characteristic in chars.Characteristics)
                {
                    if (characteristic.Uuid == CmdUuid) _cmd = characteristic;
                    else if (characteristic.Uuid == AckUuid) _ack = characteristic;
                    else if (characteristic.Uuid == Fd2Uuid) _fd2 = characteristic;
                    else if (characteristic.Uuid == LegacyUuid) _legacy = characteristic;
                    else if (characteristic.Uuid == RumbleUuid) _rumble = characteristic;
                }
            }

            if (_cmd is null || _ack is null || _fd2 is null)
            {
                return (false, "这台设备没有提供 Pro 手柄的输入通道，可能不是 Pro2。");
            }

            _ack.ValueChanged += OnAckValueChanged;
            if (!await SubscribeAsync(_ack, token).ConfigureAwait(false))
            {
                return (false, "手柄没有回应初始化，请关掉手柄再重新打开。");
            }

            foreach (byte[] command in InitCommands)
            {
                // 蓝牙上不提交配对数据（旧实现同样跳过）：这条会把手里停在配对态，
                // 之后 FD2 就不再推送，表现为「已连接但回报率 0」。
                if (command.Length > 0 && command[0] == 0x15)
                {
                    continue;
                }

                GattCommunicationStatus write = GattCommunicationStatus.Unreachable;
                TaskCompletionSource<byte[]> ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    _ackTcs = ack;
                    write = await WriteAsync(_cmd, command, token).ConfigureAwait(false);
                    if (write == GattCommunicationStatus.Success) break;

                    try { await Task.Delay(120, token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        return (false, "蓝牙连接超时，请把手柄靠近电脑后重试。");
                    }
                }

                if (write != GattCommunicationStatus.Success)
                {
                    return (false, "初始化没能完成：" + DescribeStatus(write));
                }

                // 正常 ACK 几十毫秒就回；超时继续，不阻塞整条链路。
                await Task.WhenAny(ack.Task, Task.Delay(400, token)).ConfigureAwait(false);
            }

            // 旧版的顺序不能改：先取一次电量，再写报告率，最后订阅 FD2。
            // 直读常被手柄以 ProtocolError 拒绝；这时在 FD2 还没推流之前
            // 短暂订阅 legacy 收一帧 0x09，拿到就立刻退订，不跟 FD2 抢无线电。
            if (!await ReadBatteryOnceAsync(_legacy, token).ConfigureAwait(false))
                await ReadBatteryByNotifyBeforeFd2Async(_legacy, token).ConfigureAwait(false);

            // FD2 默认不推流（或只推一帧）。必须先写它的自定义报告率
            // 描述符，把手柄切到 133Hz 周期上报。
            await ConfigureFd2ReportRateAsync(_fd2, token).ConfigureAwait(false);

            _fd2.ValueChanged += OnFd2ValueChanged;
            if (!await SubscribeAsync(_fd2, token).ConfigureAwait(false))
            {
                return (false, "无法接收手柄的输入数据。");
            }

            if (_rumble is not null)
            {
                StartRumbleWriter();
                Log?.Invoke("[BLE] rumble_ready handle=0x" + _rumble.AttributeHandle.ToString("X4"));
            }
            else
            {
                Log?.Invoke("[BLE] rumble_missing; 蓝牙将没有震动");
            }

            Log?.Invoke("[BLE] ready fd2_cccd=notify rate=" + _rateStatus +
                        " interval=" + _intervalStatus +
                        " session=" + _sessionStatus +
                        " max_pdu=" + (_session?.MaxPduSize.ToString() ?? "n/a"));

            return (true, "蓝牙");
        }
        catch (OperationCanceledException)
        {
            return (false, "蓝牙连接超时，请把手柄靠近电脑后重试。");
        }
        catch (Exception)
        {
            return (false, "蓝牙连接没有建立，请关闭手柄再重新打开。");
        }
    }

    private async Task<bool> SubscribeAsync(
        GattCharacteristic characteristic, CancellationToken token)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                GattCommunicationStatus status =
                    await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token).ConfigureAwait(false);
                Log?.Invoke("[BLE] cccd handle=0x" + characteristic.AttributeHandle.ToString("X4") +
                            " attempt=" + attempt + " status=" + status);
                if (status == GattCommunicationStatus.Success) return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log?.Invoke("[BLE] cccd_error handle=0x" + characteristic.AttributeHandle.ToString("X4") +
                            " " + ex.GetType().Name);
            }

            try { await Task.Delay(300 * attempt, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        return false;
    }

    private static async Task<GattCommunicationStatus> WriteAsync(
        GattCharacteristic characteristic, byte[] payload, CancellationToken token)
    {
        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(payload);
            return await characteristic.WriteValueAsync(writer.DetachBuffer()).AsTask(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return GattCommunicationStatus.Unreachable; }
    }

    private void OnAckValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        byte[] payload = ReadBuffer(args);
        _ackTcs?.TrySetResult(payload);
    }

    private void OnFd2ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (_closing) return;
        byte[] payload = ReadBuffer(args);
        if (payload.Length == 0) return;
        Interlocked.Increment(ref _reportCount);
        ReportReceived?.Invoke(payload, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// legacy 特征的 0x09 报告带电量档位：payload[1] 的 bit2-5 是 0–8 档，
    /// 8 就是满电（用户实测确认）。这里只解读，不参与输入。
    /// </summary>
    private void OnLegacyValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (_closing) return;

        byte[] payload = ReadBuffer(args);
        if (payload.Length < 2 || payload[0] != 0x09) return;

        byte rawPower = payload[1];
        int level = (rawPower >> 2) & 0x0F;
        bool charging = (rawPower & 0x02) != 0;
        bool external = (rawPower & 0x01) != 0;

        Interlocked.Increment(ref _legacyCount);
        if (Interlocked.Exchange(ref _legacyLogged, 1) == 0)
        {
            Log?.Invoke("[BLE] battery_notify raw=0x" + rawPower.ToString("X2") +
                        " level=" + level + "/8 charging=" + charging +
                        " external=" + external);
        }

        // 档位上限是 8；9 及以上视为不可靠读数。
        BatteryChanged?.Invoke(level <= 8 ? level : -1, charging || external);
    }

    /// <summary>
    /// 连接时先试直读；legacy 特征通常不支持 Read（ATT ProtocolError），
    /// 那就只能靠它的 0x09 通知，这里打开订阅。
    /// </summary>
    public async Task<bool> SubscribeLegacyBatteryAsync(CancellationToken token)
    {
        GattCharacteristic? legacy = _legacy;
        if (legacy is null || _closing) return false;

        legacy.ValueChanged += OnLegacyValueChanged;
        bool ok = await SubscribeAsync(legacy, token).ConfigureAwait(false);
        Log?.Invoke("[BLE] legacy_battery_subscribe=" + ok);
        if (!ok)
        {
            try { legacy.ValueChanged -= OnLegacyValueChanged; } catch { }
        }

        return ok;
    }

    /// <summary>订阅 legacy 之后如果 FD2 掉速，立刻退回，优先保住输入。</summary>
    public void DropLegacyBattery(string reason)
    {
        GattCharacteristic? legacy = _legacy;
        if (legacy is null) return;

        try { legacy.ValueChanged -= OnLegacyValueChanged; } catch { }
        Log?.Invoke("[BLE] legacy_battery_unsubscribe reason=" + reason);

        _ = Task.Run(async () =>
        {
            try
            {
                await legacy.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch
            {
                // 退订失败不影响输入，下次重连会重新来一遍。
            }
        });
    }

    /// <summary>
    /// 只在连接时读一次电量。legacy 特征大多没开 Read 权限，
    /// 读失败是常态，真正的电量走 0x09 通知。
    /// </summary>
    private async Task<bool> ReadBatteryOnceAsync(GattCharacteristic? legacy, CancellationToken token)
    {
        if (legacy is null) return false;

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            GattReadResult read = await legacy.ReadValueAsync(BluetoothCacheMode.Uncached)
                .AsTask(deadline.Token).ConfigureAwait(false);

            if (read.Status != GattCommunicationStatus.Success)
            {
                Log?.Invoke("[BLE] battery_read status=" + read.Status +
                            " fallback=legacy_notify_once");
                return false;
            }

            byte[] payload = ReadBufferValue(read.Value);
            Log?.Invoke("[BLE] battery_read len=" + payload.Length +
                        " head=" + Convert.ToHexString(payload, 0, Math.Min(4, payload.Length)));

            if (payload.Length >= 2 && payload[0] == 0x09)
            {
                byte rawPower = payload[1];
                int level = (rawPower >> 2) & 0x0F;
                bool charging = (rawPower & 0x02) != 0;
                Interlocked.Increment(ref _legacyCount);
                BatteryChanged?.Invoke(level <= 9 ? level : -1, charging);
                return true;
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            // 电量是展示项，读不到不影响输入。
            return false;
        }
        catch (Exception ex)
        {
            Log?.Invoke("[BLE] battery_read_error " + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// 直读失败时，在订阅 FD2 之前短暂订阅 legacy 收一帧 0x09 电量，
    /// 拿到（或超时）就立刻退订。FD2 这时还没开始推流，因此不会重演
    /// 「订阅 legacy 抢走 FD2 → 回报率归零」的问题。
    /// </summary>
    private async Task ReadBatteryByNotifyBeforeFd2Async(
        GattCharacteristic? legacy, CancellationToken token)
    {
        if (legacy is null || _closing) return;

        var got = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            byte[] payload = ReadBuffer(args);
            if (payload.Length < 2 || payload[0] != 0x09) return;
            OnLegacyValueChanged(sender, args);
            got.TrySetResult(true);
        }

        legacy.ValueChanged += Handler;
        bool subscribed = false;
        try
        {
            subscribed = await SubscribeAsync(legacy, token).ConfigureAwait(false);
            if (!subscribed)
            {
                Log?.Invoke("[BLE] battery_notify_once subscribe_failed");
                return;
            }

            await Task.WhenAny(got.Task, Task.Delay(1200, token)).ConfigureAwait(false);
            Log?.Invoke("[BLE] battery_notify_once got=" + got.Task.IsCompletedSuccessfully);
        }
        catch (OperationCanceledException)
        {
            // 连接被用户取消：下面的 finally 仍然会退订。
        }
        catch (Exception ex)
        {
            Log?.Invoke("[BLE] battery_notify_once_error " + ex.GetType().Name);
        }
        finally
        {
            try { legacy.ValueChanged -= Handler; } catch { }
            if (subscribed)
            {
                try
                {
                    await legacy.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None)
                        .AsTask(token).ConfigureAwait(false);
                }
                catch
                {
                    // 退订失败由后续 DropLegacyBattery 兜底，不阻断连接。
                }
            }
        }
    }

    /// <summary>
    /// 持有 GATT 会话并维持连接。缺了这一步，Windows 会在订阅完成后
    /// 松开链路，手柄的第一个 FD2 帧之后就不再推送。
    /// </summary>
    private async Task AttachSessionAsync(BluetoothLEDevice device, CancellationToken token)
    {
        try
        {
            _session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId)
                .AsTask(token).ConfigureAwait(false);
            if (_session is null)
            {
                _sessionStatus = "null";
                return;
            }

            if (_session.CanMaintainConnection)
            {
                _session.MaintainConnection = true;
            }

            _sessionStatus = _session.SessionStatus + "/maintain=" + _session.MaintainConnection;
            Log?.Invoke("[BLE] gatt_session status=" + _session.SessionStatus +
                        " can_maintain=" + _session.CanMaintainConnection +
                        " maintain=" + _session.MaintainConnection +
                        " max_pdu=" + _session.MaxPduSize);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _sessionStatus = "error_" + ex.HResult.ToString("X8");
            Log?.Invoke("[BLE] gatt_session_error " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8"));
        }
    }

    /// <summary>申请吞吐优先的连接参数（Win11 22000+ 提供该 API）。</summary>
    [SupportedOSPlatform("windows10.0.22000.0")]
    private void RequestThroughputMode(BluetoothLEDevice device)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;

        // 回报率的真正瓶颈是连接间隔，不是描述符：
        //   ThroughputOptimized = 11.25~15ms  → 实测只有 60~65Hz
        //   自定义 7.5ms（6 个 1.25ms 单位）→ 才能跑到 130Hz 级别
        // 所以先申请 7.5ms，失败再退回系统预设。
        try
        {
            var fast = new Win11CustomConnectionParameters();
            _preferenceRequest?.Dispose();
            _preferenceRequest = device.RequestPreferredConnectionParameters(fast.Parameters);
            _intervalStatus = "7p5ms_" + _preferenceRequest.Status;
            Log?.Invoke("[BLE] interval_request=7.5ms status=" + _preferenceRequest.Status +
                        " min_units=" + fast.Parameters.MinConnectionInterval +
                        " max_units=" + fast.Parameters.MaxConnectionInterval +
                        " latency=" + fast.Parameters.ConnectionLatency);
            if (_preferenceRequest.Status ==
                BluetoothLEPreferredConnectionParametersRequestStatus.Success)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("[BLE] interval_request_7p5_error " + ex.GetType().Name);
        }

        try
        {
            _preferenceRequest?.Dispose();
            _preferenceRequest = device.RequestPreferredConnectionParameters(
                BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
            _intervalStatus = "throughput_" + _preferenceRequest.Status;
            Log?.Invoke("[BLE] interval_request=throughput status=" + _preferenceRequest.Status);
        }
        catch (Exception ex)
        {
            _intervalStatus = "error";
            Log?.Invoke("[BLE] interval_request_error " + ex.GetType().Name);
        }
    }

    /// <summary>蓝牙震动特征是否可用。</summary>
    public bool IsRumbleReady => _rumble is not null && !_closing;

    public string RumbleSummary => "queued=" + Interlocked.Read(ref _rumbleQueued) +
                                   " written=" + Interlocked.Read(ref _rumbleWritten) +
                                   " refresh=" + Interlocked.Read(ref _rumbleRefreshGenerated) +
                                   " encode_fail=" + Interlocked.Read(ref _rumbleEncodeFail);

    /// <summary>
    /// VIIPER 反馈写进来的 64 字节 0x02 报告，编码成 33 字节 BLE 震动包。
    /// 只保留最新一帧（马达状态没有历史价值），写入最小间隔 7.5ms。
    /// </summary>
    public bool TryQueueRumble(ReadOnlySpan<byte> report, out string error)
    {
        error = "";
        if (_closing || _rumble is null)
        {
            error = "蓝牙震动通道未就绪";
            return false;
        }

        var packet = new byte[Pro2BleRumblePacketEncoder.BlePacketSize];
        if (!Pro2BleRumblePacketEncoder.TryEncodeRaw02(
                report,
                (byte)(Interlocked.Increment(ref _rumblePacketId) & 0x0f),
                packet,
                out bool active,
                out error,
                RumbleGain))
        {
            Interlocked.Increment(ref _rumbleEncodeFail);
            return false;
        }

        if (active)
        {
            // 新的一帧成为「当前状态」，后续由写入循环每 12 ms 重发。
            Volatile.Write(ref _heldRumble, packet);
            Volatile.Write(ref _heldRumbleTicks, Stopwatch.GetTimestamp());
        }
        else
        {
            // 静音 / 中性帧：立即结束保持，这一帧本身仍要发出去让马达停下。
            Volatile.Write(ref _heldRumble, null);
        }
        Interlocked.Exchange(ref _pendingRumble, packet);
        Interlocked.Increment(ref _rumbleQueued);
        try { _rumbleSignal.Release(); } catch (SemaphoreFullException) { }
        return true;
    }

    /// <summary>震动强度增益，由设置页推送；与有线链路用同一个值。</summary>
    private double rumbleGain = 1.0;

    public void SetRumbleGain(double gain) =>
        Volatile.Write(ref rumbleGain, double.IsFinite(gain) ? Math.Clamp(gain, 0, 3) : 1.0);

    private double RumbleGain => Volatile.Read(ref rumbleGain);

    private void StartRumbleWriter()
    {
        if (_rumbleLoop is { IsCompleted: false }) return;
        Volatile.Write(ref _heldRumble, null);
        Interlocked.Exchange(ref _pendingRumble, null);
        _rumblePace.Reset();
        Log?.Invoke("[BLE] rumble_writer mode=continuous interval_ms=" + RumbleIntervalMs +
            " hold_ms=" + RumbleHoldMs + " write=without_response");
        _rumbleLoop = Task.Run(RumbleLoopAsync);
    }

    private async Task RumbleLoopAsync()
    {
        long lastWriteTicks = 0;
        while (!_closing)
        {
            try
            {
                // 有来源帧时信号立即唤醒；没有来源帧时每 12 ms 自动醒来补一包。
                await _rumbleSignal.WaitAsync(RumbleIntervalMs).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            string kind = "source";
            byte[]? packet = Interlocked.Exchange(ref _pendingRumble, null);
            if (packet is null)
            {
                packet = BuildHoldPacket(out kind);
                if (packet is null) continue;
            }

            if (lastWriteTicks != 0)
            {
                double elapsedMs = Stopwatch.GetElapsedTime(lastWriteTicks).TotalMilliseconds;
                if (elapsedMs < 7.5)
                {
                    try { await Task.Delay(TimeSpan.FromMilliseconds(7.5 - elapsedMs)).ConfigureAwait(false); }
                    catch { return; }
                }
            }

            GattCharacteristic? characteristic = _rumble;
            if (characteristic is null) continue;

            long writeStarted = Stopwatch.GetTimestamp();
            lastWriteTicks = writeStarted;
            try
            {
                using var writer = new DataWriter();
                writer.WriteBytes(packet);

                // 键盘式「无响应写」是实时震动的关键：带响应写每帧多一个 ATT 往返，
                // 12 ms 的间隔全被吃掉，马达会一顿一顿。
                GattWriteOption option =
                    (characteristic.CharacteristicProperties & GattCharacteristicProperties.WriteWithoutResponse) != 0
                        ? GattWriteOption.WriteWithoutResponse
                        : GattWriteOption.WriteWithResponse;

                GattCommunicationStatus status =
                    await characteristic.WriteValueAsync(writer.DetachBuffer(), option)
                        .AsTask().ConfigureAwait(false);
                if (status == GattCommunicationStatus.Success)
                {
                    Interlocked.Increment(ref _rumbleWritten);
                }
            }
            catch
            {
                // 单帧写失败不致命，下一帧继续。
            }
            long completed = Stopwatch.GetTimestamp();
            string? pace = _rumblePace.ObserveWrite(writeStarted, completed, kind, 0,
                Interlocked.Read(ref _rumbleRefreshGenerated), 0,
                Volatile.Read(ref _heldRumble) != null, true);
            if (pace != null) Log?.Invoke(pace);
        }
    }

    /// <summary>
    /// 没有新的来源帧时，重发当前状态；主机超过 400 ms 不刷新强度就补一帧
    /// 零振幅并结束，避免马达一直转。与有线 Pro2WiredProtocol 行为一致。
    /// </summary>
    private byte[]? BuildHoldPacket(out string kind)
    {
        byte[]? held = Volatile.Read(ref _heldRumble);
        if (held is null)
        {
            kind = "none";
            return null;
        }

        if (Stopwatch.GetElapsedTime(Volatile.Read(ref _heldRumbleTicks)).TotalMilliseconds > RumbleHoldMs)
        {
            Volatile.Write(ref _heldRumble, null);
            kind = "timeout_stop";
            return RumbleFrameCodec.Neutralize(held);
        }

        byte[] copy = (byte[])held.Clone();
        RumbleFrameCodec.StampPacketId(copy, (byte)(Interlocked.Increment(ref _rumblePacketId) & 0x0f));
        Interlocked.Increment(ref _rumbleRefreshGenerated);
        kind = "refresh";
        return copy;
    }

    /// <summary>
    /// 连上之后如果实测帧率偏低，再申请一次 7.5ms。
    /// Windows 有时会把首次请求吃掉（刚建链时协商还没完成），
    /// 链路稳定后补一次通常就能生效。
    /// </summary>
    public void NudgeFastInterval(string reason)
    {
        BluetoothLEDevice? device = _device;
        if (device is null || _closing) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;

        Log?.Invoke("[BLE] interval_nudge reason=" + reason + " previous=" + _intervalStatus);
        RequestThroughputMode(device);
    }

    /// <summary>当前连接间隔申请结果，便于日志核对。</summary>
    public string IntervalStatus => _intervalStatus;

    private static byte[] ReadBufferValue(IBuffer buffer)
    {
        try
        {
            uint length = buffer.Length;
            if (length == 0) return Array.Empty<byte>();
            using var reader = DataReader.FromBuffer(buffer);
            var data = new byte[length];
            reader.ReadBytes(data);
            return data;
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static byte[] ReadBuffer(GattValueChangedEventArgs args)
    {
        try
        {
            uint length = args.CharacteristicValue.Length;
            if (length == 0) return Array.Empty<byte>();
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var data = new byte[length];
            reader.ReadBytes(data);
            return data;
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (_closing) return;
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            Closed?.Invoke("蓝牙连接已断开，手柄可能休眠或离开了范围。");
        }
    }

    public void Close()
    {
        _closing = true;

        try { if (_ack is not null) _ack.ValueChanged -= OnAckValueChanged; } catch { }
        try { if (_fd2 is not null) _fd2.ValueChanged -= OnFd2ValueChanged; } catch { }
        try { if (_legacy is not null) _legacy.ValueChanged -= OnLegacyValueChanged; } catch { }

        try { if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }

        foreach (GattDeviceService service in _ownedServices)
        {
            try { service.Dispose(); } catch { }
        }

        _ownedServices.Clear();

        try { _preferenceRequest?.Dispose(); } catch { }
        _preferenceRequest = null;

        try
        {
            if (_session is not null)
            {
                if (_session.CanMaintainConnection) _session.MaintainConnection = false;
                _session.Dispose();
            }
        }
        catch { }
        _session = null;

        try { _device?.Dispose(); } catch { }

        _device = null;
        _cmd = null;
        _ack = null;
        _fd2 = null;
        _legacy = null;
        _rumble = null;
        _pendingRumble = null;
        _ackTcs = null;
        _closing = false;
        Interlocked.Exchange(ref _reportCount, 0);
        Interlocked.Exchange(ref _legacyCount, 0);
        Interlocked.Exchange(ref _legacyLogged, 0);
    }

    public void Dispose() => Close();

    /// <summary>
    /// 写 FD2 的自定义报告率描述符。找不到就安静跳过，
    /// 由实测帧率决定后续表现，不影响连接。
    /// </summary>
    private async Task ConfigureFd2ReportRateAsync(
        GattCharacteristic characteristic,
        CancellationToken token)
    {
        _rateStatus = "starting";
        try
        {
            GattDescriptorsResult result =
                await characteristic.GetDescriptorsAsync(BluetoothCacheMode.Uncached)
                    .AsTask(token).ConfigureAwait(false);
            if (result.Status != GattCommunicationStatus.Success)
            {
                _rateStatus = "descriptor_" + result.Status;
                return;
            }

            List<GattDescriptor> writable = result.Descriptors
                .Where(d => d.Uuid != ClientConfigUuid)
                .OrderBy(d => d.AttributeHandle)
                .ToList();

            GattDescriptor? target =
                writable.FirstOrDefault(d => d.AttributeHandle == characteristic.AttributeHandle + 3) ??
                writable.FirstOrDefault(d => d.AttributeHandle == characteristic.AttributeHandle + 2) ??
                (writable.Count == 1 ? writable[0] : null);

            if (target is null)
            {
                _rateStatus = "no_descriptor";
                return;
            }

            using var writer = new DataWriter();
            writer.WriteUInt16(Fd2ReportRateHz);
            GattCommunicationStatus write =
                await target.WriteValueAsync(writer.DetachBuffer()).AsTask(token).ConfigureAwait(false);
            _rateStatus = write == GattCommunicationStatus.Success
                ? "written_" + Fd2ReportRateHz + "hz"
                : "write_" + write;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 报告率描述符是可选优化，失败不影响连接本身。
            _rateStatus = "error";
        }
    }

    /// <summary>把 GATT 状态翻成用户能看懂的一句话。</summary>
    private static string DescribeStatus(GattCommunicationStatus status) => status switch
    {
        GattCommunicationStatus.AccessDenied => "系统拒绝了访问，请检查蓝牙权限。",
        GattCommunicationStatus.ProtocolError => "手柄返回了异常数据。",
        GattCommunicationStatus.Unreachable => "手柄当前不可达，可能超出了范围。",
        _ => "蓝牙通道暂时不可用。"
    };
}
