using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Pro2Plus;

/// <summary>参数命令的结果：是否成功，以及直接展示给用户的文案（含日志路径）。</summary>
internal sealed record PresetCommandOutcome(bool Success, string Message);

/// <summary>
/// BLE 连接参数预设工具：命令行 --ble-turbo-* 的唯一实现。
/// 只处理两种目标——单个适配器的 Device Parameters（旧版 A 档）与 Win10 全局 BTHPORT 默认值，
/// 两者共用同一套保护：改前快照 + reg 导出 + 写后逐值校验 + 失败整体回滚 + 可选热重启。
/// 所有写入都在提权之后、备份完成之后，且不保留任何「猜设备」的分支。
/// </summary>
internal static class BleStackPresetTool
{
    public const string ApplyArgument = "--ble-turbo-apply";
    public const string RestoreArgument = "--ble-turbo-restore";
    public const string StackApplyArgument = "--ble-turbo-stack-apply";
    public const string StackRestoreArgument = "--ble-turbo-stack-restore";

    private static readonly string[] CommandNames =
        [ApplyArgument, RestoreArgument, StackApplyArgument, StackRestoreArgument];

    private static readonly string[] ParameterNames =
    [
        "ConnectionIntervalMin",
        "ConnectionIntervalMax",
        "ConnectionLatency",
        "LinkSupervisionTimeout"
    ];

