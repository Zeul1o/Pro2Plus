using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Pro2Plus;

/// <summary>一次适配器热重启的结果。字段名会落进 JSON，改名等于改动落盘格式。</summary>
public sealed record AdapterRestartOutcome(string OperationId, string PnpId, string InterfaceId,
    DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, bool RestartAttempted, int? NativeExitCode,
    bool PresentStarted, bool ParametersUnchanged, bool DriverUnchanged, string Message, string NativeOutput)
{
    public bool Verified => RestartAttempted && NativeExitCode.HasValue &&
        RadioRestartPolicy.IsVerifiedSuccess(NativeExitCode.Value, PresentStarted, ParametersUnchanged, DriverUnchanged);
    public bool RebootRequired => RestartAttempted && NativeExitCode == 3010;
    public string Telemetry => "[BLUETOOTH_RESTART_RESULT] operation=" + OperationId + " pnp_id=\"" + PnpId +
        "\" attempted=" + RestartAttempted + " exit=" + NativeExitCode + " verified=" + Verified +
        " present_started=" + PresentStarted + " parameters_unchanged=" + ParametersUnchanged +
        " driver_unchanged=" + DriverUnchanged + " reboot_required=" + RebootRequired +
        " driver_replacement=false registry_writes=false pairing_delete=false automatic_reboot=false";
}

/// <summary>
/// 以独立进程（pnputil）重启单个蓝牙适配器。子进程模式由本类的命令行参数进入，
/// 结果写成 <c>%ProgramData%\Pro2Plus\BluetoothRestart\&lt;操作编号&gt;.json</c> 供主进程读取。
/// 只做设备热重启：不换驱动、不写注册表参数、不删除配对、不自动重启系统。
/// </summary>
public static class AdapterRestartCommand
{
    public const string Command = "--bluetooth-adapter-restart";

    private const int ExitInvalidArguments = 7;
    private const int ExitNotElevated = 5;
    private const int ExitNotVerified = 1;
    private const int ExitRebootRequired = 2;
    private const int MaximumNativeOutputChars = 16000;
    private const int MaximumResultBytes = 65536;
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(80);
    private static readonly TimeSpan PresencePollInterval = TimeSpan.FromMilliseconds(500);
    private const int PresencePolls = 20;
    private static readonly Guid BluetoothClassGuid = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");

    public static bool IsCommand(IReadOnlyList<string> args) => args.Contains(Command, StringComparer.OrdinalIgnoreCase);

    public static string ResultPath(string operation)
    {
        if (!Guid.TryParseExact(operation, "N", out _)) throw new ArgumentException("无效的蓝牙重启操作编号。");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Pro2Plus", "BluetoothRestart", operation + ".json");
    }

    /// <summary>子进程要用的参数。先做一次校验：编号必须是 Guid，目标必须能生成重启参数。</summary>
    public static string[] HelperArguments(RadioRestartTarget target, string operation)
    {
        _ = ResultPath(operation);
        _ = RadioRestartPolicy.RestartArguments(target);
        return [Command, "--operation-id", operation, "--adapter-pnp", target.PnpId,
            "--adapter-interface", target.InterfaceId];
    }

    public static AdapterRestartOutcome? ReadResult(string operation, RadioRestartTarget target, DateTimeOffset requestedAt)
    {
        string path = ResultPath(operation);
        if (!File.Exists(path) || new FileInfo(path).Length > MaximumResultBytes) return null;
        AdapterRestartOutcome? outcome = JsonSerializer.Deserialize<AdapterRestartOutcome>(File.ReadAllText(path));
        return outcome != null && MatchesRequest(outcome, operation, target, requestedAt, DateTimeOffset.UtcNow)
            ? outcome
            : null;
    }

    public static bool MatchesRequest(AdapterRestartOutcome outcome, string operation, RadioRestartTarget target,
        DateTimeOffset requestedAt, DateTimeOffset now) => outcome.OperationId == operation &&
            outcome.PnpId.Equals(target.PnpId, StringComparison.OrdinalIgnoreCase) &&
            outcome.InterfaceId.Equals(target.InterfaceId, StringComparison.OrdinalIgnoreCase) &&
            outcome.StartedUtc >= requestedAt.AddSeconds(-2) && outcome.CompletedUtc >= outcome.StartedUtc &&
            outcome.CompletedUtc <= now.AddSeconds(5);

