using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;

using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Application = System.Windows.Application;
using Window = System.Windows.Window;

namespace ScreenTimeTracker;

public partial class MainWindow : Window
{
    private readonly UsageTracker _tracker;
    private readonly EdgeBridge _edgeBridge;
    private readonly DispatcherTimer _timer;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.ToolStripMenuItem _startupMenuItem;
    private readonly HashSet<string> _notifiedLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _closeRequestedProcessIds = [];
    private readonly HashSet<int> _focusCloseRequestedProcessIds = [];
    private int _dashboardRefreshTicks;
    private bool _exitRequested;
    private string? _limitStateDay;
    private string? _bridgeError;

    public MainWindow(bool startInBackground = false)
    {
        InitializeComponent();

        if (startInBackground)
        {
            ShowInTaskbar = false;
        }

        _startupMenuItem = new System.Windows.Forms.ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupService.IsEnabled(),
            CheckOnClick = true
        };
        _startupMenuItem.Click += OnStartupMenuItemClick;

        var trayMenu = new System.Windows.Forms.ContextMenuStrip();
        trayMenu.Items.Add("Open Screen Time Tracker", null, (_, _) => ShowDashboard());
        trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        trayMenu.Items.Add(_startupMenuItem);
        trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Screen Time Tracker",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowDashboard();

        _tracker = new UsageTracker();
        _edgeBridge = new EdgeBridge(_tracker);
        _edgeBridge.ListenerFailed += exception =>
        {
            _bridgeError = $"Edge website link stopped: {exception.Message}";
            Dispatcher.InvokeAsync(UpdateStatus);
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        DateText.Text = DateTime.Now.ToString("dddd, MMM d").ToUpperInvariant();

        try
        {
            _edgeBridge.Start();
        }
        catch (HttpListenerException exception)
        {
            _bridgeError = $"Edge website link unavailable: {exception.Message}";
        }

        UpdateDashboard();
        UpdateStatus();
        UpdateLimitsButton();
        UpdateFocusModeControls();
        _timer.Start();
        Closed += OnClosed;
        Closing += OnClosing;
        StateChanged += OnStateChanged;

    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            _tracker.RecordSecond();
            EnforceFocusMode();
            CheckLimits();
            _tracker.SaveIfNeeded();
            UpdateLimitsButton();
            if (IsVisible)
            {
                if (++_dashboardRefreshTicks >= 5)
                {
                    _dashboardRefreshTicks = 0;
                    UpdateDashboard();
                    UpdateStatus();
                }
            }
            else
            {
                _dashboardRefreshTicks = 0;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            StatusText.Text = $"Tracking issue: {exception.Message}";
        }
    }

    private void UpdateDashboard()
    {
        DateText.Text = DateTime.Now.ToString("dddd, MMM d").ToUpperInvariant();
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var apps = GetTodayUsage(_tracker.Data.Applications, today);
        var sites = GetTodayUsage(_tracker.Data.Websites, today);
        var entertainmentSeconds = _tracker.GetTodayEntertainmentSeconds();

        TotalTimeText.Text = FormatDuration(entertainmentSeconds);
        TotalTimeDescription.Text = _tracker.Data.DailyLimitSeconds is int dailyLimit
            ? entertainmentSeconds >= dailyLimit
                ? "Daily entertainment limit reached"
                : $"{FormatDuration(dailyLimit - entertainmentSeconds)} left of today's entertainment limit"
            : "Time from apps and websites on your Focus lists";
        AppCountText.Text = apps.Count.ToString();
        SiteCountText.Text = sites.Count.ToString();
        AppList.ItemsSource = BuildRows(apps, _tracker.Data.ApplicationLimitsSeconds);
        SiteList.ItemsSource = BuildRows(sites, _tracker.Data.WebsiteLimitsSeconds);
        AppEmptyText.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SiteEmptyText.Visibility = sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStatus()
    {
        if (_tracker.Data.FocusModeEnabled)
        {
            StatusText.Text = "Focus mode is on";
        }
        else if (_tracker.HasReachedDailyEntertainmentLimit())
        {
            StatusText.Text = "Daily entertainment limit reached";
        }
        else if (_tracker.IsIdle)
        {
            StatusText.Text = "Paused while you're away";
        }
        else if (_tracker.IsDashboardForeground)
        {
            StatusText.Text = "Dashboard open · not counting this window";
        }
        else if (_tracker.CurrentApp is null)
        {
            StatusText.Text = "Waiting for activity";
        }
        else if (_tracker.CurrentSite is not null)
        {
            StatusText.Text = $"Tracking {_tracker.CurrentApp} · {_tracker.CurrentSite}";
        }
        else if (string.Equals(_tracker.CurrentApp, "Microsoft Edge", StringComparison.Ordinal))
        {
            StatusText.Text = _bridgeError ?? "Tracking Microsoft Edge · waiting for a website";
        }
        else
        {
            StatusText.Text = $"Tracking {_tracker.CurrentApp}";
        }
    }

    private void CheckLimits()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (!string.Equals(_limitStateDay, today, StringComparison.Ordinal))
        {
            _limitStateDay = today;
            _notifiedLimits.Clear();
            _closeRequestedProcessIds.Clear();
            _focusCloseRequestedProcessIds.Clear();
        }

        if (_tracker.Data.DailyLimitSeconds is int dailyLimit)
        {
            var dailyUsage = _tracker.GetTodayEntertainmentSeconds();
            var limitJustReached = CheckLimit(
                $"daily:{dailyLimit}",
                "daily entertainment time",
                dailyUsage,
                dailyLimit,
                closeApp: false);
            if (limitJustReached)
            {
                CloseOpenFocusApplications();
            }
        }

        if (_tracker.CurrentApp is { } appName &&
            UsageTracker.GetLimitSeconds(_tracker.Data.ApplicationLimitsSeconds, appName) is int appLimit)
        {
            var appUsage = _tracker.GetTodaySeconds(_tracker.Data.Applications, appName);
            CheckLimit(
                $"app:{appName}:{appLimit}",
                appName,
                appUsage,
                appLimit,
                closeApp: true,
                startDowntime: true);
            if (appUsage >= appLimit)
            {
                return;
            }
        }

        if (_tracker.CurrentSite is { } siteName &&
            UsageTracker.GetLimitSeconds(_tracker.Data.WebsiteLimitsSeconds, siteName) is int siteLimit)
        {
            CheckLimit(
                $"website:{siteName}:{siteLimit}",
                siteName,
                _tracker.GetTodaySeconds(_tracker.Data.Websites, siteName),
                siteLimit,
                closeApp: false,
                startDowntime: true);
        }
    }

