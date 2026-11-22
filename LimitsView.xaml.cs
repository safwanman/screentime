using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;

using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using Window = System.Windows.Window;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ScreenTimeTracker;

public partial class LimitsView : UserControl
{
    private readonly UsageTracker _tracker;
    private readonly ObservableCollection<LimitEntry> _entries = [];
    private readonly DispatcherTimer _cooldownTimer;

    public LimitsView(UsageTracker tracker)
    {
        InitializeComponent();
        _tracker = tracker;
        LimitItems.ItemsSource = _entries;
        RefreshRunningApps();
        UpdateTargetInputVisibility();

        LoadSavedLimits();

        _cooldownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _cooldownTimer.Tick += (_, _) => UpdateCooldownState();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                UpdateCooldownState();
                _cooldownTimer.Start();
            }
            else
            {
                _cooldownTimer.Stop();
            }
        };
        UpdateCooldownState();
    }

    public event EventHandler? LimitsSaved;

    public void ReloadFromTracker()
    {
        LoadSavedLimits();
        UpdateCooldownState();
    }

    private void LoadSavedLimits()
    {
        _entries.Clear();
        DailyHoursInput.Clear();
        DailyMinutesInput.Clear();
        DailySecondsInput.Clear();
        TargetInput.Clear();
        AppPicker.SelectedItem = null;
        HoursInput.Clear();
        MinutesInput.Clear();
        SecondsInput.Clear();

        if (_tracker.Data.DailyLimitSeconds is int dailyLimit)
        {
            SetDurationFields(dailyLimit, DailyHoursInput, DailyMinutesInput, DailySecondsInput);
        }

        AddEntries("App", _tracker.Data.ApplicationLimitsSeconds);
        AddEntries("Website", _tracker.Data.WebsiteLimitsSeconds);
        UpdateEmptyState();
    }

    private void AddEntries(string type, Dictionary<string, int> limits)
    {
        foreach (var limit in limits.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            _entries.Add(new LimitEntry
            {
                Type = type,
                Name = limit.Key,
                Hours = (limit.Value / 3600).ToString(CultureInfo.InvariantCulture),
                Minutes = (limit.Value % 3600 / 60).ToString(CultureInfo.InvariantCulture),
                Seconds = (limit.Value % 60).ToString(CultureInfo.InvariantCulture)
            });
        }
    }

    private void AddLimit_Click(object sender, RoutedEventArgs e)
    {
        var type = (LimitTypePicker.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var target = type == "App"
            ? AppPicker.SelectedItem?.ToString() ?? ""
            : TargetInput.Text.Trim();

        if (type == "App")
        {
            RefreshRunningApps();
            target = AppPicker.SelectedItem?.ToString() ?? "";
        }

        if (type is null ||
            target.Length == 0 ||
            !TryReadDuration(HoursInput.Text, MinutesInput.Text, SecondsInput.Text, out var durationSeconds))
        {
            var message = type == "App" && target.Length == 0
                ? "No running app is selected. Choose an app that is currently open."
                : "Enter a target and a limit greater than zero, up to 24 hours. Hours, minutes, and seconds must be numeric; minutes and seconds must be 0–59.";
            ShowValidationError(message);
            return;
        }

        if (type == "Website")
        {
            target = NormalizeWebsite(target);
            if (Uri.CheckHostName(target) == UriHostNameType.Unknown || target.Contains('/'))
            {
                ShowValidationError("Enter a website domain only, such as example.com.");
                return;
            }
        }
        var existing = _entries.FirstOrDefault(entry =>
            entry.Type == type &&
            string.Equals(entry.Name, target, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _entries.Add(new LimitEntry
            {
                Type = type,
                Name = target,
                Hours = (durationSeconds / 3600).ToString(CultureInfo.InvariantCulture),
                Minutes = (durationSeconds % 3600 / 60).ToString(CultureInfo.InvariantCulture),
                Seconds = (durationSeconds % 60).ToString(CultureInfo.InvariantCulture)
            });
        }
        else
        {
            existing.Hours = (durationSeconds / 3600).ToString(CultureInfo.InvariantCulture);
            existing.Minutes = (durationSeconds % 3600 / 60).ToString(CultureInfo.InvariantCulture);
            existing.Seconds = (durationSeconds % 60).ToString(CultureInfo.InvariantCulture);
            LimitItems.Items.Refresh();
        }

        TargetInput.Clear();
        AppPicker.SelectedItem = null;
        HoursInput.Clear();
        MinutesInput.Clear();
        SecondsInput.Clear();
        UpdateEmptyState();
    }

    private void RemoveLimit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LimitEntry entry })
        {
            _entries.Remove(entry);
            UpdateEmptyState();
        }
    }

    private void SaveLimits_Click(object sender, RoutedEventArgs e)
    {
        int? dailyLimit = null;
        if (!string.IsNullOrWhiteSpace(DailyHoursInput.Text) ||
            !string.IsNullOrWhiteSpace(DailyMinutesInput.Text) ||
            !string.IsNullOrWhiteSpace(DailySecondsInput.Text))
        {
            if (!TryReadDuration(
                    DailyHoursInput.Text,
                    DailyMinutesInput.Text,
                    DailySecondsInput.Text,
                    out var dailySeconds))
            {
                ShowValidationError("The daily entertainment limit must be greater than zero, up to 24 hours. Hours, minutes, and seconds must be numeric; minutes and seconds must be 0–59.");
                return;
            }

            dailyLimit = dailySeconds;
        }

        var applicationLimits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var websiteLimits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            if (!TryReadDuration(entry.Hours, entry.Minutes, entry.Seconds, out var durationSeconds))
            {
                ShowValidationError($"The limit for {entry.Name} must be greater than zero, up to 24 hours. Hours, minutes, and seconds must be numeric; minutes and seconds must be 0–59.");
                return;
            }

            var destination = entry.Type == "Website" ? websiteLimits : applicationLimits;
            destination[entry.Name] = durationSeconds;
        }

        try
        {
            _tracker.UpdateLimits(dailyLimit, applicationLimits, websiteLimits);
        }
        catch (InvalidOperationException exception)
        {
            UpdateCooldownState();
            ShowValidationError(exception.Message);
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"Limits could not be saved.\n\n{exception.Message}",
                "Screen Time Tracker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        LimitsSaved?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCooldownState()
    {
        if (_tracker.GetLimitDowntimeRemaining() is TimeSpan remaining)
        {
            var totalSeconds = (int)Math.Ceiling(remaining.TotalSeconds);
            var hours = totalSeconds / 3600;
            var minutes = totalSeconds % 3600 / 60;
            var seconds = totalSeconds % 60;
            var remainingText = hours > 0
                ? $"{hours}h {minutes:D2}m {seconds:D2}s"
                : $"{minutes}m {seconds:D2}s";

            CooldownText.Text = $"Limit changes are locked until midnight. Time remaining: {remainingText}.";
            CooldownOverlay.Visibility = Visibility.Visible;
            SaveLimitsButton.IsEnabled = false;
        }
        else
        {
            CooldownOverlay.Visibility = Visibility.Collapsed;
            SaveLimitsButton.IsEnabled = true;
        }
    }

    private void UnlockLimits_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            Window.GetWindow(this),
            "Unlock the limit editor now? The limit that was reached will still be enforced, but you can change or remove it.",
            "Unlock limits early?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _tracker.UnlockLimitSettings();
            UpdateCooldownState();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"The limits could not be unlocked.\n\n{exception.Message}",
                "Unlock failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        LoadSavedLimits();
    }

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LimitTypePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateTargetInputVisibility();
        }
    }

    private void AppPicker_DropDownOpened(object sender, EventArgs e)
    {
        RefreshRunningApps();
    }

    private void RefreshRunningApps()
    {
        try
        {
            var selectedApp = AppPicker.SelectedItem?.ToString();
            var runningApps = UsageTracker.GetRunningAppNames();
            AppPicker.ItemsSource = runningApps;
            AppPicker.SelectedItem = selectedApp is not null &&
                runningApps.Contains(selectedApp, StringComparer.OrdinalIgnoreCase)
                    ? runningApps.First(app => string.Equals(app, selectedApp, StringComparison.OrdinalIgnoreCase))
                    : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"The list of running apps could not be loaded.\n\n{exception.Message}",
                "Running apps unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void UpdateTargetInputVisibility()
    {
        var isApp = (LimitTypePicker.SelectedItem as ComboBoxItem)?.Content?.ToString() == "App";
        LimitTypePicker.IsDropDownOpen = false;
        AppPicker.IsDropDownOpen = false;
        AppPicker.Visibility = isApp ? Visibility.Visible : Visibility.Collapsed;
        TargetInput.Visibility = isApp ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowValidationError(string message)
    {
        MessageBox.Show(Window.GetWindow(this), message, "Check your limits", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static bool TryReadDuration(
        string hoursText,
        string minutesText,
        string secondsText,
        out int durationSeconds)
    {
        durationSeconds = 0;
        if (!TryReadComponent(hoursText, out var hours) ||
            !TryReadComponent(minutesText, out var minutes) ||
            !TryReadComponent(secondsText, out var seconds) ||
            hours > 24 ||
            minutes > 59 ||
            seconds > 59)
        {
            return false;
        }

        var totalSeconds = (long)hours * 3600 + (long)minutes * 60 + seconds;
        if (totalSeconds is < 1 or > 86400)
        {
            return false;
        }

        durationSeconds = (int)totalSeconds;
        return true;
    }

    private static bool TryReadComponent(string text, out int value)
    {
        value = 0;
        return string.IsNullOrWhiteSpace(text) ||
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static void SetDurationFields(int totalSeconds, TextBox hours, TextBox minutes, TextBox seconds)
    {
        hours.Text = (totalSeconds / 3600).ToString(CultureInfo.InvariantCulture);
        minutes.Text = (totalSeconds % 3600 / 60).ToString(CultureInfo.InvariantCulture);
        seconds.Text = (totalSeconds % 60).ToString(CultureInfo.InvariantCulture);
    }

    private void DurationField_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (!int.TryParse(textBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            value = 0;
        }

        var maximum = string.Equals(textBox.Tag?.ToString(), "Hours", StringComparison.Ordinal)
            ? 24
            : 59;
        textBox.Text = Math.Clamp(value + Math.Sign(e.Delta), 0, maximum)
            .ToString(CultureInfo.InvariantCulture);
        e.Handled = true;
    }

    private void DurationField_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => !char.IsDigit(character));
    }

    private static string NormalizeWebsite(string host)
    {
        var normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        return normalizedHost.StartsWith("www.", StringComparison.Ordinal)
            ? normalizedHost[4..]
            : normalizedHost;
    }
}
