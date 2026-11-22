namespace Pro2Plus;

public sealed partial class MainViewModel
{
    // 每次刷新都会重建这批通知，属性名数组必须复用。
    private static readonly string[] ConnectionDiagnosisProperties =
    [
        nameof(HasConnectionWarning), nameof(ConnectionWarningTitle),
        nameof(ConnectionWarningAdvice), nameof(ConnectionWarningEvidence)
    ];

    private Pro2ControllerSlot? ConnectionWarningSlot => pro2Slots.FirstOrDefault(s =>
        s.Enabled && !s.IsSleeping && !s.InputSource.IsRunning && s.InputSource.ConnectionDiagnosis.Visible);
    public bool HasConnectionWarning => ConnectionWarningSlot != null;
    public string ConnectionWarningTitle => ConnectionWarningSlot is { } s
        ? s.Name + " · " + s.InputSource.ConnectionDiagnosis.Title : "";
    public string ConnectionWarningAdvice => ConnectionWarningSlot?.InputSource.ConnectionDiagnosis.Advice ?? "";
    public string ConnectionWarningEvidence => ConnectionWarningSlot is { } s
        ? EnvironmentHomeSummary + "\n" + s.InputSource.ConnectionDiagnosticText : "";

    private void RefreshConnectionDiagnosis()
    {
        for (int i = 0; i < ConnectionDiagnosisProperties.Length; i++)
            OnPropertyChanged(ConnectionDiagnosisProperties[i]);
    }
}
