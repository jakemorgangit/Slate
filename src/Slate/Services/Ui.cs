using Slate.Models;

namespace Slate.Services;

/// <summary>Presentation helpers shared by the Razor components.</summary>
public static class Ui
{
    /// <summary>Azure DevOps work item type colours, matching the ones the web UI uses.</summary>
    private static readonly Dictionary<string, string> TypeColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bug"] = "#cc293d",
        ["Task"] = "#f2cb1d",
        ["User Story"] = "#009ccc",
        ["Product Backlog Item"] = "#009ccc",
        ["Requirement"] = "#009ccc",
        ["Feature"] = "#773b93",
        ["Epic"] = "#ff7b00",
        ["Issue"] = "#b4009e",
        ["Impediment"] = "#b4009e",
        ["Test Case"] = "#00897b",
        ["Test Plan"] = "#00897b",
        ["Test Suite"] = "#00897b",
        ["Change Request"] = "#f28c00",
        ["Review"] = "#5c9c3e",
        ["Risk"] = "#e06c00",
    };

    public static string ColorFor(string workItemType)
    {
        if (TypeColors.TryGetValue(workItemType ?? "", out var known)) return known;

        // Unknown types get a stable colour derived from the name so they stay distinguishable.
        var hash = 17;
        foreach (var c in workItemType ?? "") hash = hash * 31 + char.ToLowerInvariant(c);
        // Masked rather than Math.Abs, which throws on int.MinValue.
        var hue = (hash & 0x7FFFFFFF) % 360;
        return $"hsl({hue} 62% 52%)";
    }

    /// <summary>Renders minutes as "2h 30m", "45m" or "3h".</summary>
    public static string Duration(int minutes)
    {
        if (minutes <= 0) return "0m";
        var hours = minutes / 60;
        var rest = minutes % 60;

        return hours == 0 ? $"{rest}m"
            : rest == 0 ? $"{hours}h"
            : $"{hours}h {rest}m";
    }

    /// <summary>Renders minutes as decimal hours, e.g. "1.5h" - matches how ADO shows effort.</summary>
    public static string Hours(int minutes) =>
        minutes <= 0 ? "0h" : $"{minutes / 60.0:0.##}h";

    /// <summary>Bare decimal hours for timesheet cells, e.g. "1.50".</summary>
    public static string DecimalHours(int minutes) =>
        (minutes / 60.0).ToString("0.00");

    public static string TimeRange(DateTime start, DateTime end) =>
        $"{start:HH:mm}–{end:HH:mm}";

    public static string RelativeDay(DateTime day)
    {
        var delta = (day.Date - DateTime.Today).Days;
        return delta switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            _ => day.ToString("ddd d MMM"),
        };
    }

    public static string Ago(DateTimeOffset? when)
    {
        if (when is null) return "never";
        var delta = DateTimeOffset.Now - when.Value;

        return delta.TotalSeconds < 60 ? "just now"
            : delta.TotalMinutes < 60 ? $"{(int)delta.TotalMinutes}m ago"
            : delta.TotalHours < 24 ? $"{(int)delta.TotalHours}h ago"
            : delta.TotalDays < 7 ? $"{(int)delta.TotalDays}d ago"
            : when.Value.ToString("d MMM");
    }

    public static string SyncLabel(SyncState state) => state switch
    {
        SyncState.Synced => "In Outlook",
        SyncState.Modified => "Changed - needs sending",
        SyncState.Failed => "Failed to send",
        SyncState.Missing => "Deleted in Outlook",
        _ => "Not sent yet",
    };

    public static string SyncBadgeClass(SyncState state) => state switch
    {
        SyncState.Synced => "badge badge-ok",
        SyncState.Modified => "badge badge-warn",
        SyncState.Failed => "badge badge-danger",
        SyncState.Missing => "badge badge-danger",
        _ => "badge badge-muted",
    };

    public static string StateClass(SyncState state) => state.ToString().ToLowerInvariant();

    /// <summary>
    /// Traffic-light colours for priority: P1 red through to P4 violet. Used for both your
    /// own triage and the value Azure DevOps holds.
    /// </summary>
    public static string PriorityColor(int priority) => priority switch
    {
        1 => "#e5484d",
        2 => "#f5a524",
        3 => "#e8d21a",
        4 => "#8b5cf6",
        _ => "var(--text-3)",
    };

    /// <summary>A dark-enough text colour to sit on the pill above.</summary>
    public static string PriorityTextColor(int priority) => priority switch
    {
        2 or 3 => "#20160a",
        _ => "#ffffff",
    };

    /// <summary>Filled when the level is the chosen one, outlined otherwise.</summary>
    public static string PriorityPillStyle(int priority, bool active) =>
        active
            ? $"background: {PriorityColor(priority)}; color: {PriorityTextColor(priority)}"
            : $"background: transparent; color: {PriorityColor(priority)}; box-shadow: inset 0 0 0 1.5px {PriorityColor(priority)}";

    public static string PriorityLabel(int priority) => priority switch
    {
        1 => "P1 - critical",
        2 => "P2 - high",
        3 => "P3 - medium",
        4 => "P4 - low",
        _ => "No priority set",
    };

    /// <summary>
    /// Which priority a pill should show. Your own triage wins when you have set one, with
    /// the Azure DevOps value kept alongside so the tooltip can say what it is overriding.
    /// </summary>
    public readonly record struct PriorityBadge(int Level, bool IsLocal, int AdoLevel)
    {
        public bool Any => Level is >= 1 and <= 4;

        /// <summary>True when your triage disagrees with what the team can see.</summary>
        public bool Overrides => IsLocal && AdoLevel is >= 1 and <= 4 && AdoLevel != Level;
    }

    public static PriorityBadge Badge(int local, int ado) =>
        local is >= 1 and <= 4 ? new PriorityBadge(local, true, ado) : new PriorityBadge(ado, false, ado);

    public static string PriorityTooltip(PriorityBadge badge)
    {
        if (!badge.Any) return "No priority set";

        var lines = new List<string> { PriorityLabel(badge.Level) };

        if (badge.IsLocal)
        {
            lines.Add(badge.Overrides
                ? $"Your triage. Azure DevOps has this at P{badge.AdoLevel}."
                : "Your triage - this one stays on this machine.");
        }
        else
        {
            lines.Add("From Azure DevOps.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The arrow on a sortable column header. Empty unless that column is the one in use.</summary>
    public static string SortArrow(bool active, bool descending) =>
        !active ? "" : descending ? " \u25be" : " \u25b4";

    /// <summary>Compact age, e.g. "3d", "5w", "14mo".</summary>
    public static string AgeLabel(int days) =>
        days < 1 ? "today"
        : days < 14 ? $"{days}d"
        : days < 70 ? $"{days / 7}w"
        : $"{days / 30}mo";

    /// <summary>Older work reads warmer, so a stale item stands out without needing a legend.</summary>
    public static string AgeClass(int days) =>
        days >= 90 ? "age-old"
        : days >= 30 ? "age-warm"
        : days >= 7 ? "age-mid"
        : "age-fresh";

    public static string AgeTooltip(DateTimeOffset created, int days) =>
        $"Raised {created.ToLocalTime():d MMM yyyy} - {days} day{(days == 1 ? "" : "s")} old";

    /// <summary>Truncates a string for a tooltip or a tight cell.</summary>
    public static string Clip(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..(max - 1)] + "…";
}