    private bool CheckLimit(
        string key,
        string name,
        int usedSeconds,
        int limitSeconds,
        bool closeApp,
        bool startDowntime = false)
    {
        var remainingSeconds = limitSeconds - usedSeconds;
        if (remainingSeconds > 0)
        {
            return false;
        }

        if (startDowntime)
        {
            _tracker.StartLimitDowntime(key);
        }

        var firstReached = _notifiedLimits.Add($"reached:{key}");
        if (firstReached)
        {
            MessageBox.Show(
                this,
                startDowntime
                    ? $"Your limit for {name} has been reached. Limit settings are locked until midnight."
                    : $"Your limit for {name} has been reached. It resets at midnight.",
                "Screen Time limit reached",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        if (!closeApp ||
            _tracker.CurrentApp is null ||
            _tracker.CurrentProcessId is not int processId ||
            !_closeRequestedProcessIds.Add(processId))
        {
            return firstReached;
        }

        if (!_tracker.TryCloseCurrentApp())
        {
            MessageBox.Show(
                this,
                $"The limit for {name} has been reached, but Windows could not close the app automatically. Please close it manually.",
                "Could not close app",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return firstReached;
    }

    private void OpenLimits_Click(object sender, RoutedEventArgs e)
    {
        var limitsWindow = new LimitsWindow(_tracker) { Owner = this };
        if (limitsWindow.ShowDialog() == true)
        {
            _notifiedLimits.Clear();
            UpdateDashboard();
            UpdateStatus();
            UpdateLimitsButton();
        }
    }

    private void OpenWeeklyTrends_Click(object sender, RoutedEventArgs e)
    {
        new WeeklyTrendsWindow(_tracker) { Owner = this }.ShowDialog();
    }

    private void OpenBlockedSites_Click(object sender, RoutedEventArgs e)
    {
        new BlockedSitesWindow(_tracker) { Owner = this }.ShowDialog();
    }

    private void ToggleFocusMode_Click(object sender, RoutedEventArgs e)
    {
        var enabled = !_tracker.Data.FocusModeEnabled;
        try
        {
            _tracker.SetFocusModeEnabled(enabled);
            _focusCloseRequestedProcessIds.Clear();
            UpdateFocusModeControls();
            UpdateStatus();
            if (enabled)
            {
                CloseOpenFocusApplications();
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            UpdateFocusModeControls();
            MessageBox.Show(
                this,
                $"Focus mode could not be changed.\n\n{exception.Message}",
                "Focus Mode",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenFocusSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new FocusModeSettingsWindow(_tracker) { Owner = this };
        if (settings.ShowDialog() == true)
        {
            _focusCloseRequestedProcessIds.Clear();
            if (_tracker.Data.FocusModeEnabled || _tracker.HasReachedDailyEntertainmentLimit())
            {
                CloseOpenFocusApplications();
            }
        }
    }

    private void UpdateFocusModeControls()
    {
        FocusModeButton.Content = _tracker.Data.FocusModeEnabled
            ? "Turn focus mode off"
            : "Turn focus mode on";
        FocusModeDescription.Text = _tracker.Data.FocusModeEnabled
            ? "Entertainment blockers are active"
            : "Entertainment blockers are off";
    }

    private void EnforceFocusMode()
    {
        if (_tracker.CurrentApp is not { } appName ||
            _tracker.CurrentProcessName is not { } processName ||
            _tracker.CurrentProcessId is not int processId ||
            !_tracker.ShouldBlockFocusApplication(appName, processName) ||
            !_focusCloseRequestedProcessIds.Add(processId))
        {
            return;
        }

        if (!_tracker.TryCloseCurrentApp())
        {
            var blockingReason = _tracker.Data.FocusModeEnabled
                ? "Focus Mode"
                : "the daily entertainment limit";
            MessageBox.Show(
                this,
                $"{blockingReason} could not close {appName}. Please close it manually.",
                "Could not close app",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CloseOpenFocusApplications()
    {
        var failures = _tracker.CloseRunningFocusApplications(_focusCloseRequestedProcessIds);
        if (failures.Count > 0)
        {
            MessageBox.Show(
                this,
                $"Windows could not close these apps. Close them manually; unsaved-work prompts may be open:\n\n{string.Join(Environment.NewLine, failures)}",
                "Some apps are still open",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenDataTools_Click(object sender, RoutedEventArgs e)
    {
        var dataWindow = new DataManagementWindow(_tracker) { Owner = this };
        if (dataWindow.ShowDialog() == true)
        {
            _notifiedLimits.Clear();
            _closeRequestedProcessIds.Clear();
            _focusCloseRequestedProcessIds.Clear();
            UpdateDashboard();
            UpdateStatus();
            UpdateLimitsButton();
            UpdateFocusModeControls();
        }
    }

    private void UpdateLimitsButton()
    {
        if (_tracker.GetLimitDowntimeRemaining() is TimeSpan remaining)
        {
            var secondsRemaining = (int)Math.Ceiling(remaining.TotalSeconds);
            SetLimitsButton.Content = $"Limits locked · {FormatRemaining(secondsRemaining)}";
            SetLimitsButton.ToolTip = "Open the lock screen to unlock limits early.";
            SetLimitsButton.IsEnabled = true;
        }
        else
        {
            SetLimitsButton.Content = "Set limits";
            SetLimitsButton.ToolTip = null;
            SetLimitsButton.IsEnabled = true;
        }
    }

    private void OnStartupMenuItemClick(object? sender, EventArgs e)
    {
        try
        {
            StartupService.SetEnabled(_startupMenuItem.Checked);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _startupMenuItem.Checked = StartupService.IsEnabled();
            MessageBox.Show(
                this,
                $"The Windows startup setting could not be changed.\n\n{exception.Message}",
                "Screen Time Tracker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void ShowDashboard()
    {
        ShowInTaskbar = true;
        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        Activate();
        UpdateDashboard();
        UpdateStatus();
        UpdateLimitsButton();
    }

    private void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private static Dictionary<string, int> GetTodayUsage(
        Dictionary<string, Dictionary<string, int>> usage,
        string today)
    {
        return usage.TryGetValue(today, out var dailyUsage)
            ? dailyUsage
            : new Dictionary<string, int>();
    }

    private static ObservableCollection<UsageRow> BuildRows(
        Dictionary<string, int> usage,
        Dictionary<string, int> limits)
    {
        var entries = usage
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var maxSeconds = entries.Length == 0 ? 1 : entries[0].Value;

        return new ObservableCollection<UsageRow>(entries.Select(entry =>
        {
            var limit = UsageTracker.GetLimitSeconds(limits, entry.Key);
            var remaining = limit is int limitSeconds
                ? Math.Max(0, limitSeconds - entry.Value)
                : (int?)null;

            return new UsageRow(
                entry.Key,
                GetInitial(entry.Key),
                FormatDuration(entry.Value),
                remaining is int remainingSeconds
                    ? remainingSeconds == 0 ? "Limit reached" : $"{FormatRemaining(remainingSeconds)} left"
                    : "No limit",
                Math.Max(2, entry.Value * 100d / maxSeconds));
        }));
    }

    private static string GetInitial(string name)
    {
        var trimmedName = name.Trim();
        return trimmedName.Length == 0 ? "?" : trimmedName[..1].ToUpperInvariant();
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds}s";
        }

        var time = TimeSpan.FromSeconds(seconds);
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours}h {time.Minutes:D2}m";
        }

        return $"{time.Minutes}m";
    }

    private static string FormatRemaining(int seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        var parts = new List<string>(3);
        if (time.Hours > 0 || time.Days > 0)
        {
            parts.Add($"{(int)time.TotalHours}h");
        }

        if (time.Minutes > 0)
        {
            parts.Add($"{time.Minutes}m");
        }

        if (time.Seconds > 0 || parts.Count == 0)
        {
            parts.Add($"{time.Seconds}s");
        }

        return string.Join(" ", parts);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _edgeBridge.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        try
        {
            _tracker.Save();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show(
                $"The latest activity could not be saved.\n\n{exception.Message}",
                "Screen Time Tracker",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        Application.Current.Shutdown();
    }
}