    /// <summary>子进程入口：参数严格为 7 项，顺序固定。</summary>
    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (args.Length != 7 || args[0] != Command || args[1] != "--operation-id" ||
            args[3] != "--adapter-pnp" || args[5] != "--adapter-interface")
        {
            return ExitInvalidArguments;
        }

        string operation = args[2], pnp = args[4], interfaceId = args[6];
        string resultPath;
        try
        {
            resultPath = ResultPath(operation);
        }
        catch
        {
            return ExitInvalidArguments;
        }

        DateTimeOffset started = DateTimeOffset.UtcNow;
        RestartAttempt attempt = await RunRestartAsync(resultPath, pnp, interfaceId, started).ConfigureAwait(false);
        if (attempt.NotElevated) return ExitNotElevated;
        var outcome = new AdapterRestartOutcome(operation, pnp, interfaceId, started, DateTimeOffset.UtcNow,
            attempt.Attempted, attempt.ExitCode, attempt.Present, attempt.ParametersUnchanged,
            attempt.DriverUnchanged, attempt.Message, attempt.Output);
        try
        {
            EnsureSafeResultLocation(resultPath);
            WriteNew(resultPath, JsonSerializer.Serialize(outcome, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            return ExitNotElevated;
        }

        return outcome.Verified ? 0 : outcome.RebootRequired ? ExitRebootRequired : ExitNotVerified;
    }

    private sealed record RestartAttempt(bool Attempted, int? ExitCode, bool Present, bool ParametersUnchanged,
        bool DriverUnchanged, string Message, string Output, bool NotElevated = false);

    /// <summary>实际重启流程。任何失败都折成一条给人看的 Message，不向调用方抛异常。</summary>
    private static async Task<RestartAttempt> RunRestartAsync(string resultPath, string pnp, string interfaceId,
        DateTimeOffset started)
    {
        bool attempted = false, present = false, parametersSame = false, driverSame = false;
        int? exit = null;
        string message = "", output = "";
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            // 与旧实现一致：未提权时不写结果文件，直接以 5 退出。
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return new(false, null, false, false, false, "", "", NotElevated: true);

            if (Environment.OSVersion.Version.Build < 19041)
                throw new InvalidOperationException("此 Windows 版本不支持设备热重启。");
            EnsureSafeResultLocation(resultPath);
            if (File.Exists(resultPath)) throw new InvalidOperationException("操作编号已使用；未重启设备。");

            // 命名信号量可能在线程切换后才释放，因此用 WaitOne(0) 而不是 Wait()。
            using var gate = new Semaphore(1, 1, @"Global\PRO2BluetoothAdapterRestart");
            if (!gate.WaitOne(0)) throw new InvalidOperationException("其他蓝牙重启操作正在进行。");
            try
            {
                using var budget = new CancellationTokenSource(TotalBudget);
                RadioRestartTarget target = await ResolveTargetAsync(pnp, interfaceId, budget.Token).ConfigureAwait(false);
                string parameters = ParameterSnapshot(), driver = DriverSnapshot(pnp);

                string backup = Path.ChangeExtension(resultPath, ".before.json");
                WriteNew(backup, JsonSerializer.Serialize(new { target, parameters, driver, started }));

                using Process process = StartPnputil(target);
                attempted = true;
                Task<string> stdout = CaptureOutputAsync(process.StandardOutput.BaseStream);
                Task<string> stderr = CaptureOutputAsync(process.StandardError.BaseStream);
                await WaitForExitAsync(process, budget.Token).ConfigureAwait(false);
                exit = process.ExitCode;
                output = await CombineOutputAsync(stdout, stderr).ConfigureAwait(false);

                present = await WaitForPresenceAsync(pnp, budget.Token).ConfigureAwait(false);
                parametersSame = ParameterSnapshot() == parameters;
                driverSame = DriverSnapshot(pnp) == driver;
                message = RadioRestartPolicy.CompletionMessage(exit,
                    RadioRestartPolicy.IsVerifiedSuccess(exit.Value, present, parametersSame, driverSame));
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }

        return new(attempted, exit, present, parametersSame, driverSame, message, output);
    }

    private static async Task<RadioRestartTarget> ResolveTargetAsync(string pnp, string interfaceId, CancellationToken token)
    {
        SystemBluetoothSnapshot snapshot = await SystemBluetoothDiagnostics.CaptureAsync(token).ConfigureAwait(false);
        RadioRestartTarget? target = RadioRestartPolicy.Targets(snapshot).FirstOrDefault(t =>
            t.PnpId.Equals(pnp, StringComparison.OrdinalIgnoreCase) &&
            t.InterfaceId.Equals(interfaceId, StringComparison.OrdinalIgnoreCase));
        if (target == null || !target.CanRestart || !PresentStarted(pnp))
            throw new InvalidOperationException("所选适配器已拔出、不可用或身份不匹配；未重启其他设备。");

        // 单独核对蓝牙安装类：绝不接受子设备 HID、USB 集线器或 Wi-Fi 网卡。
        using RegistryKey? device = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnp);
        if (!Guid.TryParse(device?.GetValue("ClassGUID")?.ToString(), out Guid deviceClass) ||
            deviceClass != BluetoothClassGuid)
        {
            throw new InvalidOperationException("目标不是蓝牙适配器设备；未执行重启。");
        }

        return target;
    }

