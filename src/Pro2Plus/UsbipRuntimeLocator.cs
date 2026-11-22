using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Pro2Plus;

public sealed record UsbipRuntime(string ExePath, string DirectoryPath, string Source = "unknown");
public sealed record UsbipInstaller(string InstallerPath, string? LicensePath);
public sealed record UsbipProbeResult(bool Ready, string Detail);

/// <summary>
/// 定位 usbip.exe 与内置安装包。路径规则全部交给 <see cref="UsbipPathPolicy"/>，
/// 这里只负责按优先级枚举候选、去重、验证文件存在，以及探测进程与资源提取。
///
/// 候选计划按 5 秒缓存：一次连接诊断里 Find() 会被连续调用多次，旧实现每次都
/// 重新遍历 3 组 PATH、4 组注册表、应用目录向上 8 级以及递归磁盘扫描，
/// 是纯粹的重复 I/O。缓存只覆盖静态候选（PATH/注册表/固定目录），
/// 用户显式配置的手工路径仍每次优先检查。
/// </summary>
public static class UsbipRuntimeLocator
{
    public const string BundledVersion = UsbipPathPolicy.BundledVersion;
    public const string InstallerFileName = UsbipPathPolicy.InstallerFileName;

    private const double PlanTtlSeconds = 5;
    private const int MaxScanDepth = 3;
    private const int MaxScanResults = 8;
    private static readonly object PlanGate = new();
    private static (string Path, string Source)[]? staticPlan;
    private static long planBuiltTicks;

    public static UsbipRuntime? Find(string? configuredExePath = null)
    {
        string configured = configuredExePath ?? AppSettings.Load().UsbipExecutablePath;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            UsbipRuntime? manual = Check(configured, "saved_manual_path", seen);
            if (manual != null) return manual;
        }

