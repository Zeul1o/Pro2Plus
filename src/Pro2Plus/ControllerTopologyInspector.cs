using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Pro2Plus;

/// <summary>一次 USBIP 单挂载修复的结果：过程行、摘除数、重挂数、最终端口数。</summary>
public sealed record UsbipAttachRepairOutcome(
    IReadOnlyList<string> Lines,
    int DetachedPortCount,
    int AttachCount,
    int FinalPortCount);

/// <summary>
/// 控制器拓扑检查：列出 VIIPER 总线与 USBIP 端口、清理残留虚拟设备、把单个虚拟设备
/// 收敛到「恰好一个 USBIP 挂载」。所有输出行前缀（[CLEANUP] / [USBIP_PORT] 等）与
/// 命令行参数都是外部排障依赖的契约，必须逐字稳定。
/// </summary>
public static class ControllerTopologyInspector
{
    private static readonly Regex PortLinePattern =
        new(@"^\s*Port\s+(\d+)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VidPidPattern =
        new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MiPattern =
        new(@"MI_([0-9A-F]{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CollectionPattern =
        new(@"COL(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // usbip port 里出现任一 token 就视为「我们关心的设备」。
    private static readonly string[] DeviceTokens =
    [
        "054c", "0ce6", "0df2", "057e", "2069", "2009", "045e", "028e",
        "dualsense", "wireless controller", "nintendo", "xbox", "if_hid"
    ];

    public static async Task<IReadOnlyList<string>> CaptureSnapshotAsync(
        OutputBridgeClient client,
        string sessionLogPath,
        CancellationToken cancellationToken)
    {
        var report = new List<string>
        {
            "[DIAG] kind=controller_enumeration version=pro2-controller-assistant-v1.1.0 session_log=\"" + sessionLogPath + "\""
        };
        await AppendBridgeInventoryAsync(client, report, cancellationToken);
        await AppendPortListingAsync(report, cancellationToken);
        await AppendPnpInventoryAsync(report, cancellationToken);
        return report;
    }

    public static async Task<IReadOnlyList<string>> PurgeStaleVirtualDevicesAsync(
        OutputBridgeClient client,
        CancellationToken cancellationToken,
        bool includePnpDump = true)
    {
        var report = new List<string>
        {
            "[CLEANUP] begin scope=local_viiper_buses_and_matching_usbip_ports order=usbip_detach_then_viiper_remove"
        };

        // 必须先让 Windows 看到 USB 拔出，再销毁服务端设备：
        // 反序会让拔出变成 HID 读失败，可能给长时间运行的 Steam 留下失效的 SDL 槽位。
        await DetachMatchedPortsAsync(report, cancellationToken);
        await Task.Delay(250, cancellationToken);

        await PurgeBridgeBusesAsync(client, report, cancellationToken);

        await AppendPortListingAsync(report, cancellationToken);
        if (includePnpDump)
        {
            await AppendPnpInventoryAsync(report, cancellationToken);
        }
        else
        {
            report.Add("[PNP_DUMP] skipped=automatic_virtual_device_guard");
        }

        report.Add("[CLEANUP] complete");
        return report;
    }

    private static async Task PurgeBridgeBusesAsync(
        OutputBridgeClient client,
        List<string> report,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<uint> buses = await client.BusListAsync(cancellationToken);
            report.Add("[CLEANUP_VIIPER] buses_before=" + string.Join(",", buses));
            foreach (uint bus in buses)
            {
                try
                {
                    IReadOnlyList<BridgeDeviceInfo> devices =
                        await client.BusDevicesAsync(bus, cancellationToken);
                    foreach (BridgeDeviceInfo device in devices)
                    {
                        report.Add("[CLEANUP_VIIPER] remove_device bus=" + bus +
                                   " dev=" + device.DevId +
                                   " type=" + device.Type +
                                   " vid=" + device.Vid +
                                   " pid=" + device.Pid);
                        if (!string.IsNullOrWhiteSpace(device.DevId))
                        {
                            await client.RemoveDeviceAsync(bus, device.DevId, cancellationToken);
                        }
                    }

                    await client.RemoveBusAsync(bus, cancellationToken);
                    report.Add("[CLEANUP_VIIPER] removed_bus=" + bus);
                }
                catch (Exception ex)
                {
                    report.Add("[CLEANUP_VIIPER] bus=" + bus +
                               " warning=" + CollapseLine(ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            report.Add("[CLEANUP_VIIPER] skipped=" + CollapseLine(ex.Message));
        }
    }

    public static async Task<IReadOnlyList<string>> DetachOrderlyAsync(
        BridgeDeviceInfo device,
        string host,
        int apiPort,
        CancellationToken cancellationToken)
    {
        var report = new List<string>();
        if (!IsLocalHost(host))
        {
            report.Add("[USBIP_ORDERLY_DETACH] skipped=remote_host host=" + CollapseLine(host));
            return report;
        }

        UsbipRuntime? runtime = UsbipRuntimeLocator.Find();
        if (runtime == null)
        {
            report.Add("[USBIP_ORDERLY_DETACH] skipped=usbip.exe_not_found");
            return report;
        }

        int serverPort = UsbServerPortOf(apiPort);
        CommandOutcome listing = await ExecuteAsync(
            runtime.ExePath,
            ["port"],
            runtime.DirectoryPath,
            TimeSpan.FromSeconds(6),
            cancellationToken);
        if (listing.ExitCode != 0)
        {
            report.Add("[USBIP_ORDERLY_DETACH] skipped=port_dump_failed exit=" +
                       listing.ExitCode + " output=" + CollapseLine(listing.CombinedOutput));
            return report;
        }

        int[] attached = ToUniqueArray(
            LocatePortsForRemote(listing.CombinedOutput, serverPort, device.BusId, device.DevId));
        if (attached.Length == 0)
        {
            report.Add("[USBIP_ORDERLY_DETACH] state=already_absent" +
                       " usb_server_port=" + serverPort +
                       " remote_bus_dev=" + device.BusId + "/" + device.DevId);
            return report;
        }

        foreach (int port in attached)
        {
            CommandOutcome detach = await ExecuteAsync(
                runtime.ExePath,
                ["detach", "-p", port.ToString()],
                runtime.DirectoryPath,
                TimeSpan.FromSeconds(8),
                cancellationToken);
            report.Add("[USBIP_ORDERLY_DETACH] port=" + port +
                       " usb_server_port=" + serverPort +
                       " remote_bus_dev=" + device.BusId + "/" + device.DevId +
                       " exit=" + detach.ExitCode +
                       " output=" + CollapseLine(detach.CombinedOutput));
        }

        bool gone = await AwaitRemoteGoneAsync(
            runtime,
            serverPort,
            device.BusId,
            device.DevId,
            cancellationToken);
        report.Add("[USBIP_ORDERLY_DETACH] confirmed_absent=" + (gone ? "true" : "false") +
                   " remote_bus_dev=" + device.BusId + "/" + device.DevId +
                   " policy=detach_client_before_remove_server_device");
        return report;
    }

    public static async Task<UsbipAttachRepairOutcome> EnsureSingleAttachmentAsync(
        BridgeDeviceInfo device,
        string host,
        int apiPort,
        CancellationToken cancellationToken)
    {
        var report = new List<string>();
        if (!IsLocalHost(host))
        {
            report.Add("[USBIP_SINGLETON] skipped=remote_host host=" + CollapseLine(host));
            return new UsbipAttachRepairOutcome(report, 0, 0, -1);
        }

        UsbipRuntime? runtime = UsbipRuntimeLocator.Find();
        if (runtime == null)
        {
            report.Add("[USBIP_SINGLETON] failed=usbip.exe_not_found");
            return new UsbipAttachRepairOutcome(report, 0, 0, 0);
        }

        int serverPort = UsbServerPortOf(apiPort);
        CommandOutcome listing = await ExecuteAsync(
            runtime.ExePath,
            ["port"],
            runtime.DirectoryPath,
            TimeSpan.FromSeconds(6),
            cancellationToken);
        if (listing.ExitCode != 0)
        {
            report.Add("[USBIP_SINGLETON] failed=port_dump exit=" + listing.ExitCode +
                       " output=" + CollapseLine(listing.CombinedOutput));
            return new UsbipAttachRepairOutcome(report, 0, 0, 0);
        }

        int[] attached = ToSortedUniqueArray(
            LocatePortsForRemote(listing.CombinedOutput, serverPort, device.BusId, device.DevId));
        if (attached.Length == 1)
        {
            report.Add("[USBIP_SINGLETON] state=healthy port=" + attached[0] +
                       " remote_bus_dev=" + device.BusId + "/" + device.DevId);
            return new UsbipAttachRepairOutcome(report, 0, 0, 1);
        }

        report.Add("[USBIP_SINGLETON] repair_required observed_ports=" +
                   (attached.Length == 0 ? "none" : string.Join(",", attached)) +
                   " remote_bus_dev=" + device.BusId + "/" + device.DevId);

        int detached = 0;
        foreach (int port in ChoosePortsToDetach(attached))
        {
            CommandOutcome detach = await ExecuteAsync(
                runtime.ExePath,
                ["detach", "-p", port.ToString()],
                runtime.DirectoryPath,
                TimeSpan.FromSeconds(8),
                cancellationToken);
            report.Add("[USBIP_SINGLETON] detach_port=" + port +
                       " exit=" + detach.ExitCode +
                       " output=" + CollapseLine(detach.CombinedOutput));
            if (detach.ExitCode == 0)
            {
                detached++;
            }
        }

        if (attached.Length > 0)
        {
            await AwaitRemoteGoneAsync(
                runtime,
                serverPort,
                device.BusId,
                device.DevId,
                cancellationToken);
        }

        CommandOutcome attach = await ExecuteAsync(
            runtime.ExePath,
            [
                "--tcp-port", serverPort.ToString(),
                "attach", "-r", "localhost",
                "-b", device.BusId + "-" + device.DevId
            ],
            runtime.DirectoryPath,
            TimeSpan.FromSeconds(12),
            cancellationToken);
        report.Add("[USBIP_SINGLETON] reattach exit=" + attach.ExitCode +
                   " output=" + CollapseLine(attach.CombinedOutput));

        int finalCount = await AwaitPortCountAsync(
            runtime,
            serverPort,
            device.BusId,
            device.DevId,
            expectedCount: 1,
            cancellationToken);
        report.Add("[USBIP_SINGLETON] final_port_count=" + finalCount +
                   " result=" + (finalCount == 1 ? "ok" : "failed") +
                   " policy=exactly_one_attachment_per_viiper_device");
        return new UsbipAttachRepairOutcome(
            report,
            detached,
            attach.ExitCode == 0 ? 1 : 0,
            finalCount);
    }

    private static async Task AppendBridgeInventoryAsync(
        OutputBridgeClient client,
        List<string> report,
        CancellationToken cancellationToken)
    {
        try
        {
            string ping = await client.PingAsync(cancellationToken);
            report.Add("[VIIPER_DUMP] ping=" + CollapseLine(ping));
            IReadOnlyList<uint> buses = await client.BusListAsync(cancellationToken);
            report.Add("[VIIPER_DUMP] buses=" + string.Join(",", buses));
            foreach (uint bus in buses)
            {
                IReadOnlyList<BridgeDeviceInfo> devices =
                    await client.BusDevicesAsync(bus, cancellationToken);
                if (devices.Count == 0)
                {
                    report.Add("[VIIPER_DUMP] bus=" + bus + " devices=0");
                    continue;
                }

                foreach (BridgeDeviceInfo device in devices)
                {
                    report.Add("[VIIPER_DUMP] bus=" + bus +
                               " dev=" + device.DevId +
                               " type=" + device.Type +
                               " vid=" + device.Vid +
                               " pid=" + device.Pid +
                               " classification=" + DescribeBridgeDevice(device));
                }
            }
        }
        catch (Exception ex)
        {
            report.Add("[VIIPER_DUMP] unavailable=" + CollapseLine(ex.Message));
        }
    }

    private static async Task AppendPortListingAsync(
        List<string> report,
        CancellationToken cancellationToken)
    {
        UsbipRuntime? runtime = UsbipRuntimeLocator.Find();
        if (runtime == null)
        {
            report.Add("[USBIP_PORT] unavailable=usbip.exe_not_found");
            return;
        }

        CommandOutcome listing = await ExecuteAsync(
            runtime.ExePath,
            ["port"],
            runtime.DirectoryPath,
            TimeSpan.FromSeconds(6),
            cancellationToken);
        report.Add("[USBIP_PORT] exe=\"" + runtime.ExePath + "\" exit=" + listing.ExitCode);
        foreach (string line in BreakLines(listing.CombinedOutput))
        {
            report.Add("[USBIP_PORT] " + line);
        }
    }

    private static async Task DetachMatchedPortsAsync(
        List<string> report,
        CancellationToken cancellationToken)
    {
        UsbipRuntime? runtime = UsbipRuntimeLocator.Find();
        if (runtime == null)
        {
            report.Add("[USBIP_DETACH] skipped=usbip.exe_not_found");
            return;
        }

        CommandOutcome listing = await ExecuteAsync(
            runtime.ExePath,
            ["port"],
            runtime.DirectoryPath,
            TimeSpan.FromSeconds(6),
            cancellationToken);
        if (listing.ExitCode != 0)
        {
            report.Add("[USBIP_DETACH] skipped=port_dump_failed exit=" +
                       listing.ExitCode + " output=" + CollapseLine(listing.CombinedOutput));
            return;
        }

        int[] matched = EnumerateTargetPorts(listing.CombinedOutput);
        foreach (int port in matched)
        {
            CommandOutcome detach = await ExecuteAsync(
                runtime.ExePath,
                ["detach", "-p", port.ToString()],
                runtime.DirectoryPath,
                TimeSpan.FromSeconds(8),
                cancellationToken);
            report.Add("[USBIP_DETACH] port=" + port +
                       " exit=" + detach.ExitCode +
                       " output=" + CollapseLine(detach.CombinedOutput));
        }

        if (matched.Length > 0)
        {
            await Task.Delay(250, cancellationToken);
        }
    }

    private static async Task AppendPnpInventoryAsync(
        List<string> report,
        CancellationToken cancellationToken)
    {
        string script = """
$patterns = @('VID_054C&PID_0CE6','VID_054C&PID_0DF2','VID_057E&PID_2069','VID_045E&PID_028E','If_Hid','Wireless Controller','Nintendo','XBOX')
$items = Get-CimInstance Win32_PnPEntity | Where-Object {
    $text = (@($_.Name,$_.PNPDeviceID,$_.Manufacturer,$_.PNPClass,$_.Service,$_.HardwareID,$_.CompatibleID) -join ' ')
    foreach ($p in $patterns) {
        if ($text -match [regex]::Escape($p)) { return $true }
    }
    return $false
} | Select-Object Name,PNPDeviceID,PNPClass,Service,Manufacturer,Status,HardwareID,CompatibleID
$items | ConvertTo-Json -Depth 5 -Compress
""";

        CommandOutcome run = await ExecuteAsync(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script],
            AppContext.BaseDirectory,
            TimeSpan.FromSeconds(12),
            cancellationToken);
        if (run.ExitCode != 0)
        {
            report.Add("[PNP_DUMP] unavailable exit=" + run.ExitCode +
                       " output=" + CollapseLine(run.CombinedOutput));
            return;
        }

        string json = run.Stdout.Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            report.Add("[PNP_SUMMARY] matched=0");
            return;
        }

        int matched = 0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entity in root.EnumerateArray())
                {
                    matched++;
                    EmitPnpEntity(entity, report);
                }
            }
            else
            {
                matched++;
                EmitPnpEntity(root, report);
            }

            report.Add("[PNP_SUMMARY] matched=" + matched +
                       " note=\"If_Hid entries are classified by interface/usage; they are not removed automatically.\"");
        }
        catch (Exception ex)
        {
            report.Add("[PNP_DUMP] parse_failed=" + CollapseLine(ex.Message) +
                       " raw=" + CollapseLine(json));
        }
    }

    private static void EmitPnpEntity(JsonElement entity, List<string> report)
    {
        string name = ReadJsonField(entity, "Name");
        string instance = ReadJsonField(entity, "PNPDeviceID");
        string pnpClass = ReadJsonField(entity, "PNPClass");
        string service = ReadJsonField(entity, "Service");
        string manufacturer = ReadJsonField(entity, "Manufacturer");
        string status = ReadJsonField(entity, "Status");
        string hardwareIds = ReadJsonField(entity, "HardwareID");
        string compatibleIds = ReadJsonField(entity, "CompatibleID");
        string combined = name + " " + instance + " " + hardwareIds + " " + compatibleIds;
        report.Add("[PNP_HID] name=\"" + CollapseLine(name) + "\"" +
                   " instance=\"" + CollapseLine(instance) + "\"" +
                   " vid_pid=" + FormatVidPid(combined) +
                   " mi=" + FirstGroup(MiPattern, combined, "none") +
                   " collection=" + FirstGroup(CollectionPattern, combined, "none") +
                   " class=\"" + CollapseLine(pnpClass) + "\"" +
                   " service=\"" + CollapseLine(service) + "\"" +
                   " manufacturer=\"" + CollapseLine(manufacturer) + "\"" +
                   " status=\"" + CollapseLine(status) + "\"" +
                   " usage=\"" + GuessHidUsage(combined) + "\"" +
                   " classification=\"" + DescribePnpEntity(combined, pnpClass, service) + "\"" +
                   " hardware_ids=\"" + CollapseLine(hardwareIds) + "\"" +
                   " compatible_ids=\"" + CollapseLine(compatibleIds) + "\"");
    }

    // ── 解析与判定（纯函数，供差分自检直接调用）────────────────────────────

    public static IReadOnlyList<int> LocatePortsForRemote(
        string output,
        int usbServerPort,
        uint busId,
        string devId)
    {
        string endpoint = ":" + usbServerPort + "/" + busId + "-" + devId;
        string prefix = ":" + usbServerPort + "/";
        var remotePattern = new Regex(
            @"remote\s+bus/dev\s+0*" + busId + @"/0*" + Regex.Escape(devId) + @"(?:\D|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var ports = new List<int>();
        foreach (PortSection section in SlicePortBlocks(output))
        {
            bool byEndpoint = section.Text.IndexOf(endpoint, StringComparison.OrdinalIgnoreCase) >= 0;
            bool byRemote = section.Text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            remotePattern.IsMatch(section.Text);
            if (byEndpoint || byRemote)
            {
                ports.Add(section.Port);
            }
        }

        return ports;
    }

    public static IReadOnlyList<int> ChoosePortsToDetach(IReadOnlyList<int> ports)
    {
        // 先归并重复端口再判断：0 个（无挂载）或恰好 1 个（健康）都不需要摘除。
        var ordered = new SortedSet<int>();
        for (int i = 0; i < ports.Count; i++)
        {
            ordered.Add(ports[i]);
        }

        if (ordered.Count <= 1)
        {
            return Array.Empty<int>();
        }

        var result = new int[ordered.Count];
        ordered.CopyTo(result);
        return result;
    }

    private static int[] EnumerateTargetPorts(string output)
    {
        var ports = new HashSet<int>();
        foreach (PortSection section in SlicePortBlocks(output))
        {
            for (int i = 0; i < DeviceTokens.Length; i++)
            {
                if (section.Text.IndexOf(DeviceTokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ports.Add(section.Port);
                    break;
                }
            }
        }

        var result = new int[ports.Count];
        ports.CopyTo(result);
        return result;
    }

    private static IReadOnlyList<PortSection> SlicePortBlocks(string output)
    {
        var sections = new List<PortSection>();
        int openPort = -1;
        var body = new StringBuilder();

        foreach (string line in BreakLines(output))
        {
            Match head = PortLinePattern.Match(line);
            if (head.Success)
            {
                if (openPort >= 0)
                {
                    sections.Add(new PortSection(openPort, body.ToString()));
                }

                openPort = int.Parse(head.Groups[1].Value);
                body.Clear();
            }

            body.AppendLine(line);
        }

        if (openPort >= 0)
        {
            sections.Add(new PortSection(openPort, body.ToString()));
        }

        return sections;
    }

    private static async Task<bool> AwaitRemoteGoneAsync(
        UsbipRuntime runtime,
        int serverPort,
        uint busId,
        string devId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            CommandOutcome listing = await ExecuteAsync(
                runtime.ExePath,
                ["port"],
                runtime.DirectoryPath,
                TimeSpan.FromSeconds(3),
                cancellationToken);
            if (listing.ExitCode == 0 &&
                LocatePortsForRemote(listing.CombinedOutput, serverPort, busId, devId).Count == 0)
            {
                return true;
            }

            await Task.Delay(125, cancellationToken);
        }

        return false;
    }

    private static async Task<int> AwaitPortCountAsync(
        UsbipRuntime runtime,
        int serverPort,
        uint busId,
        string devId,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        int observed = 0;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            CommandOutcome listing = await ExecuteAsync(
                runtime.ExePath,
                ["port"],
                runtime.DirectoryPath,
                TimeSpan.FromSeconds(3),
                cancellationToken);
            if (listing.ExitCode == 0)
            {
                observed = ToUniqueArray(
                    LocatePortsForRemote(listing.CombinedOutput, serverPort, busId, devId)).Length;
                if (observed == expectedCount)
                {
                    return observed;
                }
            }

            await Task.Delay(125, cancellationToken);
        }

        return observed;
    }

    private static int UsbServerPortOf(int apiPort)
    {
        if (apiPort == 3242)
        {
            return 3241;
        }

        return apiPort == 1 ? 2 : apiPort - 1;
    }

    private static bool IsLocalHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    internal static string DescribeBridgeDevice(BridgeDeviceInfo device)
    {
        string type = device.Type.ToLowerInvariant();
        string identity = (device.Vid + " " + device.Pid).ToLowerInvariant();

        if (HasAny(type, "dualsenseedge") || HasAny(identity, "0xdf2"))
        {
            return "ps5_edge_virtual_dualsenseedge_l4_r4";
        }

        if (HasAny(type, "dualsensehaptic") || HasAny(identity, "0xce6"))
        {
            return "ps5_virtual_dualsensehaptic_hd_audio";
        }

        if (HasAny(type, "ns2pro") || HasAny(identity, "2069"))
        {
            return "nintendo_pro2_virtual";
        }

        if (string.Equals(type, "nspro", StringComparison.Ordinal) || HasAny(identity, "2009"))
        {
            return "nintendo_pro1_virtual";
        }

        if (HasAny(type, "xbox") || HasAny(identity, "028e"))
        {
            return "xinput_virtual";
        }

        return "unknown_viiper_device";
    }

    internal static string DescribePnpEntity(string combined, string pnpClass, string service)
    {
        if (HasAny(combined, "VID_057E") && HasAny(combined, "PID_2009"))
        {
            return "nintendo_pro1_virtual_or_physical";
        }

        if (HasAny(combined, "PID_0DF2"))
        {
            return "ps5_edge_virtual_or_physical";
        }

        if (HasAny(combined, "PID_0CE6"))
        {
            return "ps5_dualsense_virtual_or_physical";
        }

        if (HasAny(combined, "PID_2069"))
        {
            return HasAny(combined, "If_Hid")
                ? "nintendo_interface_if_hid_descriptor_name"
                : "nintendo_pro_controller_interface";
        }

        if (HasAny(combined, "PID_028E"))
        {
            return "xinput_compat_interface";
        }

        if (HasAny(combined, "If_Hid"))
        {
            return "if_hid_needs_descriptor_usage_review";
        }

        if (string.Equals(pnpClass, "HIDClass", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service, "HidUsb", StringComparison.OrdinalIgnoreCase))
        {
            return "hid_interface";
        }

        return "matched_non_hid_or_parent";
    }

    internal static string GuessHidUsage(string text)
    {
        if (HasAny(text, "HID_DEVICE_SYSTEM_GAME") || HasAny(text, "_U:0005"))
        {
            return "game_controller";
        }

        if (HasAny(text, "_UP:0001"))
        {
            return "generic_desktop";
        }

        return "unknown_from_pnp";
    }

    internal static string FormatVidPid(string text)
    {
        Match hit = VidPidPattern.Match(text);
        if (!hit.Success)
        {
            return "none";
        }

        return hit.Groups[1].Value.ToUpperInvariant() + ":" + hit.Groups[2].Value.ToUpperInvariant();
    }

    private static string FirstGroup(Regex pattern, string text, string fallback)
    {
        Match hit = pattern.Match(text);
        return hit.Success ? hit.Groups[1].Value.ToUpperInvariant() : fallback;
    }

    private static string ReadJsonField(JsonElement entity, string propertyName)
    {
        if (!entity.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null ||
            value.ValueKind == JsonValueKind.Undefined)
        {
            return "";
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return FlattenJsonValue(value);
        }

        var joined = new StringBuilder();
        foreach (JsonElement part in value.EnumerateArray())
        {
            if (joined.Length > 0)
            {
                joined.Append(';');
            }

            joined.Append(FlattenJsonValue(part));
        }

        return joined.ToString();
    }

    private static string FlattenJsonValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "";
        }

