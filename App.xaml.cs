using System.IO;
using System.Security;
using System.Windows;
using ScreenTimeTracker.Services;

using Application = System.Windows.Application;
using StartupEventArgs = System.Windows.StartupEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ScreenTimeTracker;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            try
            {
                StartupService.InitializeDefault();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                MessageBox.Show(
                    $"The Windows startup setting could not be configured.\n\n{exception.Message}",
                    "Screen Time Tracker",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            var startInBackground = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            var window = new MainWindow(startInBackground);
            MainWindow = window;
            window.Show();
            if (startInBackground)
            {
                window.Hide();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Screen Time Tracker could not start.\n\n{exception.Message}",
                "Screen Time Tracker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