        foreach ((string path, string source) in GetStaticPlan())
        {
            UsbipRuntime? hit = Check(path, source, seen);
            if (hit != null) return hit;
        }
        return null;
    }

    private static UsbipRuntime? Check(string raw, string source, HashSet<string> seen)
    {
        string path = UsbipPathPolicy.NormalizeExecutablePath(raw);
        if (path.Length == 0 || !seen.Add(path) || !File.Exists(path)) return null;
        return new UsbipRuntime(path, Path.GetDirectoryName(path) ?? "", source);
    }

    private static (string Path, string Source)[] GetStaticPlan()
    {
        lock (PlanGate)
        {
            if (staticPlan != null &&
                Stopwatch.GetElapsedTime(planBuiltTicks).TotalSeconds < PlanTtlSeconds)
            {
                return staticPlan;
            }
            staticPlan = EnumerateStaticCandidates().ToArray();
            planBuiltTicks = Stopwatch.GetTimestamp();
            return staticPlan;
        }
    }

    /// <summary>顺序与旧实现一致：PATH → 已安装产品 → 应用附近 → 常见目录。</summary>
    private static IEnumerable<(string Path, string Source)> EnumerateStaticCandidates()
    {
        foreach (EnvironmentVariableTarget target in new[]
        {
            EnvironmentVariableTarget.Process,
            EnvironmentVariableTarget.User,
            EnvironmentVariableTarget.Machine
        })
        {
            string? path = null;
            try { path = Environment.GetEnvironmentVariable("PATH", target); } catch { }
            foreach (string candidate in UsbipPathPolicy.GetPathCandidates(path))
            {
                yield return (candidate, "path_" + target);
            }
        }

        foreach ((string path, string source) in InstalledProductCandidates())
        {
            yield return (path, source);
        }

        foreach (string root in UsbipPathPolicy.EnumerateSearchRoots(
            AppContext.BaseDirectory, UsbipPathPolicy.LocalRelativeRoots))
        {
            foreach (string found in TryFindUnder(root))
            {
                yield return (found, "near_application");
            }
        }

        foreach (string programRoot in ProgramFilesRoots())
        {
            foreach (string name in UsbipPathPolicy.CommonDirectoryNames)
            {
                foreach (string relative in UsbipPathPolicy.ExecutableRelativePaths)
                {
                    yield return (Path.Combine(programRoot, name, relative), "common_directory");
                }
            }
        }
    }

    private static IReadOnlyList<(string Path, string Source)> InstalledProductCandidates()
    {
        var candidates = new List<(string Path, string Source)>();
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using (RegistryKey? appPath = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\usbip.exe"))
                {
                    string path = UsbipPathPolicy.NormalizeExecutablePath(appPath?.GetValue(null) as string);
                    if (path.Length > 0) candidates.Add((path, "app_paths_" + hive + "_" + view));
                }

                using RegistryKey? uninstall = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;

                foreach (string subKeyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? product = uninstall.OpenSubKey(subKeyName);
                        string displayName = product?.GetValue("DisplayName") as string ?? "";
                        if (!LooksLikeUsbipProduct(displayName)) continue;
                        foreach (string path in UsbipPathPolicy.GetInstallRecordCandidatePaths(
                            product?.GetValue("InstallLocation") as string,
                            product?.GetValue("DisplayIcon") as string,
                            product?.GetValue("UninstallString") as string))
                        {
                            candidates.Add((path, "uninstall_" + hive + "_" + view));
                        }
                    }
                    catch
                    {
                        // 单个不可读的产品不能遮蔽其它记录。
                    }
                }
            }
            catch
            {
                // 注册表发现只是补充；PATH 与固定位置始终可用。
            }
        }
        return candidates;
    }

    private static bool LooksLikeUsbipProduct(string displayName) =>
        displayName.Contains("USBip", StringComparison.OrdinalIgnoreCase) ||
        displayName.Contains("USB-IP", StringComparison.OrdinalIgnoreCase) ||
        displayName.Contains("USB/IP", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 递归找 usbip.exe，但限制深度与结果数：旧实现用 AllDirectories 无上限，
    /// 遇到符号链接或很深的 node_modules 会把整次连接诊断拖慢。
    /// </summary>
    private static IEnumerable<string> TryFindUnder(string root)
    {
        if (!Directory.Exists(root)) yield break;

        List<string> files;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = MaxScanDepth,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            };
            files = Directory.EnumerateFiles(root, UsbipPathPolicy.ExecutableName, options)
                .Take(MaxScanResults)
                .ToList();
        }
        catch
        {
            yield break;
        }

        foreach (string file in files) yield return file;
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) yield return programFiles;

        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86)) yield return programFilesX86;
    }

    public static UsbipInstaller? FindBundledInstaller()
    {
        foreach (string candidate in UsbipPathPolicy.EnumerateSearchRoots(
            AppContext.BaseDirectory, UsbipPathPolicy.InstallerRelativePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            string? dir = Path.GetDirectoryName(candidate);
            string? license = dir == null ? null : Path.Combine(dir, "LICENSE.txt");
            return new UsbipInstaller(candidate, File.Exists(license) ? license : null);
        }
        return ExtractEmbeddedInstaller();
    }

    private static UsbipInstaller? ExtractEmbeddedInstaller()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Pro2Plus", "embedded", "pure-software-v1.0.0", "usbip-win2", BundledVersion);
        Directory.CreateDirectory(root);
        string installer = Path.Combine(root, InstallerFileName);
        string license = Path.Combine(root, "LICENSE.txt");

        if (!ExtractResourceIfAvailable(assembly, "Embedded.usbip.installer", installer)) return null;
        bool hasLicense = ExtractResourceIfAvailable(assembly, "Embedded.usbip.license", license);
        return new UsbipInstaller(installer, hasLicense ? license : null);
    }

    private static bool ExtractResourceIfAvailable(Assembly assembly, string resourceName, string destination)
    {
        using Stream? source = assembly.GetManifestResourceStream(resourceName);
        if (source == null) return false;
        if (File.Exists(destination) && new FileInfo(destination).Length == source.Length) return true;

        string temp = destination + ".tmp";
        using (FileStream output = File.Create(temp)) source.CopyTo(output);
        File.Move(temp, destination, overwrite: true);
        return true;
    }

    public static async Task<UsbipProbeResult> ProbeAsync(UsbipRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = runtime.ExePath,
                Arguments = "port",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = runtime.DirectoryPath
            });
            if (process == null) return new UsbipProbeResult(false, "usbip.exe 无法启动");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new UsbipProbeResult(false, "usbip port 检测超过 5 秒");
            }

            string output = ((await process.StandardOutput.ReadToEndAsync()) + " " +
                             (await process.StandardError.ReadToEndAsync())).Trim();
            return process.ExitCode == 0
                ? new UsbipProbeResult(true, string.IsNullOrWhiteSpace(output) ? "driver command ready" : output)
                : new UsbipProbeResult(false, "usbip port exit=" + process.ExitCode +
                    (string.IsNullOrWhiteSpace(output) ? "" : " / " + output));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UsbipProbeResult(false, ex.Message);
        }
    }

    // 以下门面保留旧公开 API，实现转交纯策略类。
    public static string BuildPathWithUsbipDirectory(string currentPath, UsbipRuntime runtime) =>
        UsbipPathPolicy.BuildPathWithDirectory(currentPath, runtime.DirectoryPath);

    public static string NormalizeExecutablePath(string? value) =>
        UsbipPathPolicy.NormalizeExecutablePath(value);

    public static string ParseRegisteredExecutablePath(string? value) =>
        UsbipPathPolicy.ParseRegisteredExecutablePath(value);

    public static IReadOnlyList<string> GetInstallRecordCandidatePaths(
        string? installLocation, string? displayIcon, string? uninstallString) =>
        UsbipPathPolicy.GetInstallRecordCandidatePaths(installLocation, displayIcon, uninstallString);

    public static IEnumerable<string> GetPathCandidates(string? pathValue) =>
        UsbipPathPolicy.GetPathCandidates(pathValue);
}