    private static Process StartPnputil(RadioRestartTarget target)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in RadioRestartPolicy.RestartArguments(target)) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("设备重启工具未启动。");
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken token)
    {
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            throw new TimeoutException("设备重启工具超时，实际结果未核实；不会自动重试或重启 Windows。");
        }
    }

    private static async Task<string> CombineOutputAsync(Task<string> stdout, Task<string> stderr)
    {
        string text = (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false));
        return text.Length > MaximumNativeOutputChars ? text[..MaximumNativeOutputChars] : text;
    }

    /// <summary>pnputil 返回后适配器未必立刻“已启动”，最多等 10 秒。</summary>
    private static async Task<bool> WaitForPresenceAsync(string pnp, CancellationToken token)
    {
        for (int poll = 0; poll < PresencePolls; poll++)
        {
            await Task.Delay(PresencePollInterval, token).ConfigureAwait(false);
            if (PresentStarted(pnp)) return true;
        }

        return false;
    }

    private static string ParameterSnapshot()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters");
        // 名称只枚举一次：原来四个参数各调一次 GetValueNames，等于反复展开同一个键。
        var names = key?.GetValueNames() ?? [];
        var present = new HashSet<string>(names, StringComparer.Ordinal);
        return JsonSerializer.Serialize(
            new[] { "LEConnectionIntervalMin", "LEConnectionIntervalMax", "LEConnectionLatency", "LEConnectionLSTO" }
                .ToDictionary(name => name, name => new
                {
                    present = present.Contains(name),
                    value = key?.GetValue(name),
                    kind = present.Contains(name) ? key!.GetValueKind(name).ToString() : "absent"
                }));
    }

    private static string DriverSnapshot(string pnp)
    {
        using RegistryKey? device = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnp);
        string keyName = device?.GetValue("Driver")?.ToString()
                         ?? throw new InvalidOperationException("无法读取目标驱动信息。");
        using RegistryKey? driver = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + keyName);
        if (driver == null) throw new InvalidOperationException("无法读取目标驱动信息。");
        return JsonSerializer.Serialize(new
        {
            keyName,
            provider = driver.GetValue("ProviderName"),
            version = driver.GetValue("DriverVersion"),
            date = driver.GetValue("DriverDate"),
            inf = driver.GetValue("InfPath")
        });
    }

    public static bool PresentStarted(string pnp) => CM_Locate_DevNodeW(out uint node, pnp, 0) == 0 &&
        CM_Get_DevNode_Status(out uint status, out uint problem, node, 0) == 0 && problem == 0 && (status & 8) != 0;

    private static void EnsureSafeResultLocation(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        for (string? current = parent; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("日志路径含重解析点，已停止操作。");
        }

        Directory.CreateDirectory(parent!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("日志路径不可用。");
    }

    private static void WriteNew(string path, string text)
    {
        EnsureSafeResultLocation(path);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false));
        writer.Write(text);
    }

    private static async Task<string> CaptureOutputAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        byte[] block = new byte[1024];
        int count;
        // 超过日志上限后仍然继续读，避免 pnputil 因为管道写满而卡住。
        while ((count = await stream.ReadAsync(block)) > 0)
        {
            if (buffer.Length < MaximumResultBytes)
                buffer.Write(block, 0, (int)Math.Min(count, MaximumResultBytes - buffer.Length));
        }

        return RadioRestartPolicy.DecodeNativeOutput(buffer.ToArray());
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
}
