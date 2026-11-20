using System.IO;
using Microsoft.Win32;

namespace ScreenTimeTracker.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsKeyPath = @"Software\ScreenTimeTracker";
    private const string AppValueName = "ScreenTimeTracker";
    private const string StartupPreferenceName = "StartWithWindows";

    public static bool IsEnabled()
    {
        return Registry.GetValue(
            $@"HKEY_CURRENT_USER\{RunKeyPath}",
            AppValueName,
            null) is string;
    }

    public static void InitializeDefault()
    {
        using var settings = Registry.CurrentUser.CreateSubKey(SettingsKeyPath)
            ?? throw new IOException("Could not open the current-user application settings.");

        var savedPreference = settings.GetValue(StartupPreferenceName);
        if (savedPreference is null)
        {
            SetEnabled(true);
            return;
        }

        if (savedPreference is int enabled && enabled != 0)
        {
            SetRunEntry();
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var settings = Registry.CurrentUser.CreateSubKey(SettingsKeyPath)
            ?? throw new IOException("Could not open the current-user application settings.");
        settings.SetValue(StartupPreferenceName, enabled ? 1 : 0, RegistryValueKind.DWord);

        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new IOException("Could not open the current-user Windows startup settings.");

        if (enabled)
        {
            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The application executable path is unavailable.");
            runKey.SetValue(
                AppValueName,
                $"\"{Path.GetFullPath(executablePath)}\" --background",
                RegistryValueKind.String);
        }
        else
        {
            runKey.DeleteValue(AppValueName, false);
        }
    }

    private static void SetRunEntry()
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new IOException("Could not open the current-user Windows startup settings.");
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application executable path is unavailable.");
        runKey.SetValue(
            AppValueName,
            $"\"{Path.GetFullPath(executablePath)}\" --background",
            RegistryValueKind.String);
    }
}
