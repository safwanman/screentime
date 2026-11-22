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

## Set entertainment and screen-time limits

The dashboard's daily total now counts entertainment only: usage matching the editable apps and websites in **Focus lists**. Website time is counted separately from Edge app time to avoid double-counting. Your previous optional overall daily cap is now a **daily entertainment limit**; it resets at midnight. Add individual daily limits for any app or website domain from **Set limits**. Choose an app from the list of currently running apps, or enter a website domain. Set hours, minutes, and seconds separately: type a number into each field or scroll over it to adjust. Hours range from 0 to 24; minutes and seconds range from 0 to 59.

When the daily entertainment limit is reached, the tracker asks Windows to close listed entertainment apps and blocks listed entertainment websites in Edge until midnight. An app's unsaved-work prompt may delay closing. Apps and websites outside the Focus lists remain available unless they have their own individual limit. Domains added to **Blocked sites** are always blocked.

Short-form rules block TikTok entirely, plus YouTube Shorts, Instagram Reels, Facebook Reels, and Snapchat Spotlight routes. This only recognizes those known site paths; it cannot detect every short video embedded on arbitrary websites. The extension checks the page path locally and sends only the site hostname and a short-form yes/no flag to the desktop app. Full paths and page content are not sent or saved. Blocking requires the desktop app and Edge extension to be running.

Use **Focus mode** on the dashboard to block the same entertainment list immediately, before the daily limit is reached. The starter list covers streaming, social, gaming, and music services. Listed apps are asked to close gracefully when Focus Mode starts or when they become active; Windows may still show an unsaved-work prompt. Listed website domains and their subdomains are redirected to the local blocked page in Edge. Focus Mode stays on until you turn it off, including across app restarts. Add or remove entries with **Focus lists**. App matching uses the app's displayed name or process name; there is no universal way to identify every entertainment app or website automatically.

When an individual app or website limit is reached, limit settings are locked until local midnight. Open the locked limits screen and choose **Unlock limits now** to unlock early after confirmation. The reached limit remains enforced; the same already-reached limit will not immediately relock the editor that day. A different limit reaching its cap can start a new lockout. The lockout is saved locally and remains in effect if the tracker is restarted.

Choose **Weekly trends** to compare app time from the last seven calendar days (including today) with the previous seven days. Website time is shown separately as a subset of Edge app time, not added twice.

Choose **Data tools** to export usage history as CSV, back up history and settings as JSON, or clear all history, limits, and lockout data. Clearing requires confirmation and cannot be undone; make a backup first if you may want to keep the data.

## Enable Edge website tracking

1. Start Screen Time Tracker and leave it running.
2. In Edge, open `edge://extensions` and enable **Developer mode**.
3. Choose **Load unpacked** and select this project's `edge-extension` folder.
4. If the extension was already installed, reload it after updating the project files.
5. Open or reload a website in Edge. Its domain will appear in the dashboard while that tab and Edge are active.

The extension sends only the focused, visible tab's hostname to the desktop app over loopback and stops its periodic check for hidden or unfocused tabs. Full URLs, page titles, and page content are not collected. App history stays on this PC.
