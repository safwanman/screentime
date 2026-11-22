using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Globalization;
using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Services;

public sealed class UsageTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Dictionary<string, string[]> FocusAppProcessAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Battle.net"] = ["Battle.net"],
            ["Epic Games Launcher"] = ["EpicGamesLauncher"],
            ["Fortnite"] = ["FortniteClient-Win64-Shipping", "Fortnite"],
            ["Google Chrome"] = ["chrome"],
            ["League of Legends"] = ["LeagueClientUx", "League of Legends"],
            ["Microsoft Edge"] = ["msedge"],
            ["Minecraft Launcher"] = ["MinecraftLauncher"],
            ["PlayStation"] = ["PlayStation"],
            ["Riot Client"] = ["RiotClientServices"],
            ["Roblox Game Client"] = ["RobloxPlayerBeta"],
            ["VALORANT"] = ["VALORANT-Win64-Shipping", "VALORANT"],
            ["Xbox"] = ["XboxPcApp"]
        };
    private static readonly HashSet<string> SystemAppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Application Frame Host",
        "COM Surrogate",
        "Explorer",
        "File Explorer",
        "Microsoft Text Input Application",
        "Runtime Broker",
        "Search",
        "Search Application",
        "Search Host",
        "Searchhost",
        "Search Indexer",
        "Shell Infrastructure Host",
        "Shell Experience Host",
        "Start",
        "Start Menu Experience Host",
        "Text Input Application",
        "Text Input Host",
        "TextInputHost",
        "Windows Explorer",
        "Windows Input Experience",
        "Windows Search",
        "Windows Shell Experience Host",
        "Windows Start Experience",
        "Windows Widgets"
    };
    private static readonly string WindowsDirectory = Path.TrimEndingDirectorySeparator(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private static readonly TimeSpan SiteHeartbeatTimeout = TimeSpan.FromSeconds(15);
    private readonly object _dataLock = new();
    private readonly object _siteLock = new();
    private readonly string _dataPath;
    private string? _activeSite;
    private DateTime _lastSiteHeartbeatUtc;
    private int _secondsSinceSave;
    private int? _cachedForegroundProcessId;
    private DateTime _cachedForegroundProcessStartTime;
    private string? _cachedForegroundProcessName;
    private string? _cachedForegroundAppName;
    private bool _cachedForegroundIsUserApplication;

    public UsageTracker()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenTimeTracker");
        Directory.CreateDirectory(dataDirectory);
        _dataPath = Path.Combine(dataDirectory, "usage.json");

        if (File.Exists(_dataPath))
        {
            Data = JsonSerializer.Deserialize<UsageData>(File.ReadAllText(_dataPath))
                ?? throw new InvalidDataException($"Usage history at '{_dataPath}' is empty or invalid.");
        }
        else
        {
            Data = new UsageData();
        }

        Data.Applications ??= [];
        Data.Websites ??= [];
        Data.ApplicationLimitsSeconds ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Data.WebsiteLimitsSeconds ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Data.BlockedWebsites ??= [];
        Data.FocusApplications ??= [];
        Data.FocusWebsites ??= [];
        Data.LimitDowntimeTriggeredKeys ??= [];
        RemoveSystemAppsFromHistory();
        UpgradeActiveLimitDowntimeToMidnight();
    }

    public UsageData Data { get; }
    public string? CurrentApp { get; private set; }
    public string? CurrentSite { get; private set; }
    public string? CurrentProcessName { get; private set; }
    public bool IsIdle { get; private set; }
    public bool IsDashboardForeground { get; private set; }
    public int? CurrentProcessId { get; private set; }

    public TimeSpan? GetLimitDowntimeRemaining()
    {
        lock (_dataLock)
        {
            return GetLimitDowntimeRemainingUnsafe(DateTime.UtcNow);
        }
    }

    public void UnlockLimitSettings()
    {
        DateTime? previousUntil;
        lock (_dataLock)
        {
            previousUntil = Data.LimitDowntimeUntilUtc;
            Data.LimitDowntimeUntilUtc = null;
        }

        try
        {
            Save();
        }
        catch
        {
            lock (_dataLock)
            {
                Data.LimitDowntimeUntilUtc = previousUntil;
            }

            throw;
        }
    }

    public void CreateBackup(string path)
    {
        string serializedData;
        lock (_dataLock)
        {
            serializedData = JsonSerializer.Serialize(Data, JsonOptions);
        }

        File.WriteAllText(path, serializedData);
    }

    public void ExportHistoryCsv(string path)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Date,Category,Name,Seconds");

        lock (_dataLock)
        {
            AppendCsvRows(csv, "App", Data.Applications);
            AppendCsvRows(csv, "Website", Data.Websites);
        }

        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
    }

    public void ClearAllData()
    {
        Dictionary<string, Dictionary<string, int>> previousApplications;
        Dictionary<string, Dictionary<string, int>> previousWebsites;
        int? previousDailyLimit;
        Dictionary<string, int> previousApplicationLimits;
        Dictionary<string, int> previousWebsiteLimits;
        List<string> previousBlockedWebsites;
        bool previousFocusModeEnabled;
        bool previousDarkModeEnabled;
        List<string> previousFocusApplications;
        List<string> previousFocusWebsites;
        DateTime? previousDowntimeUntil;
        string? previousDowntimeDay;
        List<string> previousTriggeredKeys;

        lock (_dataLock)
        {
            previousApplications = Data.Applications;
            previousWebsites = Data.Websites;
            previousDailyLimit = Data.DailyLimitSeconds;
            previousApplicationLimits = Data.ApplicationLimitsSeconds;
            previousWebsiteLimits = Data.WebsiteLimitsSeconds;
            previousBlockedWebsites = Data.BlockedWebsites;
            previousFocusModeEnabled = Data.FocusModeEnabled;
            previousDarkModeEnabled = Data.DarkModeEnabled;
            previousFocusApplications = Data.FocusApplications;
            previousFocusWebsites = Data.FocusWebsites;
            previousDowntimeUntil = Data.LimitDowntimeUntilUtc;
            previousDowntimeDay = Data.LimitDowntimeDay;
            previousTriggeredKeys = Data.LimitDowntimeTriggeredKeys;

            Data.Applications = [];
            Data.Websites = [];
            Data.DailyLimitSeconds = null;
            Data.ApplicationLimitsSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Data.WebsiteLimitsSeconds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Data.BlockedWebsites = [];
            Data.FocusModeEnabled = false;
            Data.DarkModeEnabled = false;
            Data.FocusApplications = [];
            Data.FocusWebsites = [];
            Data.LimitDowntimeUntilUtc = null;
            Data.LimitDowntimeDay = null;
            Data.LimitDowntimeTriggeredKeys = [];
        }

        try
        {
            Save();
        }
        catch
        {
            lock (_dataLock)
            {
                Data.Applications = previousApplications;
                Data.Websites = previousWebsites;
                Data.DailyLimitSeconds = previousDailyLimit;
                Data.ApplicationLimitsSeconds = previousApplicationLimits;
                Data.WebsiteLimitsSeconds = previousWebsiteLimits;
                Data.BlockedWebsites = previousBlockedWebsites;
                Data.FocusModeEnabled = previousFocusModeEnabled;
                Data.DarkModeEnabled = previousDarkModeEnabled;
                Data.FocusApplications = previousFocusApplications;
                Data.FocusWebsites = previousFocusWebsites;
                Data.LimitDowntimeUntilUtc = previousDowntimeUntil;
                Data.LimitDowntimeDay = previousDowntimeDay;
                Data.LimitDowntimeTriggeredKeys = previousTriggeredKeys;
            }

            throw;
        }
    }

    public void StartLimitDowntime(string limitKey)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var normalizedKey = limitKey.ToLowerInvariant();
        var until = DateTime.Today.AddDays(1).ToUniversalTime();
        DateTime? previousUntil;
        string? previousDay;
        List<string> previousTriggeredKeys;

        lock (_dataLock)
        {
            previousUntil = Data.LimitDowntimeUntilUtc;
            previousDay = Data.LimitDowntimeDay;
            previousTriggeredKeys = [.. Data.LimitDowntimeTriggeredKeys];

            if (!string.Equals(Data.LimitDowntimeDay, today, StringComparison.Ordinal))
            {
                Data.LimitDowntimeDay = today;
                Data.LimitDowntimeTriggeredKeys.Clear();
            }

            var wasAlreadyTriggered = Data.LimitDowntimeTriggeredKeys.Contains(
                normalizedKey,
                StringComparer.Ordinal);
            if (!wasAlreadyTriggered)
            {
                Data.LimitDowntimeTriggeredKeys.Add(normalizedKey);
            }

            if (wasAlreadyTriggered)
            {
                return;
            }

            Data.LimitDowntimeUntilUtc = until;
        }

        try
        {
            Save();
        }
        catch
        {
            lock (_dataLock)
            {
                Data.LimitDowntimeUntilUtc = previousUntil;
                Data.LimitDowntimeDay = previousDay;
                Data.LimitDowntimeTriggeredKeys = previousTriggeredKeys;
            }

            throw;
        }
    }

    public void UpdateActiveSite(string host, DateTime receivedAtUtc)
    {
        lock (_siteLock)
        {
            _activeSite = host;
            _lastSiteHeartbeatUtc = receivedAtUtc;
        }
    }

    public void RecordSecond()
    {
        IsIdle = IsUserIdle();
        IsDashboardForeground = false;
        CurrentSite = null;
        CurrentProcessId = null;
        CurrentProcessName = null;

        if (IsIdle)
        {
            CurrentApp = null;
            CurrentProcessId = null;
            return;
        }

        var foregroundProcess = GetForegroundProcess();
        if (foregroundProcess is null)
        {
            CurrentApp = null;
            CurrentProcessId = null;
            return;
        }

        try
        {
            using (foregroundProcess)
            {
                if (string.Equals(
                        foregroundProcess.ProcessName,
                        Path.GetFileNameWithoutExtension(Environment.ProcessPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    CurrentApp = null;
                    IsDashboardForeground = true;
                    return;
                }

                var processName = foregroundProcess.ProcessName;
                var processId = foregroundProcess.Id;
                var processStartTime = foregroundProcess.StartTime;
                if (!string.Equals(
                        processName,
                        _cachedForegroundProcessName,
                        StringComparison.OrdinalIgnoreCase) ||
                    processId != _cachedForegroundProcessId ||
                    processStartTime != _cachedForegroundProcessStartTime)
                {
                    _cachedForegroundProcessId = processId;
                    _cachedForegroundProcessName = processName;
                    _cachedForegroundProcessStartTime = processStartTime;
                    _cachedForegroundIsUserApplication = IsUserApplication(foregroundProcess);
                    _cachedForegroundAppName = _cachedForegroundIsUserApplication
                        ? GetAppName(foregroundProcess, processName)
                        : null;
                }

                if (!_cachedForegroundIsUserApplication || _cachedForegroundAppName is null)
                {
                    CurrentApp = null;
                    return;
                }

                CurrentProcessId = processId;
                CurrentProcessName = processName;
                var appName = _cachedForegroundAppName;
                CurrentApp = appName;

                var today = DateTime.Now.ToString("yyyy-MM-dd");
                lock (_dataLock)
                {
                    Increment(Data.Applications, today, appName);

                    if (string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase))
                    {
                        var site = GetRecentSite();
                        if (site is not null)
                        {
                            CurrentSite = site;
                            Increment(Data.Websites, today, site);
                        }
                    }

                    _secondsSinceSave++;
                }
            }
        }
        catch (InvalidOperationException)
        {
            CurrentApp = null;
            CurrentSite = null;
            CurrentProcessId = null;
            CurrentProcessName = null;
        }
        catch (Win32Exception)
        {
            CurrentApp = null;
            CurrentSite = null;
            CurrentProcessId = null;
            CurrentProcessName = null;
        }
    }

    public int GetTodayTotalSeconds()
    {
        lock (_dataLock)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            return Data.Applications.TryGetValue(today, out var dailyUsage)
                ? dailyUsage.Values.Sum()
                : 0;
        }
    }

    public int GetTodayEntertainmentSeconds()
    {
        lock (_dataLock)
        {
            return GetTodayEntertainmentSecondsUnsafe();
        }
    }

    public bool HasReachedDailyEntertainmentLimit()
    {
        lock (_dataLock)
        {
            return Data.DailyLimitSeconds is int limit &&
                GetTodayEntertainmentSecondsUnsafe() >= limit;
        }
    }

    public bool ShouldBlockFocusApplication(string appName, string processName)
    {
        lock (_dataLock)
        {
            if (!IsFocusApplicationUnsafe(appName, processName))
            {
                return false;
            }

            return Data.FocusModeEnabled ||
                (Data.DailyLimitSeconds is int limit &&
                 GetTodayEntertainmentSecondsUnsafe() >= limit);
        }
    }

    public int GetTodaySeconds(Dictionary<string, Dictionary<string, int>> usage, string name)
    {
        lock (_dataLock)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (!usage.TryGetValue(today, out var dailyUsage))
            {
                return 0;
            }

            var matchingEntry = dailyUsage.FirstOrDefault(entry =>
                string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase));
            return matchingEntry.Value;
        }
    }

    public bool IsSiteBlocked(string host)
    {
        return GetSiteBlockReason(host) is not null;
    }

    public string? GetSiteBlockReason(string host)
    {
        return GetSiteBlockReason(host, isShortForm: false);
    }

    public string? GetSiteBlockReason(string host, bool isShortForm)
    {
        lock (_dataLock)
        {
            if (IsWebsiteBlocked(host))
            {
                return "blocked";
            }

            if (isShortForm || IsDomainOrSubdomain(host, "tiktok.com"))
            {
                return "short-form";
            }

            var isFocusWebsite = IsFocusWebsiteUnsafe(host);
            if (Data.FocusModeEnabled && isFocusWebsite)
            {
                return "focus";
            }

            if (isFocusWebsite &&
                Data.DailyLimitSeconds is int dailyLimit &&
                GetTodayEntertainmentSecondsUnsafe() >= dailyLimit)
            {
                return "daily-entertainment";
            }

            var siteLimit = GetLimitSeconds(Data.WebsiteLimitsSeconds, host);
            return siteLimit is int limit && GetTodaySeconds(Data.Websites, host) >= limit
                ? "website"
                : null;
        }
    }

    public void SetFocusModeEnabled(bool enabled)
    {
        bool previousValue;
        lock (_dataLock)
        {
            previousValue = Data.FocusModeEnabled;
            Data.FocusModeEnabled = enabled;
        }

        try
        {
            Save();
        }
        catch
        {
            lock (_dataLock)
            {
                Data.FocusModeEnabled = previousValue;
            }

            throw;
        }
    }

    public void SetDarkModeEnabled(bool enabled)
    {
        bool previousValue;
        lock (_dataLock)
        {
            previousValue = Data.DarkModeEnabled;
            Data.DarkModeEnabled = enabled;
        }

        try
        {
            Save();
        }
        catch
        {
            lock (_dataLock)
            {
                Data.DarkModeEnabled = previousValue;
            }

            throw;
        }
    }

    public void UpdateFocusBlocklists(
        IEnumerable<string> applications,
        IEnumerable<string> websites)
    {
        var normalizedApplications = applications
            .Select(NormalizeFocusApplication)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var normalizedWebsites = websites
            .Select(NormalizeBlockedWebsite)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> previousApplications;
        List<string> previousWebsites;

        lock (_dataLock)
        {
            previousApplications = Data.FocusApplications;
            previousWebsites = Data.FocusWebsites;
            Data.FocusApplications = normalizedApplications;
            Data.FocusWebsites = normalizedWebsites;
        }

        try
        {
            Save();
        }
        catch
        {
            lock (_dataLock)
            {
                Data.FocusApplications = previousApplications;
                Data.FocusWebsites = previousWebsites;
            }

            throw;
        }
    }

    public bool IsFocusApplication(string appName, string processName)
    {
        lock (_dataLock)
        {
            return IsFocusApplicationUnsafe(appName, processName);
        }
    }

    public IReadOnlyList<string> CloseRunningFocusApplications(ISet<int> requestedProcessIds)
    {
        var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentProcessName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);

        foreach (var process in Process.GetProcesses())
        {
            string? appName = null;
            try
            {
                var processName = process.ProcessName;
                if (string.Equals(processName, currentProcessName, StringComparison.OrdinalIgnoreCase) ||
                    !IsUserApplication(process))
                {
                    continue;
                }

                appName = GetAppName(process, processName);
                if (!IsFocusApplication(appName, processName) ||
                    !requestedProcessIds.Add(process.Id))
                {
                    continue;
                }

                if (!process.CloseMainWindow())
                {
                    failures.Add(appName);
                }
            }
            catch (Win32Exception)
            {
                if (appName is not null)
                {
                    failures.Add(appName);
                }
            }
            catch (InvalidOperationException)
            {
                if (appName is not null)
                {
                    failures.Add(appName);
                }
            }
            catch (NotSupportedException)
            {
                if (appName is not null)
                {
                    failures.Add(appName);
                }
            }
            catch (SecurityException)
            {
                if (appName is not null)
                {
                    failures.Add(appName);
                }
            }
            catch (UnauthorizedAccessException)
            {
                if (appName is not null)
                {
                    failures.Add(appName);
                }
            }
            catch (ArgumentException)
            {
                if (appName is not null)
                {
                    failures.Add(appName);
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        return failures.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void UpdateBlockedWebsites(IEnumerable<string> blockedWebsites)
    {
        var normalizedWebsites = blockedWebsites
            .Select(NormalizeBlockedWebsite)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<string> previousWebsites;

        lock (_dataLock)
        {
            previousWebsites = Data.BlockedWebsites;
            Data.BlockedWebsites = normalizedWebsites;
        }

        try
        {
            Save();
        }
        catch
        {
            lock (_dataLock)
            {
                Data.BlockedWebsites = previousWebsites;
            }

            throw;
        }
    }

    public void UpdateLimits(
        int? dailyLimitSeconds,
        Dictionary<string, int> applicationLimits,
        Dictionary<string, int> websiteLimits)
    {
        int? previousDailyLimit;
        Dictionary<string, int> previousApplicationLimits;
        Dictionary<string, int> previousWebsiteLimits;
        lock (_dataLock)
        {
            if (GetLimitDowntimeRemainingUnsafe(DateTime.UtcNow) is TimeSpan remaining)
            {
                throw new InvalidOperationException(
                    $"Limits are locked for {Math.Ceiling(remaining.TotalMinutes)} more minute(s).");
            }

            previousDailyLimit = Data.DailyLimitSeconds;
            previousApplicationLimits = Data.ApplicationLimitsSeconds;
            previousWebsiteLimits = Data.WebsiteLimitsSeconds;
            Data.DailyLimitSeconds = dailyLimitSeconds;
            Data.ApplicationLimitsSeconds = applicationLimits;
            Data.WebsiteLimitsSeconds = websiteLimits;
        }

        try
        {
            Save();
        }
        catch
        {
            lock (_dataLock)
            {
                Data.DailyLimitSeconds = previousDailyLimit;
                Data.ApplicationLimitsSeconds = previousApplicationLimits;
                Data.WebsiteLimitsSeconds = previousWebsiteLimits;
            }

            throw;
        }
    }

    public bool TryCloseCurrentApp()
    {
        if (CurrentProcessId is not int processId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.CloseMainWindow();
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public static int? GetLimitSeconds(Dictionary<string, int> limits, string name)
    {
        var matchingEntry = limits.FirstOrDefault(entry =>
            string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase));
        return matchingEntry.Key is null ? null : matchingEntry.Value;
    }

    public static IReadOnlyList<string> GetRunningAppNames()
    {
        var currentProcessName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        var appNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!IsUserApplication(process) ||
                    string.Equals(process.ProcessName, currentProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                appNames.Add(GetAppName(process, process.ProcessName));
            }
            catch (Win32Exception)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (SecurityException)
            {
            }
            catch (ArgumentException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return appNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool IsSystemAppName(string name)
    {
        return SystemAppNames.Contains(name.Trim());
    }

    public void SaveIfNeeded()
    {
        if (_secondsSinceSave < 30)
        {
            return;
        }

        Save();
    }

    public void Save()
    {
        string serializedData;
        lock (_dataLock)
        {
            serializedData = JsonSerializer.Serialize(Data, JsonOptions);
        }

        var temporaryPath = $"{_dataPath}.tmp";
        File.WriteAllText(temporaryPath, serializedData);
        File.Move(temporaryPath, _dataPath, true);
        _secondsSinceSave = 0;
    }

    private static string GetAppName(Process process, string processName)
    {
        if (string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Edge";
        }

        if (string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase))
        {
            return "Google Chrome";
        }

        try
        {
            var executable = process.MainModule?.FileName;
            var description = executable is null
                ? null
                : FileVersionInfo.GetVersionInfo(executable).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description.Trim();
            }
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return System.Globalization.CultureInfo.InvariantCulture.TextInfo
            .ToTitleCase(processName.Replace('_', ' ').Replace('-', ' '));
    }

    private static bool IsUserApplication(Process process)
    {
        if (process.MainWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        var executablePath = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var fullExecutablePath = Path.GetFullPath(executablePath);
        return !fullExecutablePath.StartsWith(
            WindowsDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveSystemAppsFromHistory()
    {
        var historyChanged = false;
        foreach (var dailyUsage in Data.Applications.Values)
        {
            foreach (var appName in dailyUsage.Keys.Where(IsSystemAppName).ToArray())
            {
                historyChanged |= dailyUsage.Remove(appName);
            }
        }

        foreach (var appName in Data.ApplicationLimitsSeconds.Keys.Where(IsSystemAppName).ToArray())
        {
            historyChanged |= Data.ApplicationLimitsSeconds.Remove(appName);
        }

        if (historyChanged)
        {
            Save();
        }
    }

    private string? GetRecentSite()
    {
        lock (_siteLock)
        {
            return DateTime.UtcNow - _lastSiteHeartbeatUtc <= SiteHeartbeatTimeout
                ? _activeSite
                : null;
        }
    }

    private static void Increment(
        Dictionary<string, Dictionary<string, int>> usage,
        string day,
        string name)
    {
        if (!usage.TryGetValue(day, out var dailyUsage))
        {
            dailyUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            usage.Add(day, dailyUsage);
        }

        dailyUsage.TryGetValue(name, out var seconds);
        dailyUsage[name] = seconds + 1;
    }

    private static void AppendCsvRows(
        StringBuilder csv,
        string category,
        Dictionary<string, Dictionary<string, int>> usage)
    {
        foreach (var day in usage.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            foreach (var item in day.Value.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                csv.Append(QuoteCsv(day.Key))
                    .Append(',')
                    .Append(QuoteCsv(category))
                    .Append(',')
                    .Append(QuoteCsv(item.Key))
                    .Append(',')
                    .AppendLine(item.Value.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static string QuoteCsv(string value)
    {
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private bool IsWebsiteBlocked(string host)
    {
        return Data.BlockedWebsites.Any(blockedWebsite =>
            IsDomainOrSubdomain(host, blockedWebsite));
    }

    private int GetTodayEntertainmentSecondsUnsafe()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var appSeconds = 0;
        var edgeSeconds = 0;
        var websiteSeconds = 0;
        var trackedWebsiteSeconds = 0;

        if (Data.Applications.TryGetValue(today, out var dailyApps))
        {
            foreach (var app in dailyApps)
            {
                if (!Data.FocusApplications.Any(focusApp =>
                        IsFocusApplicationIdentifierMatch(focusApp, app.Key, app.Key)))
                {
                    continue;
                }

                appSeconds += app.Value;
                if (string.Equals(app.Key, "Microsoft Edge", StringComparison.OrdinalIgnoreCase))
                {
                    edgeSeconds += app.Value;
                }
            }
        }

        if (Data.Websites.TryGetValue(today, out var dailyWebsites))
        {
            foreach (var website in dailyWebsites)
            {
                trackedWebsiteSeconds += website.Value;
                if (IsFocusWebsiteUnsafe(website.Key))
                {
                    websiteSeconds += website.Value;
                }
            }
        }

        var edgeTimeWithoutTrackedWebsites = appSeconds - Math.Min(edgeSeconds, trackedWebsiteSeconds);
        return edgeTimeWithoutTrackedWebsites + websiteSeconds;
    }

    private bool IsFocusApplicationUnsafe(string appName, string processName)
    {
        return Data.FocusApplications.Any(blockedApp =>
            IsFocusApplicationIdentifierMatch(blockedApp, appName, processName));
    }

    private static bool IsFocusApplicationIdentifierMatch(
        string blockedApp,
        string appName,
        string processName)
    {
        var normalizedBlockedApp = NormalizeFocusApplicationIdentifier(blockedApp);
        var normalizedAppName = NormalizeFocusApplicationIdentifier(appName);
        var normalizedProcessName = NormalizeFocusApplicationIdentifier(processName);
        if (string.Equals(normalizedBlockedApp, normalizedAppName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedBlockedApp, normalizedProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (FocusAppProcessAliases.TryGetValue(blockedApp, out var aliases) &&
            aliases.Any(alias =>
                string.Equals(alias, normalizedProcessName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(alias, normalizedAppName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return FocusAppProcessAliases.Any(aliasGroup =>
            aliasGroup.Value.Any(alias =>
                string.Equals(alias, normalizedBlockedApp, StringComparison.OrdinalIgnoreCase)) &&
            (string.Equals(aliasGroup.Key, normalizedAppName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(aliasGroup.Key, normalizedProcessName, StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsFocusWebsiteUnsafe(string host)
    {
        return Data.FocusWebsites.Any(domain => IsDomainOrSubdomain(host, domain));
    }

    private static bool IsDomainOrSubdomain(string host, string domain)
    {
        return string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBlockedWebsite(string host)
    {
        var normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalizedHost.StartsWith("www.", StringComparison.Ordinal))
        {
            normalizedHost = normalizedHost[4..];
        }

        if (normalizedHost.Contains('/') ||
            normalizedHost.Contains(':') ||
            Uri.CheckHostName(normalizedHost) == UriHostNameType.Unknown)
        {
            throw new ArgumentException(
                $"'{host}' is not a valid website domain.",
                nameof(host));
        }

        return normalizedHost;
    }

    private static string NormalizeFocusApplication(string appName)
    {
        var normalizedName = NormalizeFocusApplicationIdentifier(appName);
        if (normalizedName.Length == 0 ||
            normalizedName.Length > 128 ||
            normalizedName.Contains('/') ||
            normalizedName.Contains('\\'))
        {
            throw new ArgumentException(
                $"'{appName}' is not a valid application name or process name.",
                nameof(appName));
        }

        return normalizedName;
    }

    private static string NormalizeFocusApplicationIdentifier(string name)
    {
        var normalizedName = name.Trim();
        return normalizedName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalizedName[..^4]
            : normalizedName;
    }

    private TimeSpan? GetLimitDowntimeRemainingUnsafe(DateTime nowUtc)
    {
        if (!string.Equals(
                Data.LimitDowntimeDay,
                DateTime.Now.ToString("yyyy-MM-dd"),
                StringComparison.Ordinal) ||
            Data.LimitDowntimeUntilUtc is not DateTime untilUtc)
        {
            return null;
        }

        var remaining = untilUtc - nowUtc;
        return remaining > TimeSpan.Zero ? remaining : null;
    }

    private void UpgradeActiveLimitDowntimeToMidnight()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var untilMidnightUtc = DateTime.Today.AddDays(1).ToUniversalTime();
        var changed = false;

        lock (_dataLock)
        {
            if (string.Equals(Data.LimitDowntimeDay, today, StringComparison.Ordinal) &&
                Data.LimitDowntimeUntilUtc is DateTime untilUtc &&
                untilUtc > DateTime.UtcNow &&
                untilUtc < untilMidnightUtc)
            {
                Data.LimitDowntimeUntilUtc = untilMidnightUtc;
                changed = true;
            }
        }

        if (changed)
        {
            Save();
        }
    }

    private static bool IsUserIdle()
    {
        var lastInput = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref lastInput))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var millisecondsSinceInput = unchecked((uint)Environment.TickCount - lastInput.Time);
        return millisecondsSinceInput >= 300_000;
    }

    private static Process? GetForegroundProcess()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            return Process.GetProcessById((int)processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);
}
