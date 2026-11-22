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

public partial class BlockedSitesWindow : Window
{
    private readonly UsageTracker _tracker;
    private readonly ObservableCollection<string> _blockedSites = [];

    public BlockedSitesWindow(UsageTracker tracker)
    {
        InitializeComponent();
        _tracker = tracker;
        foreach (var site in tracker.Data.BlockedWebsites.OrderBy(
                     host => host,
                     StringComparer.OrdinalIgnoreCase))
        {
            _blockedSites.Add(site);
        }

        BlockedSitesList.ItemsSource = _blockedSites;
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
                $"Blocked websites could not be saved.\n\n{exception.Message}",
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void UpdateEmptyState()
    {
        EmptyText.Visibility = _blockedSites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowValidationError(string message)
    {
        MessageBox.Show(this, message, "Check the website domain", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
