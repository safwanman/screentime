namespace ScreenTimeTracker.Models;

public sealed class UsageData
{
    public int Version { get; set; } = 1;

    public Dictionary<string, Dictionary<string, int>> Applications { get; set; } = [];

    public Dictionary<string, Dictionary<string, int>> Websites { get; set; } = [];

    public int? DailyLimitSeconds { get; set; }

    public Dictionary<string, int> ApplicationLimitsSeconds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> WebsiteLimitsSeconds { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed record UsageRow(
    string Name,
    string Initial,
    string Duration,
    string Remaining,
    double Percent);

public sealed class LimitEntry
{
    public string Type { get; set; } = "";

    public string Name { get; set; } = "";

    public string Hours { get; set; } = "";

    public string Minutes { get; set; } = "";

    public string Seconds { get; set; } = "";
}
