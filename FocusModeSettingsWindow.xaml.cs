using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ScreenTimeTracker.Services;

using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ScreenTimeTracker;

public partial class FocusModeSettingsWindow : Window
{
    private readonly UsageTracker _tracker;
    private readonly ObservableCollection<string> _apps = [];
    private readonly ObservableCollection<string> _websites = [];

    public FocusModeSettingsWindow(UsageTracker tracker)
    {
        InitializeComponent();
        _tracker = tracker;

        foreach (var app in tracker.Data.FocusApplications.OrderBy(
                     name => name,
                     StringComparer.OrdinalIgnoreCase))
        {
            _apps.Add(app);
        }

        foreach (var website in tracker.Data.FocusWebsites.OrderBy(
                     host => host,
                     StringComparer.OrdinalIgnoreCase))
        {
            _websites.Add(website);
        }

        AppsList.ItemsSource = _apps;
        WebsitesList.ItemsSource = _websites;
        UpdateEmptyStates();
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var app = AppInput.Text.Trim();
        if (app.Length == 0 || app.Length > 128 || app.Contains('/') || app.Contains('\\'))
        {
            ShowValidationError("Enter an app name or process name, such as Spotify or steam.exe.");
            return;
        }

        if (_apps.Contains(app, StringComparer.OrdinalIgnoreCase))
        {
            ShowValidationError("That app is already on the focus list.");
            return;
        }

        _apps.Add(app);
        AppInput.Clear();
        UpdateEmptyStates();
    }

    private void AddWebsite_Click(object sender, RoutedEventArgs e)
    {
        var host = WebsiteInput.Text.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        if (host.Contains('/') ||
            host.Contains(':') ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            ShowValidationError("Enter a valid website domain only, such as example.com.");
            return;
        }

        if (_websites.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            ShowValidationError("That domain is already on the focus list.");
            return;
        }

        _websites.Add(host);
        WebsiteInput.Clear();
        UpdateEmptyStates();
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string app })
        {
            _apps.Remove(app);
            UpdateEmptyStates();
        }
    }

    private void RemoveWebsite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string website })
        {
            _websites.Remove(website);
            UpdateEmptyStates();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _tracker.UpdateFocusBlocklists(_apps, _websites);
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            ShowValidationError(exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                $"Focus lists could not be saved.\n\n{exception.Message}",
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void UpdateEmptyStates()
    {
        AppsEmptyText.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WebsitesEmptyText.Visibility = _websites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowValidationError(string message)
    {
        MessageBox.Show(this, message, "Check the focus list", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
