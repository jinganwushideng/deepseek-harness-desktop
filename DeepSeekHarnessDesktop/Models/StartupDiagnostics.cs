namespace DeepSeekHarnessDesktop.Models;

public enum StartupStage
{
    None,
    ValidatingSettings,
    CheckingRuntime,
    CheckingPort,
    PreparingDirectories,
    StartingProcess,
    WaitingForPort,
    WaitingForHttp,
    WaitingForApi,
    Ready
}

public sealed record StartupProgress(
    StartupStage Stage,
    int Percentage,
    string Title,
    string Detail = "",
    bool IsIndeterminate = false);

public sealed record StartupFailure(
    StartupStage Stage,
    string Title,
    string Detail,
    string Suggestion,
    DateTimeOffset Time,
    IReadOnlyList<string> RecentLogLines)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Detail) ? Title : $"{Title}\n{Detail}";
}
