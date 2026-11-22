using System;

namespace Pro2Plus;

public enum ParameterRoute
{
    UnsupportedPlatform,
    ApiUnavailable,
    ExplicitDiagnostic,
    ValidatedAutomatic,
    UnvalidatedEnvironment
}

/// <summary>
/// 自动启用只限于在用户 Win11 机器上实测过的那一组「系统栈 + 适配器/驱动」组合，
/// 不代表对所有 Win11 的认证。驱动信息只在连接建立时读一次，绝不在通知回调里读取。
/// </summary>
public static class ValidatedConnectionProfile
{
    public const string ProfileId = "win11_26200_intel_8087_0026_22_250_0_2";
    public const int ValidatedBuild = 26200;
    public const string ValidatedStackSha256 = "6750A4EA8ACEC328EC615863CC1A10B5808BE0BEACBF8F9F329FA340DED38D62";
    public const string ValidatedDriverVersion = "22.250.0.2";

    public static bool MatchesStack(EnvironmentFacts facts) =>
        facts.IsWindows && facts.Build == ValidatedBuild &&
        facts.RequestParametersApiAvailable && facts.ReadParametersApiAvailable &&
        string.Equals(facts.StackSha256, ValidatedStackSha256, StringComparison.OrdinalIgnoreCase);

    public static ParameterRoute Select(
        EnvironmentFacts facts, BluetoothAdapterDiagnosticInfo? adapter, bool diagnosticOptIn)
    {
        if (!facts.IsWindows || facts.Build < 22000) return ParameterRoute.UnsupportedPlatform;
        if (!facts.RequestParametersApiAvailable || !facts.ReadParametersApiAvailable)
            return ParameterRoute.ApiUnavailable;
        if (diagnosticOptIn) return ParameterRoute.ExplicitDiagnostic;
        if (MatchesStack(facts) && adapter != null &&
            string.Equals(adapter.Vid, "8087", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(adapter.Pid, "0026", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(adapter.DriverProvider?.Trim(), "Intel Corporation", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(adapter.DriverVersion?.Trim(), ValidatedDriverVersion, StringComparison.Ordinal))
            return ParameterRoute.ValidatedAutomatic;
        return ParameterRoute.UnvalidatedEnvironment;
    }

    public static bool AllowsCustomRequest(ParameterRoute route) =>
        route is ParameterRoute.ExplicitDiagnostic or ParameterRoute.ValidatedAutomatic;

    public static BluetoothAdapterDiagnosticInfo? SingleAuditedAdapter(SystemBluetoothSnapshot environment) =>
        environment.Warning.Length == 0 && environment.Adapters.Count == 1 &&
        environment.Adapters[0].LowEnergy && environment.Adapters[0].Central
            ? environment.Adapters[0].Driver : null;
}
