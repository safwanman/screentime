using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;

namespace ScreenTimeTracker;

public partial class WeeklyTrendsView : UserControl
{
    private readonly UsageTracker _tracker;

    public WeeklyTrendsView(UsageTracker tracker)
    {
        InitializeComponent();
        _tracker = tracker;
        Refresh();
    }

    public void Refresh()
    {
        var tracker = _tracker;
        var today = DateTime.Today;
        var currentStart = today.AddDays(-6);
        var previousStart = today.AddDays(-13);
        var currentTotal = GetRangeTotal(tracker.Data.Applications, currentStart, today);
        var previousTotal = GetRangeTotal(
            tracker.Data.Applications,
            previousStart,
            currentStart.AddDays(-1));

        CurrentTotalText.Text = FormatDuration(currentTotal);
        PreviousTotalText.Text = FormatDuration(previousTotal);
        ChangeText.Text = FormatChange(currentTotal, previousTotal);
        PeriodText.Text = $"{currentStart:d} – {today:d}";

        var dailyUsage = Enumerable.Range(0, 7)
            .Select(offset => today.AddDays(offset - 6))
            .Select(day => new
            {
                Date = day,
                AppSeconds = GetDayTotal(tracker.Data.Applications, day),
                WebsiteSeconds = GetDayTotal(tracker.Data.Websites, day)
            })
            .ToArray();
        var maxAppSeconds = Math.Max(1, dailyUsage.Max(day => day.AppSeconds));

        TrendList.ItemsSource = new ObservableCollection<WeeklyUsageRow>(dailyUsage.Select(day =>
            new WeeklyUsageRow(
                day.Date.ToString("ddd, MMM d"),
                $"Apps: {FormatDuration(day.AppSeconds)}",
                $"Websites: {FormatDuration(day.WebsiteSeconds)}",
                day.AppSeconds * 100d / maxAppSeconds,
                day.WebsiteSeconds * 100d / maxAppSeconds)));
    }

    private static int GetRangeTotal(
        Dictionary<string, Dictionary<string, int>> usage,
        DateTime start,
        DateTime end)
    {
        var total = 0;
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            total += GetDayTotal(usage, day);
        }

        return total;
    }

    private static int GetDayTotal(
        Dictionary<string, Dictionary<string, int>> usage,
        DateTime day)
    {
        return usage.TryGetValue(day.ToString("yyyy-MM-dd"), out var dailyUsage)
            ? dailyUsage.Values.Sum()
            : 0;
    }

    private static string FormatChange(int current, int previous)
    {
        if (previous == 0)
        {
            return current == 0 ? "No recorded app time in either period" : "App time is new this week";
        }

        var changePercent = (current - previous) * 100d / previous;
        var direction = changePercent > 0 ? "more" : changePercent < 0 ? "less" : "no change";
        return changePercent == 0
            ? "No change from the previous 7 days"
            : $"{Math.Abs(changePercent):0}% {direction} than the previous 7 days";
    }

    private static string FormatDuration(int seconds)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes:D2}m";
        }

        return duration.TotalMinutes >= 1
            ? $"{duration.Minutes}m"
            : $"{duration.Seconds}s";
    }
}
