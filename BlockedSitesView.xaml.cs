using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ScreenTimeTracker.Services;

using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Window = System.Windows.Window;

namespace ScreenTimeTracker;

public partial class BlockedSitesView : UserControl
{
    private readonly UsageTracker _tracker;
    private readonly ObservableCollection<string> _blockedSites = [];

    public BlockedSitesView(UsageTracker tracker)
    {
        InitializeComponent();
        _tracker = tracker;
        BlockedSitesList.ItemsSource = _blockedSites;
        LoadSavedSites();
    }

    public event EventHandler? SettingsSaved;

    public void ReloadFromTracker()
    {
        LoadSavedSites();
    }

    private void LoadSavedSites()
    {
        _blockedSites.Clear();
        DomainInput.Clear();
        foreach (var site in _tracker.Data.BlockedWebsites.OrderBy(
                     host => host,
                     StringComparer.OrdinalIgnoreCase))
        {
            _blockedSites.Add(site);
        }

        UpdateEmptyState();
    }

    private void AddSite_Click(object sender, RoutedEventArgs e)
    {
        var host = DomainInput.Text.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        if (host.Contains('/') ||
            host.Contains(':') ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            ShowValidationError("Enter a valid website domain only, such as example.com. Do not include a path or web address.");
            return;
        }

        if (_blockedSites.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            ShowValidationError("That domain is already on the blocked list.");
            return;
        }

        _blockedSites.Add(host);
        DomainInput.Clear();
        UpdateEmptyState();
    }

    private void RemoveSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string host })
        {
            _blockedSites.Remove(host);
            UpdateEmptyState();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _tracker.UpdateBlockedWebsites(_blockedSites);
            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException exception)
        {
            ShowValidationError(exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"Blocked websites could not be saved.\n\n{exception.Message}",
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        LoadSavedSites();
    }

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = _blockedSites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowValidationError(string message)
    {
        MessageBox.Show(Window.GetWindow(this), message, "Check the website domain", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
