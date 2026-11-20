I’ll trace the website bridge and its place in the app, then explain the data flow in plain language.

Read 
Completed with input: {
  "path": "C:\\Users\\safwa\\source\\repos\\screentime\\Services\\EdgeBridge.cs"
}

Read 
Completed with input: {
  "path": "C:\\Users\\safwa\\source\\repos\\screentime\\edge-extension\\background.js"
}

Read 
Completed with input: {
  "path": "C:\\Users\\safwa\\source\\repos\\screentime\\edge-extension\\content.js"
}

Read , lines 1 to 160
Completed with input: {
  "path": "C:\\Users\\safwa\\source\\repos\\screentime\\Services\\UsageTracker.cs",
  "view_range": [
    1,
    160
  ]
}

Read 
Completed with input: {
  "path": "C:\\Users\\safwa\\source\\repos\\screentime\\README.md"
}

Search for `RecordSecond\(|GetSiteBlockReason|Save\(|SiteHeartbeatTimeout|SetTimer|Dispat...`

## In plain English

Think of the app as a **stopwatch that checks what’s in front of you once a second**.

### Tracking apps

Every second, the app asks Windows which application is currently in the foreground—the one you’re actively looking at. It adds that second to the app’s total for today. It doesn’t count every program that happens to be open in the background.

It ignores the tracker window itself, pauses after five minutes without keyboard or mouse activity, and filters out Windows system components. The main tracking logic is in `UsageTracker.cs`.

### Tracking websites

Website tracking needs the **Edge extension** as well as the desktop app:

1. The extension checks whether an Edge page is visible and focused. Every five seconds, it sends the page’s **domain name**—for example, `example.com`—to the desktop app.
2. The desktop app receives that message locally on your PC. `EdgeBridge.cs` handles the connection; the extension’s `content.js` and `background.js` send it.
3. When Edge is the foreground app and the extension has reported recently, the tracker adds time to that domain.

It records the domain, **not** the full web address, page title, or page contents. Website tracking works in Edge with the extension installed; the app alone can’t tell which website you’re visiting.

### Limits and saved data

The app compares tracked time with your daily limits. It asks Windows to close an app when it reaches its limit, and the Edge extension redirects an over-limit site to a blocked page. Limits reset each day.

Your history and settings are saved on your PC in `%LOCALAPPDATA%\ScreenTimeTracker\usage.json`. The app can stay running in the background from the system tray; choosing **Exit** stops tracking. More setup detail is in the `README.md`.