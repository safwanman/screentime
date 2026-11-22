using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
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
    private LimitsView? _limitsView;
    private FocusListsView? _focusListsView;
    private BlockedSitesView? _blockedSitesView;
    private WeeklyTrendsView? _weeklyTrendsView;
    private DataToolsView? _dataToolsView;
    private SettingsView? _settingsView;
    private readonly Button[] _navigationButtons;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.ToolStripMenuItem _startupMenuItem;
    private readonly HashSet<string> _notifiedLimits = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _closeRequestedProcessIds = [];
    private readonly HashSet<int> _focusCloseRequestedProcessIds = [];
    private int _dashboardRefreshTicks;
    private int _currentSectionIndex;
    private bool _exitRequested;
    private bool _viewsInitialized;
    private string? _limitStateDay;
    private string? _bridgeError;

    public MainWindow(bool startInBackground = false)
    {
        InitializeComponent();
        _navigationButtons =
        [
            DashboardNavigationButton,
            LimitsNavigationButton,
            FocusListsNavigationButton,
            BlockedSitesNavigationButton,
            WeeklyTrendsNavigationButton,
            DataToolsNavigationButton,
            SettingsNavigationButton
        ];

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
        _viewsInitialized = true;
        ApplyTheme(_tracker.Data.DarkModeEnabled);
        NavigateToSection(0);
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

    private void UpdateFocusModeControls()
    {
        FocusModeButton.Content = _tracker.Data.FocusModeEnabled
            ? "Turn focus mode off"
            : "Turn focus mode on";
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

    private void OpenNavigation_Click(object sender, RoutedEventArgs e)
    {
        if (NavigationOverlay.Visibility == Visibility.Visible)
        {
            CloseNavigation();
            return;
        }

        NavigationOverlay.Visibility = Visibility.Visible;
        NavigationDrawerTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(-298, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        NavigationScrim.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void CloseNavigation_Click(object sender, RoutedEventArgs e)
    {
        CloseNavigation();
    }

    private void NavigationScrim_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        CloseNavigation();
    }

    private void NavigationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sectionName })
        {
            var selectedIndex = sectionName switch
            {
                "Dashboard" => 0,
                "Limits" => 1,
                "Focus lists" => 2,
                "Blocked sites" => 3,
                "Weekly trends" => 4,
                "Data tools" => 5,
                "Settings" => 6,
                _ => -1
            };

            if (selectedIndex >= 0)
            {
                NavigateToSection(selectedIndex);
            }
        }

        CloseNavigation();
    }

    private void NavigateToSection(int selectedIndex)
    {
        if (!_viewsInitialized)
        {
            return;
        }

        _currentSectionIndex = selectedIndex;
        var isDashboard = selectedIndex == 0;
        DashboardContent.Visibility = isDashboard ? Visibility.Visible : Visibility.Collapsed;
        PageContent.Visibility = isDashboard ? Visibility.Collapsed : Visibility.Visible;
        object? selectedView = selectedIndex switch
        {
            1 => GetLimitsView(),
            2 => GetFocusListsView(),
            3 => GetBlockedSitesView(),
            4 => GetWeeklyTrendsView(),
            5 => GetDataToolsView(),
            6 => GetSettingsView(),
            _ => null
        };
        PageContent.Content = selectedView;

        if (selectedView is WeeklyTrendsView weeklyTrendsView)
        {
            weeklyTrendsView.Refresh();
        }

        if (selectedView is SettingsView settingsView)
        {
            settingsView.Refresh();
        }

        UpdateNavigationSelection();
    }

    private LimitsView GetLimitsView()
    {
        if (_limitsView is not null)
        {
            return _limitsView;
        }

        _limitsView = new LimitsView(_tracker);
        _limitsView.LimitsSaved += (_, _) =>
        {
            _notifiedLimits.Clear();
            UpdateDashboard();
            UpdateStatus();
        };
        return _limitsView;
    }

    private FocusListsView GetFocusListsView()
    {
        if (_focusListsView is not null)
        {
            return _focusListsView;
        }

        _focusListsView = new FocusListsView(_tracker);
        _focusListsView.SettingsSaved += (_, _) =>
        {
            _focusCloseRequestedProcessIds.Clear();
            UpdateDashboard();
            if (_tracker.Data.FocusModeEnabled || _tracker.HasReachedDailyEntertainmentLimit())
            {
                CloseOpenFocusApplications();
            }
        };
        return _focusListsView;
    }

    private BlockedSitesView GetBlockedSitesView()
    {
        if (_blockedSitesView is not null)
        {
            return _blockedSitesView;
        }

        _blockedSitesView = new BlockedSitesView(_tracker);
        _blockedSitesView.SettingsSaved += (_, _) => UpdateStatus();
        return _blockedSitesView;
    }

    private WeeklyTrendsView GetWeeklyTrendsView()
    {
        return _weeklyTrendsView ??= new WeeklyTrendsView(_tracker);
    }

    private SettingsView GetSettingsView()
    {
        if (_settingsView is not null)
        {
            return _settingsView;
        }

        _settingsView = new SettingsView(_tracker);
        _settingsView.ThemeChanged += (_, _) => ApplyTheme(_tracker.Data.DarkModeEnabled);
        return _settingsView;
    }

    private DataToolsView GetDataToolsView()
    {
        if (_dataToolsView is not null)
        {
            return _dataToolsView;
        }

        _dataToolsView = new DataToolsView(_tracker);
        _dataToolsView.DataCleared += (_, _) =>
        {
            _notifiedLimits.Clear();
            _closeRequestedProcessIds.Clear();
            _focusCloseRequestedProcessIds.Clear();
            _limitsView?.ReloadFromTracker();
            _focusListsView?.ReloadFromTracker();
            _blockedSitesView?.ReloadFromTracker();
            UpdateDashboard();
            UpdateStatus();
            UpdateFocusModeControls();
            _settingsView?.Refresh();
            ApplyTheme(_tracker.Data.DarkModeEnabled);
        };
        return _dataToolsView;
    }

    private void UpdateNavigationSelection()
    {
        for (var index = 0; index < _navigationButtons.Length; index++)
        {
            var isSelected = index == _currentSectionIndex;
            _navigationButtons[index].Background = isSelected
                ? (MediaBrush)FindResource("Theme.AccentSurface")
                : MediaBrushes.Transparent;
            _navigationButtons[index].Foreground = isSelected
                ? (MediaBrush)FindResource("Theme.Accent")
                : (MediaBrush)FindResource("Theme.MutedStrong");
            _navigationButtons[index].FontWeight = isSelected
                ? FontWeights.SemiBold
                : FontWeights.Medium;
        }
    }

    private void ApplyTheme(bool darkMode)
    {
        SetThemeBrush("Theme.Background", darkMode ? "#1D2420" : "#F5F4EF");
        SetThemeBrush("Theme.Surface", darkMode ? "#27332D" : "#FBFAF6");
        SetThemeBrush("Theme.SurfaceAlt", darkMode ? "#303A33" : "#F0F1E9");
        SetThemeBrush("Theme.Input", darkMode ? "#222A25" : "#FFFEFA");
        SetThemeBrush("Theme.Text", darkMode ? "#F4F3ED" : "#242A2C");
        SetThemeBrush("Theme.Muted", darkMode ? "#B4BDB2" : "#6D7471");
        SetThemeBrush("Theme.MutedStrong", darkMode ? "#C5CEC3" : "#5D6860");
        SetThemeBrush("Theme.Subtle", darkMode ? "#9AA69A" : "#80877D");
        SetThemeBrush("Theme.Border", darkMode ? "#39443D" : "#DFDFD6");
        SetThemeBrush("Theme.BorderStrong", darkMode ? "#48534A" : "#D4D8CF");
        SetThemeBrush("Theme.InputBorder", darkMode ? "#4A574D" : "#D6DBD1");
        SetThemeBrush("Theme.Accent", darkMode ? "#B8D5B9" : "#4A7257");
        SetThemeBrush("Theme.AccentText", darkMode ? "#C6DFC5" : "#4A7257");
        SetThemeBrush("Theme.AccentSurface", darkMode ? "#34443A" : "#E8EEE1");
        SetThemeBrush("Theme.AccentBorder", darkMode ? "#506852" : "#DDE6D8");
        SetThemeBrush("Theme.ProgressBackground", darkMode ? "#39433D" : "#E9EBE3");
        SetThemeBrush("Theme.Progress", darkMode ? "#A8C6A7" : "#7D9E7D");
        SetThemeBrush("Theme.Danger", darkMode ? "#F08B8B" : "#A34848");
        SetThemeBrush("Theme.Good", darkMode ? "#83B58D" : "#86AA83");
        SetThemeBrush("Theme.Website", darkMode ? "#90BEA2" : "#7D9E7D");
        SetThemeBrush("Theme.Separator", darkMode ? "#39443D" : "#DFDFD6");
        SetThemeBrush("Theme.ButtonSurface", darkMode ? "#343E36" : "#EBEEE6");
        SetThemeBrush("Theme.Slate", darkMode ? "#D8E0D6" : "#35483B");
        SetThemeBrush("Theme.OnAccent", darkMode ? "#1D2420" : "#FFFFFF");
        SetThemeBrush("Theme.Overlay", darkMode ? "#B3000000" : "#660D1424");
        SetThemeBrush("Theme.LockOverlay", darkMode ? "#CC1D2420" : "#CCF5F4EF");
        UpdateNavigationSelection();
    }

    private static void SetThemeBrush(string key, string hexColor)
    {
        var offset = hexColor.Length == 9 ? 3 : 1;
        var alpha = hexColor.Length == 9
            ? Convert.ToByte(hexColor.Substring(1, 2), 16)
            : byte.MaxValue;
        var red = Convert.ToByte(hexColor.Substring(offset, 2), 16);
        var green = Convert.ToByte(hexColor.Substring(offset + 2, 2), 16);
        var blue = Convert.ToByte(hexColor.Substring(offset + 4, 2), 16);
        Application.Current.Resources[key] = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(alpha, red, green, blue));
    }

    private void CloseNavigation()
    {
        if (NavigationOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(150);
        var drawerAnimation = new DoubleAnimation(
            NavigationDrawerTransform.X,
            -298,
            duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        drawerAnimation.Completed += (_, _) => NavigationOverlay.Visibility = Visibility.Collapsed;
        NavigationDrawerTransform.BeginAnimation(TranslateTransform.XProperty, drawerAnimation);
        NavigationScrim.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(NavigationScrim.Opacity, 0, duration));
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
        NavigateToSection(0);
        CloseNavigation();
        UpdateDashboard();
        UpdateStatus();
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
