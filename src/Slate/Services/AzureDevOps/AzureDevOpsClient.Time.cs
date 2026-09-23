using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Slate.Models;

namespace Slate.Services.AzureDevOps;

/// <summary>
/// Booking time against a work item, and taking it back off.
///
/// Every change here is pinned to the revision it was worked out from, with a JSON Patch test
/// on /rev, and that is what lets it survive a lost answer. A pinned change either became the
/// very next revision or never will, so after a timeout, a dropped connection or a server
/// error, that one revision says whether it landed. One that landed is reported as done
/// rather than sent a second time; one that has not yet can be sent again pinned the same
/// way, knowing that at most one of the two can ever go through.
/// </summary>
public sealed partial class AzureDevOpsClient
{
    private const string CompletedWorkField = "Microsoft.VSTS.Scheduling.CompletedWork";
    private const string RemainingWorkField = "Microsoft.VSTS.Scheduling.RemainingWork";

    /// <summary>
    /// PATCHes one change may take: the first, one more after a conflict or a lost answer,
    /// and one to spare for both happening to the same change.
    /// </summary>
    private const int MaxTimeSends = 3;

    /// <summary>The pause before reading the item a second time, when the first read after a lost answer failed too.</summary>
    private static readonly TimeSpan RereadPause = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How far apart this machine's clock and the service's may be before a revision's time is
    /// worth doubting. Generous on purpose: the times are only ever used to rule a revision
    /// out, and ruling out one of our own is what would book the hours twice.
    /// </summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    /// <summary>
    /// One time change per work item at a time from this copy. Two pinned to the same
    /// revision would otherwise race, and the loser's look at the work item would find the
    /// winner's change - same size, same person - and take it for its own.
    /// </summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _timeGates = new();

    /// <summary>
    /// Told what is about to go out, just before each send of it. The caller writes the change
    /// down before it can land, so a copy that dies between the send and the answer still
    /// leaves the next one something it can settle against the work item.
    /// </summary>
    public delegate void PinnedHandler(TimeWritePlan pinned);

    /// <summary>Books time against a work item.</summary>
    public Task<TimeRecordResult> RecordTimeAsync(
        int id, double hours, bool reduceRemaining, PinnedHandler? pinning = null, CancellationToken ct = default)
    {
        if (hours <= 0) throw new AzureDevOpsException("Enter a number of hours greater than zero.");
        return AdjustTimeAsync(id, hours, reduceRemaining ? -hours : 0, pinning, ct);
    }

    /// <summary>
    /// Takes a previous booking back off the work item. Reverses the changes that were
    /// actually applied rather than the hours that were asked for: recording clamps at
    /// zero, so undoing by the asked-for hours hands back work that was never there.
    /// </summary>
    public Task<TimeRecordResult> UndoTimeAsync(
        int id, double appliedCompleted, double appliedRemaining,
        PinnedHandler? pinning = null, CancellationToken ct = default)
    {
        if (appliedCompleted == 0 && appliedRemaining == 0)
            throw new AzureDevOpsException("Nothing to undo.");

        return AdjustTimeAsync(id, -appliedCompleted, -appliedRemaining, pinning, ct);
    }