        return value.ToString();
    }

    internal static string[] BreakLines(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Array.Empty<string>();
        }

        string[] raw = value.Replace("\r", "").Split('\n');
        var kept = new List<string>(raw.Length);
        foreach (string line in raw)
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                kept.Add(trimmed);
            }
        }

        return kept.ToArray();
    }

    internal static string CollapseLine(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
    }

    private static bool HasAny(string text, string token) =>
        text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

    private static int[] ToUniqueArray(IReadOnlyList<int> ports)
    {
        var seen = new HashSet<int>();
        var kept = new List<int>(ports.Count);
        foreach (int port in ports)
        {
            if (seen.Add(port))
            {
                kept.Add(port);
            }
        }

        return kept.ToArray();
    }

    private static int[] ToSortedUniqueArray(IReadOnlyList<int> ports)
    {
        int[] unique = ToUniqueArray(ports);
        Array.Sort(unique);
        return unique;
    }

    private static async Task<CommandOutcome> ExecuteAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? AppContext.BaseDirectory
                : workingDirectory
        };
        for (int i = 0; i < args.Count; i++)
        {
            startInfo.ArgumentList.Add(args[i]);
        }

        using Process? process = Process.Start(startInfo);
        if (process == null)
        {
            return new CommandOutcome(-1, "", "Process.Start returned null");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watchdog.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(watchdog.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return new CommandOutcome(
                -2,
                "",
                "timeout after " + timeout.TotalSeconds.ToString("F0") + "s");
        }

        return new CommandOutcome(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed record CommandOutcome(int ExitCode, string Stdout, string Stderr)
    {
        public string CombinedOutput => (Stdout + "\n" + Stderr).Trim();
    }

    private readonly record struct PortSection(int Port, string Text);
}
