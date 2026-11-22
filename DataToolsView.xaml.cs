using System.IO;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using Microsoft.Win32;
using ScreenTimeTracker.Services;

using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Window = System.Windows.Window;

namespace ScreenTimeTracker;

public partial class DataToolsView : UserControl
{
    private readonly UsageTracker _tracker;

    public DataToolsView(UsageTracker tracker)
    {
        InitializeComponent();
        _tracker = tracker;
    }

    public event EventHandler? DataCleared;

    private void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".csv",
            FileName = "screen-time-history.csv",
            Filter = "CSV files (*.csv)|*.csv"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            _tracker.ExportHistoryCsv(dialog.FileName);
            MessageBox.Show(
                Window.GetWindow(this),
                "Your usage history was exported.",
                "Export complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"Usage history could not be exported.\n\n{exception.Message}",
                "Export failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BackupData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".json",
            FileName = "screen-time-backup.json",
            Filter = "JSON files (*.json)|*.json"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            _tracker.CreateBackup(dialog.FileName);
            MessageBox.Show(
                Window.GetWindow(this),
                "Your usage history and settings were backed up.",
                "Backup complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"The backup could not be created.\n\n{exception.Message}",
                "Backup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ClearAllData_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            Window.GetWindow(this),
            "This permanently clears all tracked app and website history, all limits, focus lists and mode, and the current lockout. This cannot be undone. Continue?",
            "Clear all Screen Time data?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _tracker.ClearAllData();
            MessageBox.Show(
                Window.GetWindow(this),
                "All Screen Time history, limits, focus settings, and lockout data were cleared.",
                "Data cleared",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DataCleared?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"Screen Time data could not be cleared.\n\n{exception.Message}",
                "Clear failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

}