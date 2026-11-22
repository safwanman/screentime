using System.IO;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using ScreenTimeTracker.Services;

using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Window = System.Windows.Window;

namespace ScreenTimeTracker;

public partial class SettingsView : UserControl
{
    private readonly UsageTracker _tracker;

    public SettingsView(UsageTracker tracker)
    {
        InitializeComponent();
        _tracker = tracker;
        Refresh();
    }

    public event EventHandler? ThemeChanged;

    public void Refresh()
    {
        var isDarkMode = _tracker.Data.DarkModeEnabled;
        AppearanceText.Text = isDarkMode ? "Dark mode is on." : "Light mode is on.";
        ThemeButton.Content = isDarkMode ? "Switch to light mode" : "Switch to dark mode";
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _tracker.SetDarkModeEnabled(!_tracker.Data.DarkModeEnabled);
            Refresh();
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Refresh();
            MessageBox.Show(
                Window.GetWindow(this),
                $"The appearance setting could not be saved.\n\n{exception.Message}",
                "Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
