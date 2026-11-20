using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Services;

public sealed class UsageTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
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
        RemoveSystemAppsFromHistory();
    }

    public UsageData Data { get; }
    public string? CurrentApp { get; private set; }
    public string? CurrentSite { get; private set; }
    public bool IsIdle { get; private set; }
    public bool IsDashboardForeground { get; private set; }
    public int? CurrentProcessId { get; private set; }

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
        }
        catch (Win32Exception)
        {
            CurrentApp = null;
            CurrentSite = null;
            CurrentProcessId = null;
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
        lock (_dataLock)
        {
            if (Data.DailyLimitSeconds is int dailyLimit && GetTodayTotalSeconds() >= dailyLimit)
            {
                return "daily";
            }

            var siteLimit = GetLimitSeconds(Data.WebsiteLimitsSeconds, host);
            return siteLimit is int limit && GetTodaySeconds(Data.Websites, host) >= limit
                ? "website"
                : null;
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