    /// <summary>
    /// Looks for a change that was never confirmed, without writing anything. True when it
    /// landed, with the item as it now stands; false when it did not and now never can; null
    /// when that still cannot be told - which includes a change not on the item yet but sent
    /// too recently to rule out.
    /// </summary>
    public async Task<(bool? Landed, TimeRecordResult? Result)> CheckTimeWriteAsync(
        TimeWritePlan plan, CancellationToken ct = default)
    {
        var gate = _timeGates.GetOrAdd(plan.WorkItemId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var (landing, now, _) = await SettleAsync(plan, ct);
            return landing switch
            {
                Landing.Landed => (true, ResultFrom(plan, now!)),
                Landing.Lost => (false, null),
                Landing.NotYet when !plan.CouldStillLand => (false, null),
                _ => (null, null),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Moves Completed Work and Remaining Work by signed amounts, reporting what it managed
    /// to apply as well as where the fields ended up. Pinned to the revision it read, so an
    /// edit made meanwhile is never overwritten: the change is worked out again from the
    /// fresh values and tried once more.
    ///
    /// Ends one of three ways: the change is on the work item exactly once; an
    /// <see cref="AzureDevOpsException"/>, when it certainly is not; or a
    /// <see cref="TimeWriteUnconfirmedException"/>, when that could not be told.
    /// </summary>
    private async Task<TimeRecordResult> AdjustTimeAsync(
        int id, double completedDelta, double remainingDelta, PinnedHandler? pinning, CancellationToken ct)
    {
        var gate = _timeGates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await AdjustTimeLockedAsync(id, completedDelta, remainingDelta, pinning, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TimeRecordResult> AdjustTimeLockedAsync(
        int id, double completedDelta, double remainingDelta, PinnedHandler? pinning, CancellationToken ct)
    {
        // Nothing has gone out yet, so a failure to read is an ordinary failure.
        var now = await ReadTimeFieldsAsync(id, ct);

        // The change being sent, and whether a send of it went unanswered - which leaves it
        // free to land at any moment until the item moves past the revision it is pinned to.
        TimeWritePlan? pinned = null;
        var outstanding = false;
        AzureDevOpsException? lastFailure = null;

        for (var sends = 1; ; sends++)
        {
            if (pinned is null)
            {
                pinned = PlanTimeWrite(id, now, completedDelta, remainingDelta);

                // Only an undo against a work item already at zero gets here. A change that
                // moves nothing is not worth a revision.
                if (pinned.AppliedCompleted == 0 && pinned.AppliedRemaining == 0)
                    return new TimeRecordResult(now.Completed, now.Remaining);
            }

            var sentAt = DateTimeOffset.Now;
            pinned = pinned with
            {
                SentAt = sentAt,
                FirstSentAt = pinned.FirstSentAt == default ? sentAt : pinned.FirstSentAt,
            };

            AzureDevOpsException? refused = null;

            // Written down before it can possibly land, never after: a copy that goes away
            // between this send and its answer would otherwise leave hours on the work item
            // with nothing here pointing at them, and the block offered again as unrecorded.
            pinning?.Invoke(pinned);

            try
            {
                using var doc = await SendAsync(HttpMethod.Patch, WorkItemUrl(id), TimePatch(pinned), ct,
                    "application/json-patch+json");
                return ReadTimeResult(pinned, doc.RootElement);
            }
            catch (AzureDevOpsException ex) when (ex.Unanswered)
            {
                lastFailure = ex;
                outstanding = true;
            }
            catch (AzureDevOpsException ex) when (IsRevisionConflict(ex))
            {
                // Turned away, so this send did nothing - but the change that beat it can be
                // this very one: an earlier send of it, or this send made twice by the network
                // stack after the first answer went missing. Only the item can say.
                lastFailure = ex;
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       && (outstanding || IsWorthSettling(ex)))
            {
                // Turned away outright. Still looked into: an earlier send may be unaccounted
                // for, a failed revision test can come back as a plain bad request, and even a
                // refusal is worth putting to the work item before it is reported.
                lastFailure = refused = ex as AzureDevOpsException ?? new AzureDevOpsException(ex.Message, ex);
            }

            var (landing, current, why) = await SettleAsync(pinned, ct);
            now = current ?? now;

            switch (landing)
            {
                case Landing.Landed:
                    return ResultFrom(pinned, now);

                // Nothing of this is on its way, so nothing of it can go on the work item any
                // more - however little of the item could be read. An ordinary failure, which
                // is safe to try again, rather than hours that may already be booked.
                case Landing.Unknown when !outstanding:
                case Landing.NotYet when !outstanding && refused is not null:
                    throw refused ?? lastFailure!;

                case Landing.Unknown:
                    throw Unconfirmed(pinned, lastFailure, why);

                case Landing.Lost:
                    // Nothing of it went on, and nothing of it can now: the item has moved past
                    // its revision. Worked out again from the item as it stands.
                    pinned = null;
                    outstanding = false;
                    break;

                case Landing.NotYet when !outstanding:
                    pinned = null;
                    break;

                // Not there yet, with a send unanswered: still pinned where it was, so sending
                // it again can only ever land once between the two.
            }

            if (sends >= MaxTimeSends)
                throw outstanding
                    ? Unconfirmed(pinned!, lastFailure, "it was still not on the work item after the last send")
                    : lastFailure!;
        }
    }

    private enum Landing
    {
        /// <summary>The change is the revision after the one it was pinned to.</summary>
        Landed,

        /// <summary>The item has moved on without it, so it never can land.</summary>
        Lost,

        /// <summary>The item has not moved: it has not landed, though a send still on its way could.</summary>
        NotYet,

        /// <summary>The item or its history could not be read, or does not add up.</summary>
        Unknown,
    }

    /// <summary>
    /// Works out from the work item whether a change sent without an answer landed. Reads the
    /// item, and when it has moved on, the one revision the change could have made.
    ///
    /// Anything it cannot settle comes back with the reason why, which goes to the log and
    /// into what the user is told: an identity that never matches looks exactly like an
    /// outage from the outside, and one of those is worth diagnosing rather than living with.
    /// </summary>
    private async Task<(Landing Landing, TimeFields? Now, string? Why)> SettleAsync(
        TimeWritePlan plan, CancellationToken ct)
    {
        TimeFields now;
        try
        {
            now = await ReadTimeFieldsAsync(plan.WorkItemId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Often the same blip that lost the answer, so it gets a moment and one more go.
            try
            {
                await Task.Delay(RereadPause, ct);
                now = await ReadTimeFieldsAsync(plan.WorkItemId, ct);
            }
            catch (Exception again) when (again is not OperationCanceledException)
            {
                return Undecided(plan, null, $"#{plan.WorkItemId} could not be read: {again.Message}");
            }
        }

        if (now.Rev == plan.Rev) return (Landing.NotYet, now, null);

        // Behind the pin - restored from the recycle bin, perhaps. Nothing to reason from.
        if (now.Rev < plan.Rev)
            return Undecided(plan, now,
                $"#{plan.WorkItemId} is at revision {now.Rev}, behind the {plan.Rev} this was pinned to");

        // Pinned to plan.Rev, so the next revision is the only one this change can have made.
        var (revision, read) = await ReadRevisionAsync(plan.WorkItemId, plan.Rev + 1, ct);

        // The history was read through where this change would be and it is not there. It had
        // its one chance at that revision, so it never went on and never will.
        if (read == RevisionRead.Absent) return (Landing.Lost, now, null);

        if (revision is null)
            return Undecided(plan, now, $"revision {plan.Rev + 1} of #{plan.WorkItemId} could not be read");

        if (!revision.Made(plan)) return (Landing.Lost, now, null);

        // The right change at the right revision - but a revision made before this change was
        // first sent cannot be it. The same hours booked from a second machine look exactly
        // like this one, and claiming those as ours would quietly lose a booking that is still
        // owed. Not claimed and not ruled out: only the work item itself can say.
        if (revision.RevisedDate is { } when && when < plan.SendWindowStart - ClockSkew)
            return Undecided(plan, now,
                $"revision {plan.Rev + 1} of #{plan.WorkItemId} made the change, but Azure DevOps dates it "
                + $"{when:u}, before this was first sent at {plan.SendWindowStart:u}");

        // The right change at the right revision. Someone else making exactly this change as
        // the very next edit is far-fetched, but when the service names who made it and that
        // is plainly not this account, it is not claimed. Unknown rather than lost, so that a
        // mismatch in how the two name the same person can never lead to booking it again.
        var mine = await TryReadMyNamesAsync(ct);
        if (mine.Count > 0 && revision.Names.Count > 0 && !revision.Names.Overlaps(mine))
            return Undecided(plan, now,
                $"revision {plan.Rev + 1} of #{plan.WorkItemId} made the change, but Azure DevOps says it was made by "
                + $"[{string.Join(", ", revision.Names)}] and this sign-in answers to [{string.Join(", ", mine)}]");

        return (Landing.Landed, now, null);
    }

    /// <summary>
    /// Could not be told either way, with the reason kept for the log and for what is shown.
    /// </summary>
    private static (Landing, TimeFields?, string?) Undecided(TimeWritePlan plan, TimeFields? now, string why)
    {
        CrashLog.WriteLine(
            $"Could not settle the time change on #{plan.WorkItemId} pinned to revision {plan.Rev} " +
            $"({plan.CompletedBefore:0.##}h to {plan.CompletedAfter:0.##}h completed): {why}.");

        return (Landing.Unknown, now, why);
    }

    /// <summary>The revision number and time fields of a work item as it stands.</summary>
    private sealed record TimeFields(int Rev, double Completed, double Remaining);

    private async Task<TimeFields> ReadTimeFieldsAsync(int id, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, WorkItemUrl(id), null, ct);

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("rev", out var rev) || rev.ValueKind != JsonValueKind.Number
            || !root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            throw new AzureDevOpsException(
                $"Azure DevOps sent #{id} back without its revision, so the time on it could not be worked out.");

        return new TimeFields(rev.GetInt32(),
            Num(fields, CompletedWorkField) ?? 0,
            Num(fields, RemainingWorkField) ?? 0);
    }

    /// <summary>
    /// What one revision did to the time fields, and every name the service gave for whose
    /// hand it was. More than one, because the same person is named differently depending on
    /// where you ask - and matching on any of them is what keeps an ordinary booking from
    /// reading as somebody else's work.
    /// </summary>
    private sealed record RevisionChange(
        double? CompletedOld, double? CompletedNew, bool CompletedChanged,
        double? RemainingOld, double? RemainingNew, bool RemainingChanged,
        HashSet<string> Names, DateTimeOffset? RevisedDate)
    {
        /// <summary>
        /// True when this revision moved the field the change moves, from exactly where the
        /// change found it to exactly where it would leave it. Only that one field is held to
        /// account: a process rule may touch others on the same save, and a false "not ours"
        /// is the answer that would book the hours a second time.
        /// </summary>
        public bool Made(TimeWritePlan plan)
        {
            if (!Same(plan.CompletedBefore, plan.CompletedAfter))
                return CompletedChanged
                       && Same(CompletedOld ?? 0, plan.CompletedBefore)
                       && Same(CompletedNew ?? 0, plan.CompletedAfter);

            if (plan.SetsRemaining && !Same(plan.RemainingBefore, plan.RemainingAfter))
                return RemainingChanged
                       && Same(RemainingOld ?? 0, plan.RemainingBefore)
                       && Same(RemainingNew ?? 0, plan.RemainingAfter);

            // A change that moves nothing is never sent.
            return false;
        }

        private static bool Same(double a, double b) => Math.Abs(a - b) < 0.001;
    }

    /// <summary>How a look through a work item's history for one revision turned out.</summary>
    private enum RevisionRead
    {
        /// <summary>Read, and there it is.</summary>
        Found,

        /// <summary>Read past where it would be: the history does not have it.</summary>
        Absent,

        /// <summary>Could not be read, or does not reach far enough to say either way.</summary>
        Unreadable,
    }

    /// <summary>
    /// One revision's changes, from the work item's updates - the only place that says what a
    /// single revision changed, who made it and when.
    ///
    /// Absent and unreadable are kept apart on purpose: only a history that was actually read
    /// through can rule a change out, and treating a failed read as "not there" is what would
    /// send the same hours a second time.
    /// </summary>
    private async Task<(RevisionChange? Change, RevisionRead Read)> ReadRevisionAsync(
        int id, int rev, CancellationToken ct)
    {
        const int page = 200;

        try
        {
            // Updates come oldest first. Capped, so a service that ignored $skip could not
            // keep this going for ever.
            for (var skip = 0; skip < 50 * page; skip += page)
            {
                using var doc = await SendAsync(HttpMethod.Get,
                    $"{OrgUrl}/_apis/wit/workItems/{id}/updates?$top={page}&$skip={skip}&api-version={ApiVersion}",
                    null, ct);

                var updates = ReadValueArray(doc.RootElement).ToList();
                foreach (var update in updates)
                {
                    var updateRev = update.TryGetProperty("rev", out var r) && r.ValueKind == JsonValueKind.Number
                        ? r.GetInt32()
                        : 0;

                    if (updateRev == rev) return (ReadRevisionChange(update), RevisionRead.Found);

                    // Past where it would be, so the history has been read through and it is
                    // not in it.
                    if (updateRev > rev) return (null, RevisionRead.Absent);
                }

                // The history stops short of a revision the work item is already past, so it
                // has not caught up yet and its silence says nothing.
                if (updates.Count < page) return (null, RevisionRead.Unreadable);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unreadable, which is not the same as absent.
        }

        return (null, RevisionRead.Unreadable);
    }

    private static RevisionChange ReadRevisionChange(JsonElement update)
    {
        var fields = update.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;

        (double? Old, double? New, bool Changed) Field(string name)
        {
            if (fields.ValueKind != JsonValueKind.Object || !fields.TryGetProperty(name, out var change)
                || change.ValueKind != JsonValueKind.Object)
                return (null, null, false);

            return (Number(change, "oldValue"), Number(change, "newValue"), true);
        }

        var completed = Field(CompletedWorkField);
        var remaining = Field(RemainingWorkField);

        var by = NewNameSet();
        if (update.TryGetProperty("revisedBy", out var who))
            AddNames(by, who, "id", "uniqueName", "descriptor", "subjectDescriptor");

        // The revision's own date, or the one the item's Changed Date moved to on the same
        // save, which some process templates are the only ones to set.
        var when = Moment(update, "revisedDate")
                   ?? (fields.ValueKind == JsonValueKind.Object
                       && fields.TryGetProperty("System.ChangedDate", out var changed)
                       ? Moment(changed, "newValue")
                       : null);

        return new RevisionChange(
            completed.Old, completed.New, completed.Changed,
            remaining.Old, remaining.New, remaining.Changed,
            by, when);
    }

    /// <summary>A moment, whether it came as a string or was left out altogether.</summary>
    private static DateTimeOffset? Moment(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static HashSet<string> NewNameSet() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Collects whichever of the named string properties an identity object carries.</summary>
    private static void AddNames(HashSet<string> into, JsonElement identity, params string[] properties)
    {
        if (identity.ValueKind != JsonValueKind.Object) return;

        foreach (var property in properties)
        {
            if (identity.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text)
                into.Add(text);
        }
    }

    /// <summary>A number, whether it came as one or as text.</summary>
    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var v)) return null;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// Every name the account this client works as answers to, remembered for as long as the
    /// connection stays the same. Empty when it could not be read, which simply means the
    /// identity has no say in whether a change was ours.
    /// </summary>
    private (string Connection, HashSet<string> Names) _myNames = ("", NewNameSet());

    /// <summary>
    /// The two identities connectionData names. They are often different people on paper -
    /// the credential and the account it acts for - and which of them a work item's history
    /// names is not something a client gets to know, so both count as us.
    /// </summary>
    private static readonly string[] ConnectionIdentities = ["authenticatedUser", "authorizedUser"];

    /// <summary>
    /// Reads who this client is from connectionData, keeping every form of the answer.
    ///
    /// authenticatedUser is not the one that matches a work item's identity references on an
    /// AAD-backed organization - authorizedUser usually is - and either can be named by
    /// descriptor or sign-in address rather than by id. Taking all of them and matching on any
    /// is what stops an ordinary booking looking like somebody else's edit for ever.
    /// </summary>
    private async Task<HashSet<string>> TryReadMyNamesAsync(CancellationToken ct)
    {
        // The organization decides which identity ids mean anything, and the way of signing in
        // decides who we are; either moving makes what is held here somebody else's answer.
        var connection = $"{OrgUrl}|{settings.Current.Ado.AuthMode}";
        if (_myNames.Connection == connection && _myNames.Names.Count > 0) return _myNames.Names;

        var names = NewNameSet();
        try
        {
            using var doc = await SendAsync(HttpMethod.Get,
                $"{OrgUrl}/_apis/connectionData?api-version={ApiVersion}-preview", null, ct);

            foreach (var which in ConnectionIdentities)
            {
                if (!doc.RootElement.TryGetProperty(which, out var user)) continue;

                AddNames(names, user, "id", "descriptor", "subjectDescriptor", "uniqueName");

                // The sign-in address lives under properties.Account as a typed value.
                if (user.TryGetProperty("properties", out var properties)
                    && properties.ValueKind == JsonValueKind.Object
                    && properties.TryGetProperty("Account", out var account))
                    AddNames(names, account, "$value");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CrashLog.WriteLine($"Could not read who this sign-in is from {OrgUrl}: {ex.Message}");
            return names;
        }

        if (names.Count > 0) _myNames = (connection, names);
        return names;
    }

    private static TimeWritePlan PlanTimeWrite(int id, TimeFields now, double completedDelta, double remainingDelta)
    {
        var setsRemaining = remainingDelta != 0;

        return new TimeWritePlan(
            id,
            now.Rev,
            now.Completed,
            Math.Round(Math.Max(0, now.Completed + completedDelta), 2),
            now.Remaining,
            setsRemaining ? Math.Round(Math.Max(0, now.Remaining + remainingDelta), 2) : now.Remaining,
            setsRemaining,
            DateTimeOffset.Now);
    }

    private static List<object> TimePatch(TimeWritePlan plan)
    {
        var patch = new List<object>
        {
            new { op = "test", path = "/rev", value = (object)plan.Rev },
            new { op = "add", path = "/fields/" + CompletedWorkField, value = (object)plan.CompletedAfter },
        };

        if (plan.SetsRemaining)
            patch.Add(new { op = "add", path = "/fields/" + RemainingWorkField, value = (object)plan.RemainingAfter });

        return patch;
    }

    /// <summary>
    /// The item as the PATCH answered with it. Falls back on what was sent for anything the
    /// answer leaves out: the change is on the item either way, and must not be reported as a
    /// failure over the shape of the reply.
    /// </summary>
    private static TimeRecordResult ReadTimeResult(TimeWritePlan plan, JsonElement root)
    {
        var fields = root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object
            ? f
            : default;

        var hasFields = fields.ValueKind == JsonValueKind.Object;

        return new TimeRecordResult(
            (hasFields ? Num(fields, CompletedWorkField) : null) ?? plan.CompletedAfter,
            (hasFields ? Num(fields, RemainingWorkField) : null) ?? plan.RemainingAfter,
            plan.AppliedCompleted,
            plan.AppliedRemaining,
            plan);
    }

    private static TimeRecordResult ResultFrom(TimeWritePlan plan, TimeFields now) =>
        new(now.Completed, now.Remaining, plan.AppliedCompleted, plan.AppliedRemaining, plan);

    /// <summary>
    /// Refusals that are still worth putting to the work item before they are reported. A
    /// failed revision test can come back as a plain bad request. "Service unavailable" is the
    /// front door turning a request away rather than the service acting on it, so nothing
    /// should have happened - but the one read is cheap, and it is the difference between
    /// reporting a failure the work item agrees with and one it does not.
    /// </summary>
    private static bool IsWorthSettling(Exception ex) =>
        ex is AzureDevOpsException
        { Status: HttpStatusCode.BadRequest or HttpStatusCode.ServiceUnavailable };

    /// <summary>
    /// The revision test failing: the item was changed after it was read. Which status that
    /// comes back with is not something the documentation pins down, so Azure DevOps' own
    /// names for it count as well.
    /// </summary>
    private static bool IsRevisionConflict(AzureDevOpsException ex) =>
        ex.Status is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
        || (ex.ErrorKey?.Contains("RevisionMismatch", StringComparison.OrdinalIgnoreCase) ?? false)
        || (ex.ErrorKey?.Contains("TestPatchOperationFailed", StringComparison.OrdinalIgnoreCase) ?? false)
        || ex.Message.Contains("TF26071", StringComparison.Ordinal);

    private static TimeWriteUnconfirmedException Unconfirmed(TimeWritePlan plan, Exception? cause, string? why) =>
        new($"Azure DevOps did not confirm the change to #{plan.WorkItemId}, and a look at the work item " +
            "afterwards could not settle whether it went through"
            + (string.IsNullOrEmpty(why) ? "" : $" - {why}")
            + ". Check the work item before trying again.",
            plan, cause);

    private string WorkItemUrl(int id) => $"{OrgUrl}/_apis/wit/workitems/{id}?api-version={ApiVersion}";
}
