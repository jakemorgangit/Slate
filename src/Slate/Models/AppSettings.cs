using System.Text.Json.Serialization;

namespace Slate.Models;

public enum AdoAuthMode { Pat, Entra }
public enum ThemeMode { Dark, Light, System }
public enum WorkItemScope { AssignedToMe, SavedQuery, CustomWiql }

/// <summary>How text typed into the app is turned into the HTML Azure DevOps stores.</summary>
public enum TextFormat { PlainText, Markdown, Html }

/// <summary>
/// How much of the app is on show. Basic is read-only against Azure DevOps - it lists work
/// items and plans your own time against them, and writes nothing back. Advanced adds
/// everything that changes the work item itself.
/// </summary>
public enum AppMode { Basic, Advanced }

/// <summary>How far the Outlook side has got: no application configured, configured but not
/// signed in, or fully working.</summary>
public enum OutlookState { Off, NeedsSignIn, Ready }

public sealed class AppSettings
{
    public AdoSettings Ado { get; set; } = new();
    public EntraSettings Entra { get; set; } = new();
    public CalendarSettings Calendar { get; set; } = new();
    public PlanningSettings Planning { get; set; } = new();
    public UiSettings Ui { get; set; } = new();

    /// <summary>True once there is enough configuration to talk to Azure DevOps.</summary>
    [JsonIgnore]
    public bool IsAdoConfigured =>
        !string.IsNullOrWhiteSpace(Ado.OrganizationUrl) &&
        (Ado.AuthMode == AdoAuthMode.Entra ? Entra.IsConfigured : !string.IsNullOrWhiteSpace(Ado.PersonalAccessToken));

    [JsonIgnore]
    public bool IsCalendarConfigured => Entra.IsConfigured;

    /// <summary>Graph's own vocabulary for the free/busy status of an event.</summary>
    private static readonly string[] ShowAsValues = ["free", "tentative", "busy", "oof", "workingElsewhere"];

    /// <summary>
    /// Brings the settings back inside the ranges the rest of the app takes for granted,
    /// and replaces any section or list a file left null. The Settings page is not the only
    /// way in - an imported configuration is somebody else's JSON and settings.json can be
    /// edited by hand - so the guarantee belongs here rather than in the form.
    /// </summary>
    public AppSettings Normalize()
    {
        Ado ??= new AdoSettings();
        Entra ??= new EntraSettings();
        Calendar ??= new CalendarSettings();
        Planning ??= new PlanningSettings();
        Ui ??= new UiSettings();

        Ado.ExcludedStates ??= [];

        if (string.IsNullOrWhiteSpace(Calendar.SubjectTemplate)) Calendar.SubjectTemplate = "#{id} {title}";
        // Bounded like everything else here: it is appended to a subject that has its own
        // 250-character ceiling, and a marker long enough to eat the whole line would leave
        // the subject as nothing but a truncated tag.
        if (string.IsNullOrWhiteSpace(Calendar.Marker)) Calendar.Marker = "-Slate-";
        else if (Calendar.Marker.Length > 40) Calendar.Marker = Calendar.Marker[..40];
        Calendar.ReminderMinutes = Math.Clamp(Calendar.ReminderMinutes, 0, 40320);
        Calendar.ShowAs = Array.Find(ShowAsValues,
            v => string.Equals(v, Calendar.ShowAs, StringComparison.OrdinalIgnoreCase)) ?? "busy";

        // A week with no days renders an empty grid with nothing to say why, so an absent
        // or empty list falls back to the usual working week rather than to nothing.
        Planning.WorkingDays = Planning.WorkingDays is { Count: > 0 } days
            ? [.. days.Distinct().OrderBy(d => ((int)d + 6) % 7)]
            : [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

        // Start first, then end against the clamped start: Math.Clamp throws when the
        // bounds cross, which a raw DayStartHour of 24 would do.
        Planning.DayStartHour = Math.Clamp(Planning.DayStartHour, 0, 22);
        Planning.DayEndHour = Math.Clamp(Planning.DayEndHour, Planning.DayStartHour + 1, 24);

        // Kept null rather than pinned to today's working day: null means "follow it", so
        // moving the working day later carries the booking window with it instead of
        // silently leaving it behind at an hour nobody chose.
        if (Planning.AllocateFromHour is { } from)
            Planning.AllocateFromHour = Math.Clamp(from, 0, 23);
        if (Planning.AllocateUntilHour is { } until)
            Planning.AllocateUntilHour = Math.Clamp(until, 1, 24);
        Planning.SlotMinutes = Math.Clamp(Planning.SlotMinutes, 5, 120);
        Planning.DefaultDurationMinutes =
            Math.Clamp(Planning.DefaultDurationMinutes, Planning.SlotMinutes, 24 * 60);
        Planning.RefreshMinutes = Math.Clamp(Planning.RefreshMinutes, 0, 120);
        Planning.AutoSyncSeconds = Math.Clamp(Planning.AutoSyncSeconds, 1, 60);
        Planning.WorkItemRefreshSeconds = Math.Clamp(Planning.WorkItemRefreshSeconds, 0, 3600);
        if (string.IsNullOrWhiteSpace(Planning.SpawnedTaskType)) Planning.SpawnedTaskType = "Task";

        if (string.IsNullOrWhiteSpace(Ui.Accent)) Ui.Accent = "violet";

        return this;
    }
}

public sealed class AdoSettings
{
    /// <summary>e.g. https://dev.azure.com/contoso</summary>
    public string OrganizationUrl { get; set; } = "";
    public string Project { get; set; } = "";
    /// <summary>
    /// Microsoft sign-in by default: the Connect card leads with it and a new install that
    /// followed that card would otherwise never count as configured, because a token it was
    /// never asked for would still be missing.
    /// </summary>
    public AdoAuthMode AuthMode { get; set; } = AdoAuthMode.Entra;

