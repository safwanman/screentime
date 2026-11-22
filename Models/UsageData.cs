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

    public List<string> BlockedWebsites { get; set; } = [];

    public bool FocusModeEnabled { get; set; }

    public List<string> FocusApplications { get; set; } =
    [
        "Battle.net",
        "Discord",
        "Epic Games Launcher",
        "Fortnite",
        "League of Legends",
        "Minecraft Launcher",
        "Netflix",
        "PlayStation",
        "Riot Client",
        "Roblox Game Client",
        "Spotify",
        "Steam",
        "VALORANT",
        "Xbox"
    ];

    public List<string> FocusWebsites { get; set; } =
    [
        "battle.net",
        "crunchyroll.com",
        "discord.com",
        "disneyplus.com",
        "epicgames.com",
        "facebook.com",
        "fortnite.com",
        "hulu.com",
        "instagram.com",
        "kick.com",
        "leagueoflegends.com",
        "max.com",
        "music.apple.com",
        "netflix.com",
        "paramountplus.com",
        "pinterest.com",
        "playvalorant.com",
        "playstation.com",
        "primevideo.com",
        "reddit.com",
        "roblox.com",
        "riotgames.com",
        "snapchat.com",
        "soundcloud.com",
        "spotify.com",
        "steamcommunity.com",
        "steampowered.com",
        "tiktok.com",
        "twitch.tv",
        "x.com",
        "xbox.com",
        "youtube.com",
        "minecraft.net"
    ];

    public DateTime? LimitDowntimeUntilUtc { get; set; }

    public string? LimitDowntimeDay { get; set; }

    public List<string> LimitDowntimeTriggeredKeys { get; set; } = [];
}

public sealed record UsageRow(
    string Name,
    string Initial,
    string Duration,
    string Remaining,
    double Percent);

public sealed record WeeklyUsageRow(
    string Date,
    string AppTime,
    string WebsiteTime,
    double AppPercent,
    double WebsitePercent);

public sealed class LimitEntry
{
    public string Type { get; set; } = "";

    public string Name { get; set; } = "";

    public string Hours { get; set; } = "";

    public string Minutes { get; set; } = "";

    public string Seconds { get; set; } = "";
}