    // 设备级 A 档写入的四项固定值（与 preset.Values 无关）。
    private static readonly Dictionary<string, int> DeviceProfileA = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ConnectionIntervalMin"] = 6,
        ["ConnectionIntervalMax"] = 12,
        ["ConnectionLatency"] = 0,
        ["LinkSupervisionTimeout"] = 200
    };

    private const string BluetoothClassGuid = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string UsbEnumPath = @"SYSTEM\CurrentControlSet\Enum\USB";
    private const string DriverClassPath = @"SYSTEM\CurrentControlSet\Control\Class\";
    private const string NativePrefix = @"HKEY_LOCAL_MACHINE\";
    private const string ProviderPrefix = "Registry::HKEY_LOCAL_MACHINE\\";

    public static bool IsCommand(IReadOnlyList<string> args) =>
        CommandNames.Any(name => HasArgument(args, name));

    public static PresetCommandOutcome Execute(IReadOnlyList<string> args)
    {
        try
        {
            // 先校验完整选择器，再提权、再快照：参数不合法时一个字节都不写。
            BlePresetDefinition preset = BlePresetCatalog.ParseCommand(args);
            bool experimentalConsent = BleStackProfile.ParseExperimentalConsent(args);
            RequireAdministrator();

            if (HasArgument(args, StackRestoreArgument)) return RunStackOperation(restore: true, preset, false);
            if (HasArgument(args, StackApplyArgument)) return RunStackOperation(restore: false, preset, experimentalConsent);
            return HasArgument(args, RestoreArgument) ? RestoreDeviceProfile() : ApplyDeviceProfile();
        }
        catch (Exception ex)
        {
            return new PresetCommandOutcome(false, ex.Message);
        }
    }

    private static bool HasArgument(IReadOnlyList<string> args, string name) =>
        args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

    // ---------------- Win10 全局 BTHPORT 预设 ----------------

    private static PresetCommandOutcome RunStackOperation(bool restore, BlePresetDefinition preset,
        bool experimentalConsent)
    {
        string root = StateRoot();
        Directory.CreateDirectory(root);
        string stamp = Stamp(withMilliseconds: true);
        string logPath = Path.Combine(root, (restore ? "restore-stack-" : "apply-stack-") + stamp + ".log");
        string originalPath = Path.Combine(root, "original-stack-state.json");
        string nativePath = NativePrefix + BleStackProfile.RegistrySubKey;
        IReadOnlyDictionary<string, int> targets = preset.Values;
        string[] names = targets.Keys.ToArray();
        var log = new StringBuilder();
        AppendStackHeader(log, restore, preset, nativePath, experimentalConsent);

        try
        {
            // 恢复路径不依赖当前系统栈是否被审查：更新之后仍要能退回去。
            StackParameterAssessment? assessment = restore ? null : AssessedStack(log, logPath, experimentalConsent);

            StackBackupState? original = File.Exists(originalPath) ? ReadBackup(originalPath, names) : null;
            if (restore && original == null)
                throw new InvalidOperationException("缺少首次修改前的系统栈备份；未修改任何参数。 ");
            if (original != null && !string.Equals(NormalizeRegistrySubKey(original.RegistryPath),
                    BleStackProfile.RegistrySubKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份路径不是目标 BTHPORT Parameters；未修改任何参数。 ");

            // 这四项是全局值：没有唯一 USB 适配器也不阻止备份/写入/恢复，只是不猜重启目标。
            UsbRadioInfo? radio = null;
            string restartBlocked = "";
            try
            {
                radio = FindSingleUsbRadio();
            }
            catch (Exception ex)
            {
                restartBlocked = "没有明确的单一当前 USB 蓝牙适配器，不会猜测重启设备；全局值操作后请正常重启 Windows。 " + ex.Message;
                log.AppendLine("adapter_restart_skipped=" + OneLine(ex.Message));
            }

            if (radio != null)
            {
                log.AppendLine("adapter=" + radio.Name);
                log.AppendLine("pnp_id=" + radio.PnpDeviceId);
                log.AppendLine("driver_provider=" + radio.DriverProvider);
                log.AppendLine("driver_version=" + radio.DriverVersion);
            }

            using RegistryKey key = OpenStackKey();
            Dictionary<string, RegistryValueState> before = TakeSnapshot(key, names);
            AppendSnapshot(log, "before", before, names);

            string backupReg = Path.Combine(root, "bthport-before-" + stamp + ".reg");
            ExportRegistry(nativePath, backupReg);
            var state = new StackBackupState(
                DateTimeOffset.Now.ToString("O"),
                radio ?? original?.Adapter ??
                new UsbRadioInfo("BTHPORT global defaults (no unique current USB radio)", "", "", "", ""),
                nativePath,
                before,
                backupReg);
            WriteJson(Path.Combine(root, "before-stack-" + stamp + ".json"), state);
            if (!restore && original == null) WriteJson(originalPath, state);
            log.AppendLine("original_state=" + originalPath);
            log.AppendLine("backup_reg=" + backupReg);

            WriteVerified(key, restore, targets, original, before, names, log);

            RestartOutcome restart = radio != null
                ? RestartRadio(radio.PnpDeviceId)
                : new RestartOutcome(false, restartBlocked);
            log.AppendLine("restart_success=" + restart.Success);
            log.AppendLine("restart_output=" + OneLine(restart.Output));
            WriteJson(Path.Combine(root, "last-stack-operation.json"), new
            {
                operation = restore ? "restore" : "apply",
                completedAt = DateTimeOffset.Now.ToString("O"),
                preset = restore ? "original" : preset.Id,
                restartSucceeded = restart.Success,
                restartRequired = !restart.Success,
                originalPath,
                backupReg,
                logPath,
                evidenceLevel = assessment?.EvidenceLevel.ToString() ?? "restore_original",
                stackSha256 = assessment?.StackSha256 ?? "",
                stackVersion = assessment?.StackVersion ?? "",
                experimental = assessment?.RequiresExperimentalConsent ?? false,
                parameterNamesFound = assessment?.ParameterNamesFound ?? Array.Empty<string>()
            });
            log.AppendLine("result=success");
            return new PresetCommandOutcome(true, StackSuccessMessage(restore, preset, assessment, restart, originalPath, logPath));
        }
        catch (Exception ex)
        {
            log.AppendLine("result=failed");
            log.AppendLine(ex.ToString());
            return new PresetCommandOutcome(false, ex.Message + "\n\n日志：\n" + logPath);
        }
        finally
        {
            File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
        }
    }

    private static void AppendStackHeader(StringBuilder log, bool restore, BlePresetDefinition preset,
        string nativePath, bool experimentalConsent)
    {
        log.AppendLine("=== Win10 reversible BTHPORT global BLE defaults experiment ===");
        log.AppendLine("time=" + DateTimeOffset.Now.ToString("O"));
        log.AppendLine("operation=" + (restore ? "restore" : "apply"));
        log.AppendLine("preset=" + (restore ? "original" : preset.Id));
        log.AppendLine("registry=" + nativePath);
        log.AppendLine("experimental_consent=" + experimentalConsent);
    }

    // 未审查的 Win10 系统栈只能作为「明确确认过」的实验，不能当作已证实的读取结果。
    private static StackParameterAssessment AssessedStack(StringBuilder log, string logPath, bool experimentalConsent)
    {
        StackParameterAssessment assessment = StackParameterAssessment.ReadCurrent();
        log.AppendLine("windows_build=" + assessment.WindowsBuild);
        log.AppendLine("bthport_path=" + assessment.StackPath);
        log.AppendLine("bthport_version=" + assessment.StackVersion);
        log.AppendLine("bthport_sha256=" + assessment.StackSha256);
        log.AppendLine("evidence_level=" + assessment.EvidenceLevel);
        log.AppendLine("parameter_names_found=" + string.Join(",", assessment.ParameterNamesFound));
        log.AppendLine("evidence_reason=" + OneLine(assessment.Reason));
        log.AppendLine("audited_hash_match=" + assessment.IsAudited);

        if (!assessment.CanExperiment)
            throw new InvalidOperationException(assessment.EvidenceSummary + " 未修改任何参数。\n日志：" + logPath);
        if (!assessment.CanApply(experimentalConsent))
            throw new InvalidOperationException(
                "当前 Win10 系统栈未审查；需明确确认可恢复实验（--ble-turbo-stack-experimental）后再应用。\n" +
                assessment.EvidenceSummary + "\n未修改任何参数。\n日志：" + logPath);
        return assessment;
    }

    // 写入 + 逐值校验；任何一步失败都整体回到 before，并区分「回滚成功」与「回滚也失败」。
    private static void WriteVerified(RegistryKey key, bool restore, IReadOnlyDictionary<string, int> targets,
        StackBackupState? original, IReadOnlyDictionary<string, RegistryValueState> before, string[] names,
        StringBuilder log)
    {
        try
        {
            if (restore)
            {
                WriteBack(key, original!.Values, names);
            }
            else
            {
                foreach ((string name, int value) in targets) key.SetValue(name, value, RegistryValueKind.DWord);
                key.Flush();
            }

            Dictionary<string, RegistryValueState> after = TakeSnapshot(key, names);
            AppendSnapshot(log, "after", after, names);
            foreach (string name in names)
            {
                RegistryValueState expected = restore ? original!.Values[name] : new RegistryValueState(true, targets[name]);
                if (after[name] != expected) throw new InvalidOperationException("写入后校验失败：" + name);
            }
        }
        catch (Exception operationError)
        {
            log.AppendLine("operation_error=" + OneLine(operationError.ToString()));
            try
            {
                WriteBack(key, before, names);
                Dictionary<string, RegistryValueState> rolledBack = TakeSnapshot(key, names);
                AppendSnapshot(log, "rollback", rolledBack, names);
                if (names.Any(name => rolledBack[name] != before[name]))
                    throw new InvalidOperationException("回滚后校验失败。");
                log.AppendLine("operation_rolled_back=true");
            }
            catch (Exception rollbackError)
            {
                log.AppendLine("operation_rolled_back=false");
                log.AppendLine("rollback_error=" + OneLine(rollbackError.ToString()));
                throw new AggregateException("BLE 操作和回滚均失败；保留备份，请检查日志。", operationError, rollbackError);
            }

            throw;
        }
    }

    private static string StackSuccessMessage(bool restore, BlePresetDefinition preset,
        StackParameterAssessment? assessment, RestartOutcome restart, string originalPath, string logPath) =>
        (restore
            ? "已恢复首次修改前的四项系统 BLE 默认值；原来不存在的值已移除。"
            : "已写入 Win10 系统栈实验预设：" + preset.Title + "、Latency 0、Timeout 2s。\n" +
              assessment!.EvidenceSummary + "\n写入成功不代表采用成功；真实 125Hz / 66Hz 均以连接后的 FD2 实测为准。") +
        "\n\n" + (restart.Success ? "适配器已安全重启。" : "参数已保存，但热重启未成功；请正常重启 Windows 后连接。") +
        "\n\n原始备份：\n" + originalPath + "\n\n日志：\n" + logPath;

    // ---------------- 设备级 Device Parameters（旧版 A 档） ----------------

    private static PresetCommandOutcome ApplyDeviceProfile()
    {
        string stateRoot = StateRoot();
        Directory.CreateDirectory(stateRoot);
        string stamp = Stamp(withMilliseconds: false);
        string logPath = Path.Combine(stateRoot, "apply-A-" + stamp + ".log");
        var log = new StringBuilder();
        log.AppendLine("=== PRO2 Controller Assistant / BLE Turbo Profile A ===");
        log.AppendLine("time=" + DateTimeOffset.Now.ToString("O"));

        UsbRadioInfo radio = FindSingleUsbRadio();
        string subKeyPath = @"SYSTEM\CurrentControlSet\Enum\" + radio.PnpDeviceId + @"\Device Parameters";
        string nativePath = NativePrefix + subKeyPath;
        using RegistryKey key = Registry.LocalMachine.OpenSubKey(subKeyPath, writable: true) ??
            throw new InvalidOperationException("蓝牙适配器 Device Parameters 不存在：" + nativePath);

        Dictionary<string, RegistryValueState> before = TakeSnapshot(key);
        AppendRadio(log, radio);
        AppendSnapshot(log, "before", before);

        string backupReg = Path.Combine(stateRoot, "device-parameters-before-A-" + stamp + ".reg");
        ExportRegistry(nativePath, backupReg);

        var state = new StackBackupState(DateTimeOffset.Now.ToString("O"), radio, nativePath, before, backupReg);
        WriteJson(Path.Combine(stateRoot, "before-A-" + stamp + ".json"), state);
        string originalState = Path.Combine(stateRoot, "original-state.json");
        if (!File.Exists(originalState))
        {
            WriteJson(originalState, state);
            log.AppendLine("original_state_created=" + originalState);
        }
        else
        {
            log.AppendLine("original_state_retained=" + originalState);
        }

        try
        {
            foreach ((string name, int value) in DeviceProfileA) key.SetValue(name, value, RegistryValueKind.DWord);
            key.Flush();
        }
        catch
        {
            WriteBack(key, before);
            throw;
        }

        Dictionary<string, RegistryValueState> after = TakeSnapshot(key);
        AppendSnapshot(log, "after", after);
        RequireProfileValues(after, DeviceProfileA);
        WriteJson(Path.Combine(stateRoot, "active-profile.json"), new
        {
            profile = "A",
            appliedAt = DateTimeOffset.Now.ToString("O"),
            adapter = radio,
            values = new
            {
                ConnectionIntervalMin = 6,
                ConnectionIntervalMax = 12,
                ConnectionLatency = 0,
                LinkSupervisionTimeout = 200
            }
        });

        RestartOutcome restart = RestartRadio(radio.PnpDeviceId);
        log.AppendLine("restart_success=" + restart.Success.ToString().ToLowerInvariant());
        log.AppendLine("restart_output=" + OneLine(restart.Output));
        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));

        string restartText = restart.Success
            ? "蓝牙适配器已安全重启，请重新连接手柄。"
            : "参数已写入，但适配器无法安全热重启；请正常重启 Windows 一次。";
        return new PresetCommandOutcome(true,
            "旧版适配器参数 A 已写入（不代表 125Hz 验收通过）：Min=6、Max=12、Latency=0、Timeout=200。\n\n" +
            restartText + "\n\n修改前备份：\n" + backupReg + "\n\n日志：\n" + logPath);
    }

    private static PresetCommandOutcome RestoreDeviceProfile()
    {
        string stateRoot = StateRoot();
        string originalState = Path.Combine(stateRoot, "original-state.json");
        if (!File.Exists(originalState))
        {
            throw new FileNotFoundException("找不到修改前备份，因此没有更改任何注册表值。", originalState);
        }

        StackBackupState state = ReadBackup(originalState);
        string subKeyPath = NormalizeRegistrySubKey(state.RegistryPath);
        using RegistryKey key = Registry.LocalMachine.OpenSubKey(subKeyPath, writable: true) ??
            throw new InvalidOperationException("备份对应的注册表键不存在：" + state.RegistryPath);

        string stamp = Stamp(withMilliseconds: false);
        string logPath = Path.Combine(stateRoot, "restore-" + stamp + ".log");
        var log = new StringBuilder();
        log.AppendLine("=== PRO2 Controller Assistant / BLE Turbo Restore ===");
        log.AppendLine("time=" + DateTimeOffset.Now.ToString("O"));
        AppendSnapshot(log, "before_restore", TakeSnapshot(key));
        WriteBack(key, state.Values);
        AppendSnapshot(log, "after_restore", TakeSnapshot(key));

        string activeProfile = Path.Combine(stateRoot, "active-profile.json");
        if (File.Exists(activeProfile)) File.Delete(activeProfile);

        RestartOutcome restart = RestartRadio(state.Adapter.PnpDeviceId);
        log.AppendLine("restart_success=" + restart.Success.ToString().ToLowerInvariant());
        log.AppendLine("restart_output=" + OneLine(restart.Output));
        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));

        string restartText = restart.Success
            ? "蓝牙适配器已安全重启。"
            : "原值已恢复；请正常重启 Windows 一次使恢复结果生效。";
        return new PresetCommandOutcome(true,
            "已按首次修改前的备份恢复蓝牙参数。\n\n" + restartText +
            "\n\n原始备份继续保留：\n" + originalState + "\n\n日志：\n" + logPath);
    }

    // ---------------- 提权与适配器识别 ----------------

    private static void RequireAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("此操作需要管理员权限；请从程序内点击按钮并接受 UAC。 ");
        }
    }

    // 只认当前存在的 USB 蓝牙无线（ClassGUID 匹配 + CfgMgr 能定位），拔掉的幽灵节点不参与计数。
    private static UsbRadioInfo FindSingleUsbRadio()
    {
        var radios = new List<UsbRadioInfo>();
        using RegistryKey usb = Registry.LocalMachine.OpenSubKey(UsbEnumPath) ??
            throw new InvalidOperationException("无法读取 Windows USB 设备注册表。 ");

        foreach (string hardware in usb.GetSubKeyNames().Where(name =>
                     name.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) &&
                     name.Contains("&PID_", StringComparison.OrdinalIgnoreCase)))
        {
            using RegistryKey? hardwareKey = usb.OpenSubKey(hardware);
            if (hardwareKey == null) continue;

            foreach (string instance in hardwareKey.GetSubKeyNames())
            {
                using RegistryKey? device = hardwareKey.OpenSubKey(instance);
                if (!string.Equals(Convert.ToString(device?.GetValue("ClassGUID")), BluetoothClassGuid,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string pnpId = @"USB\" + hardware + @"\" + instance;
                if (!IsPresentDeviceInstance(pnpId)) continue;

                string name = LastResourcePart(Convert.ToString(device?.GetValue("DeviceDesc")) ?? "");
                string provider = "";
                string version = "";
                string date = "";
                string driver = Convert.ToString(device?.GetValue("Driver")) ?? "";
                if (!string.IsNullOrWhiteSpace(driver))
                {
                    using RegistryKey? driverKey = Registry.LocalMachine.OpenSubKey(DriverClassPath + driver);
                    name = Convert.ToString(driverKey?.GetValue("DriverDesc")) ?? name;
                    provider = Convert.ToString(driverKey?.GetValue("ProviderName")) ?? "";
                    version = Convert.ToString(driverKey?.GetValue("DriverVersion")) ?? "";
                    date = Convert.ToString(driverKey?.GetValue("DriverDate")) ?? "";
                }

                radios.Add(new UsbRadioInfo(string.IsNullOrWhiteSpace(name) ? pnpId : name,
                    pnpId, provider, version, date));
            }
        }

        if (radios.Count == 0)
        {
            throw new InvalidOperationException("没有找到当前存在的 USB 蓝牙适配器；当前一键配置仅支持可识别的 USB 蓝牙适配器。 ");
        }

        if (radios.Count > 1)
        {
            throw new InvalidOperationException(
                "检测到多个 USB 蓝牙适配器，为避免改错设备，本次没有修改：\n" +
                string.Join("\n", radios.Select(radio => radio.Name + " = " + radio.PnpDeviceId)));
        }

        return radios[0];
    }

    private static bool IsPresentDeviceInstance(string pnpDeviceId) =>
        OperatingSystem.IsWindows() && CM_Locate_DevNode(out _, pnpDeviceId, 0) == 0;

    [DllImport("CfgMgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    private static void AppendRadio(StringBuilder log, UsbRadioInfo radio)
    {
        log.AppendLine("adapter=" + radio.Name);
        log.AppendLine("pnp_id=" + radio.PnpDeviceId);
        log.AppendLine("driver_provider=" + radio.DriverProvider);
        log.AppendLine("driver_version=" + radio.DriverVersion);
    }

    // ---------------- 注册表读写 ----------------

    private static RegistryKey OpenStackKey() =>
        Registry.LocalMachine.OpenSubKey(BleStackProfile.RegistrySubKey, writable: true) ??
        throw new InvalidOperationException("BTHPORT Parameters 不存在；未修改任何参数。 ");

    private static Dictionary<string, RegistryValueState> TakeSnapshot(RegistryKey key, IEnumerable<string>? names = null)
    {
        var presentNames = key.GetValueNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, RegistryValueState>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names ?? ParameterNames)
        {
            bool present = presentNames.Contains(name);
            // 非 DWORD 无法精确还原，宁可不改也不要写成另一种类型。
            if (present && key.GetValueKind(name) != RegistryValueKind.DWord)
                throw new InvalidDataException("现有参数不是 DWORD，不能保证精确恢复；未修改：" + name);
            values[name] = new RegistryValueState(present, present ? Convert.ToInt64(key.GetValue(name, 0)) : 0);
        }

        return values;
    }

    private static void WriteBack(RegistryKey key, IReadOnlyDictionary<string, RegistryValueState> snapshot,
        IEnumerable<string>? names = null)
    {
        foreach (string name in names ?? ParameterNames)
        {
            if (snapshot.TryGetValue(name, out RegistryValueState? saved) && saved.Present)
            {
                key.SetValue(name, checked((int)saved.Value), RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
        }

        key.Flush();
    }

    private static void RequireProfileValues(IReadOnlyDictionary<string, RegistryValueState> values,
        IReadOnlyDictionary<string, int> expected)
    {
        foreach ((string name, int expectedValue) in expected)
        {
            if (!values.TryGetValue(name, out RegistryValueState? actual) ||
                !actual.Present || actual.Value != expectedValue)
            {
                throw new InvalidOperationException("写入后校验失败：" + name);
            }
        }
    }

    private static void ExportRegistry(string nativePath, string destination)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "reg.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("export");
        startInfo.ArgumentList.Add(nativePath);
        startInfo.ArgumentList.Add(destination);
        startInfo.ArgumentList.Add("/y");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 reg.exe 导出修改前备份。 ");
        }

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(destination))
        {
            throw new InvalidOperationException(
                "注册表备份失败，因此没有写入参数。reg.exe exit=" + process.ExitCode + " " + OneLine(output));
        }
    }

    private static RestartOutcome RestartRadio(string pnpDeviceId)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/restart-device");
            startInfo.ArgumentList.Add(pnpDeviceId);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new RestartOutcome(false, "pnputil.exe start failed");
            }

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            // pnputil 有时 exit=0 但实际要求重启系统；中英文两种措辞都要当失败。
            bool deferred = output.Contains("waiting for a system restart", StringComparison.OrdinalIgnoreCase) ||
                            output.Contains("failed to restart", StringComparison.OrdinalIgnoreCase) ||
                            output.Contains("等待系统重新启动", StringComparison.OrdinalIgnoreCase) ||
                            output.Contains("重新启动设备失败", StringComparison.OrdinalIgnoreCase);
            return new RestartOutcome(process.ExitCode == 0 && !deferred, output);
        }
        catch (Exception ex)
        {
            return new RestartOutcome(false, ex.Message);
        }
    }

    // ---------------- 备份 JSON ----------------

    private static StackBackupState ReadBackup(string path, IEnumerable<string>? names = null)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        JsonElement adapterElement = GetProperty(root, "adapter");
        var adapter = new UsbRadioInfo(
            GetString(adapterElement, "name"),
            GetString(adapterElement, "pnpDeviceId"),
            GetString(adapterElement, "driverProvider"),
            GetString(adapterElement, "driverVersion"),
            GetString(adapterElement, "driverDate"));

        var values = new Dictionary<string, RegistryValueState>(StringComparer.OrdinalIgnoreCase);
        JsonElement valuesElement = GetProperty(root, "values");
        foreach (string name in names ?? ParameterNames)
        {
            JsonElement entry = GetProperty(valuesElement, name);
            bool present = GetProperty(entry, "present").GetBoolean();
            long value = present ? GetProperty(entry, "value").GetInt64() : 0;
            // 先校验完整个备份再打开可写目标键：后面一项坏掉不能只恢复一半。
            if (present) _ = checked((int)value);
            values[name] = new RegistryValueState(present, value);
        }

        return new StackBackupState(GetString(root, "capturedAt"), adapter, GetString(root, "registryPath"),
            values, GetString(root, "backupReg"));
    }

    // 备份文件由本工具写出（camelCase），但读取时按大小写不敏感匹配，容忍手工修改。
    private static JsonElement GetProperty(JsonElement element, string name)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }

        throw new InvalidDataException("备份 JSON 缺少字段：" + name);
    }

    private static string GetString(JsonElement element, string name)
    {
        JsonElement value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.Null ? "" : value.GetString() ?? "";
    }

    private static string NormalizeRegistrySubKey(string value)
    {
        if (value.StartsWith(ProviderPrefix, StringComparison.OrdinalIgnoreCase))
            return value[ProviderPrefix.Length..];
        if (value.StartsWith(NativePrefix, StringComparison.OrdinalIgnoreCase))
            return value[NativePrefix.Length..];
        return value.TrimStart('\\');
    }

    private static void AppendSnapshot(StringBuilder builder, string prefix,
        IReadOnlyDictionary<string, RegistryValueState> snapshot, IEnumerable<string>? names = null)
    {
        foreach (string name in names ?? ParameterNames)
        {
            RegistryValueState entry = snapshot[name];
            builder.AppendLine(prefix + "." + name + "=" + (entry.Present ? entry.Value.ToString() : "absent"));
        }
    }

    private static string StateRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Pro2Plus", "BLE-Turbo");

    private static void WriteJson<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }), new UTF8Encoding(false));

    private static string Stamp(bool withMilliseconds) =>
        DateTime.Now.ToString(withMilliseconds ? "yyyyMMdd-HHmmss-fff" : "yyyyMMdd-HHmmss");

    private static string LastResourcePart(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Split(';')[^1];

    private static string OneLine(string value) =>
        (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    // 属性名必须与既有备份文件一致：JSON 用 camelCase 落盘，改名会让旧备份读不出来。
    private sealed record RegistryValueState(bool Present, long Value);

    private sealed record RestartOutcome(bool Success, string Output);

    private sealed record UsbRadioInfo(
        string Name,
        string PnpDeviceId,
        string DriverProvider,
        string DriverVersion,
        string DriverDate);

    private sealed record StackBackupState(
        string CapturedAt,
        UsbRadioInfo Adapter,
        string RegistryPath,
        Dictionary<string, RegistryValueState> Values,
        string BackupReg);
}
