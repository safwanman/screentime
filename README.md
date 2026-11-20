# Screen Time Tracker

A Windows desktop dashboard that counts the foreground user app once per second and, with the Edge companion extension, the active website's domain. Windows shell and system-host processes are excluded from app tracking and app-limit selection; new user applications are recognized from their foreground window and executable location. Tracking pauses after five minutes without keyboard or mouse input and while the dashboard itself is in front.

## Run the desktop app

Install the .NET 8 SDK, open this folder in a terminal, and run:

```powershell
dotnet run
```

On first launch, the app adds itself to your current Windows user's startup apps and starts in the background on later sign-ins. Its tray icon lets you reopen the dashboard, toggle **Start with Windows**, or exit. Closing or minimizing the dashboard hides it to the tray; it keeps tracking until you choose **Exit** from the tray menu.

The app saves history and limits to `%LOCALAPPDATA%\ScreenTimeTracker\usage.json`.
It checks activity once per second to keep limits responsive, but refreshes the dashboard only every five seconds while it is visible. History is saved every 30 seconds and again when you exit, reducing background work and disk writes; an unexpected shutdown could lose up to about 30 seconds of recent usage.

## Set screen-time limits

Select **Set limits** in the dashboard to set an optional overall daily cap, or add daily limits for individual apps and website domains. Choose an app from the list of currently running apps, or enter a website domain. Set hours, minutes, and seconds separately: type a number into each field or scroll over it to adjust. Hours range from 0 to 24; minutes and seconds range from 0 to 59. Limits reset at midnight.

At an app or overall daily limit, the tracker requests Windows to close the active app; an app's unsaved-work prompt may delay closing. With the Edge extension installed, an over-limit site (or any site after the overall limit is reached) is redirected to a local blocked page. Website limits require the desktop app and Edge extension to be running.

## Enable Edge website tracking

1. Start Screen Time Tracker and leave it running.
2. In Edge, open `edge://extensions` and enable **Developer mode**.
3. Choose **Load unpacked** and select this project's `edge-extension` folder.
4. If the extension was already installed, reload it after updating the project files.
5. Open or reload a website in Edge. Its domain will appear in the dashboard while that tab and Edge are active.

The extension sends only the focused, visible tab's hostname to the desktop app over loopback and stops its periodic check for hidden or unfocused tabs. Full URLs, page titles, and page content are not collected. App history stays on this PC.