    /// <summary>Stored on disk encrypted with DPAPI; only ever plaintext in memory.</summary>
    public string PersonalAccessToken { get; set; } = "";

    public WorkItemScope Scope { get; set; } = WorkItemScope.AssignedToMe;
    public string SavedQueryId { get; set; } = "";
    public string CustomWiql { get; set; } = "";

    /// <summary>States excluded from the board because they need no more of your time.</summary>
    public List<string> ExcludedStates { get; set; } = ["Closed", "Done", "Removed", "Resolved"];
}

public sealed class EntraSettings
{
    /// <summary>Application (client) ID of your Entra ID app registration.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Directory (tenant) ID, or "organizations" / "common" / "consumers".</summary>
    public string TenantId { get; set; } = "common";

    [JsonIgnore]
    public bool IsConfigured => Guid.TryParse(ClientId, out _);

    [JsonIgnore]
    public string Authority => $"https://login.microsoftonline.com/{(string.IsNullOrWhiteSpace(TenantId) ? "common" : TenantId)}";
}

public sealed class CalendarSettings
{
    /// <summary>Graph calendar id. Empty means the account's default calendar.</summary>
    public string CalendarId { get; set; } = "";
    public string CalendarName { get; set; } = "Default calendar";

    /// <summary>Supports {id}, {title}, {type}, {state}.</summary>
    public string SubjectTemplate { get; set; } = "#{id} {title}";

    /// <summary>
    /// Appended to every subject this app writes, and what tells a second machine which
    /// events are its to manage. The plan file does not travel between machines; the
    /// calendar does, so the calendar is where the truth about a block has to live.
    /// </summary>
    public string Marker { get; set; } = "-Slate-";

    public int ReminderMinutes { get; set; } = 5;
    public bool ReminderEnabled { get; set; } = true;

    /// <summary>free | tentative | busy | oof | workingElsewhere</summary>
    public string ShowAs { get; set; } = "busy";

    public string Category { get; set; } = "";
    public bool IsPrivate { get; set; }
    public bool IncludeWorkItemLink { get; set; } = true;

    /// <summary>Deleting an allocation also deletes the calendar event it created.</summary>
    public bool DeleteEventWithAllocation { get; set; } = true;
}

public sealed class PlanningSettings
{
    /// <summary>Start of the working day. With the full day off, this is also where the grid starts.</summary>
    public int DayStartHour { get; set; } = 8;

    /// <summary>End of the working day, exclusive.</summary>
    public int DayEndHour { get; set; } = 19;

    /// <summary>
    /// Draw the whole 24 hours rather than only the working day, for anyone whose week does
    /// not stop at five - on call, on shift, or simply booking around the evening.
    /// </summary>
    public bool ShowFullDay { get; set; }

    /// <summary>
    /// First hour automatic scheduling may place work in. Null follows the working day, which
    /// is what most people want; setting it later leaves the start of the morning clear for
    /// whatever the day actually opens with.
    /// </summary>
    public int? AllocateFromHour { get; set; }

    /// <summary>Last hour automatic scheduling may run to, exclusive. Null follows the working day.</summary>
    public int? AllocateUntilHour { get; set; }

    // The four below are what the rest of the app should ask for: they hold the relationships
    // between these settings in one place, so nothing has to remember that the grid and the
    // working day stopped being the same thing.

