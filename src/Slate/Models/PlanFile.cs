using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slate.Models;

/// <summary>On-disk shape of the plan. Versioned so the format can move without losing data.</summary>
public sealed class PlanFile
{
    public int Version { get; set; } = 2;
    public List<Allocation> Allocations { get; set; } = [];
    public List<TimeEntry> TimeEntries { get; set; } = [];

    /// <summary>
    /// Bookings Azure DevOps never confirmed or denied. Kept apart from the entries: they may
    /// not be on the work item at all, so they count towards nothing until a check says so.
    /// </summary>
    public List<UnconfirmedBooking> UnconfirmedBookings { get; set; } = [];

    /// <summary>Work item id to locally-assigned priority. Never written back to Azure DevOps.</summary>
    public Dictionary<int, int> Priorities { get; set; } = [];

    /// <summary>
    /// Outlook event ids whose block has been deleted but whose event still needs removing.
    /// Written down rather than held in memory: closing the app before the next send would
    /// otherwise strand those events on the calendar with nothing left pointing at them.
    /// </summary>
    public List<string> PendingDeletes { get; set; } = [];

    /// <summary>
    /// Outlook event ids this plan has deliberately let go of - unlinked, or deleted with the
    /// calendar entry left in place. Their events still carry the stamp that says a block once
    /// lived there, so without this they would simply be picked up again on the next refresh
    /// and the thing the user just got rid of would walk straight back onto the grid.
    /// </summary>
    public List<string> Disowned { get; set; } = [];

    /// <summary>
    /// Anything in the saved plan this copy does not know about, kept so it survives a save.
    /// The update can put an older exe back after a failed handover, and without this that
    /// copy's first save would drop whatever a newer one had added.
    ///
    /// This one covers the top level of the file only. It is why every type written into the
    /// plan keeps its own - <see cref="Allocation"/>, <see cref="TimeEntry"/>,
    /// <see cref="UnconfirmedBooking"/> and <see cref="TimeWritePlan"/> - because a member
    /// added inside one of those is dropped on the way through an older copy whatever this
    /// does, and the ones that say a booking or an undo was never confirmed live in there.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = [];
}