    /// <summary>Start of the working day, clamped to something usable.</summary>
    [JsonIgnore]
    public int WorkStartHour => Math.Clamp(DayStartHour, 0, 23);

    /// <summary>End of the working day, always at least an hour after it starts.</summary>
    [JsonIgnore]
    public int WorkEndHour => Math.Clamp(DayEndHour, WorkStartHour + 1, 24);

    /// <summary>First hour the grid draws.</summary>
    [JsonIgnore]
    public int GridStartHour => ShowFullDay ? 0 : WorkStartHour;

    /// <summary>Last hour the grid draws, exclusive.</summary>
    [JsonIgnore]
    public int GridEndHour => ShowFullDay ? 24 : WorkEndHour;

    /// <summary>First hour automatic scheduling will book into.</summary>
    [JsonIgnore]
    public int BookFromHour =>
        Math.Clamp(AllocateFromHour ?? WorkStartHour, GridStartHour, GridEndHour - 1);

    /// <summary>Last hour automatic scheduling will book into, exclusive.</summary>
    [JsonIgnore]
    public int BookUntilHour =>
        Math.Clamp(AllocateUntilHour ?? WorkEndHour, BookFromHour + 1, GridEndHour);

    /// <summary>Grid granularity in minutes: 15, 30 or 60.</summary>
    public int SlotMinutes { get; set; } = 30;

    public int DefaultDurationMinutes { get; set; } = 60;

    /// <summary>Days shown in the week grid.</summary>
    public List<DayOfWeek> WorkingDays { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public DayOfWeek WeekStart { get; set; } = DayOfWeek.Monday;

    /// <summary>Overlay existing Outlook events so you can see what is already booked.</summary>
    public bool ShowExistingEvents { get; set; } = true;

    /// <summary>
    /// Whether the overlay has ever been decided with a working calendar in front of you.
    /// Until it has, connecting Outlook turns the overlay on: a "false" carried in from a
    /// config written while Outlook was unavailable is not a choice anybody made, and the
    /// symptom - a connected calendar showing nothing - reads as the app being broken.
    /// </summary>
    public bool OverlayChosen { get; set; }

    /// <summary>Refuse to place work on top of something already in the calendar.</summary>
    public bool PreventOverlap { get; set; } = true;

    /// <summary>Warn when an allocation ends up on top of something already in the calendar.</summary>
    public bool WarnOnConflict { get; set; } = true;

    /// <summary>Pull moves and deletions made in Outlook back into the plan.</summary>
    public bool TwoWaySync { get; set; } = true;

    /// <summary>
    /// Push changes to Outlook on their own, shortly after you stop making them. Off turns
    /// the manual Send button back on for anyone who would rather decide when it happens.
    /// </summary>
    public bool AutoSync { get; set; } = true;

    /// <summary>
    /// How long to wait for you to stop before pushing. Rearranging a week is a flurry of
    /// small edits, and waiting for the quiet turns that into one write per block.
    /// </summary>
    public int AutoSyncSeconds { get; set; } = 4;

    /// <summary>How often to re-read the calendar while the app is open. Zero disables polling.</summary>
    public int RefreshMinutes { get; set; } = 5;

    /// <summary>How often to re-read work items from Azure DevOps. Zero disables it.</summary>
    public int WorkItemRefreshSeconds { get; set; } = 60;

    /// <summary>Take Remaining Work down as Completed Work goes up when recording time.</summary>
    public bool ReduceRemainingOnRecord { get; set; } = true;

    /// <summary>
    /// Offer to spawn a Task when a block is placed against a work item type that cannot
    /// carry time, so there is somewhere to book the hours.
    /// </summary>
    public bool OfferTaskForUntrackable { get; set; } = true;

    /// <summary>The type spawned by that offer. Task exists in every stock process template.</summary>
    public string SpawnedTaskType { get; set; } = "Task";
}

public sealed class UiSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public string Accent { get; set; } = "violet";
    public bool CompactDensity { get; set; }

    /// <summary>
    /// Basic or Advanced. Defaults to Advanced so an existing setup keeps every feature it
    /// already had; a new one can be turned down on the first day.
    /// </summary>
    public AppMode Mode { get; set; } = AppMode.Advanced;

    /// <summary>How the comment box reads what you type. Remembered between sessions.</summary>
    public TextFormat CommentFormat { get; set; } = TextFormat.Markdown;

    /// <summary>
    /// How the description editor reads what you type. Defaults to HTML because that is what
    /// Azure DevOps already stores, so opening an existing description edits it in place.
    /// </summary>
    public TextFormat DescriptionFormat { get; set; } = TextFormat.Html;
}
