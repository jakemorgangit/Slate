using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
    /// What names one work item to the two dictionaries below: the organization it is on as
    /// well as its number. A work item number means nothing outside one organization, so a
    /// change pinned to one organization's #7 must never be settled - or held to account -
    /// against another's. Keyed on the number alone, everything below went on reasoning about
    /// "#7" for the rest of a session after a switch.
    ///
    /// The address is reduced to one spelling, the same way an entry's stamp is, so that a
    /// trailing slash or a change of case is not taken for a different organization.
    /// </summary>
    private readonly record struct TimeItem(string Organization, int WorkItemId);

    private TimeItem Item(int workItemId) => new(TimeEntry.NormaliseOrganization(OrgUrl), workItemId);

    /// <summary>
    /// One time change per work item at a time from this copy. Two pinned to the same
    /// revision would otherwise race, and the loser's look at the work item would find the
    /// winner's change - same size, same person - and take it for its own.
    /// </summary>
    private readonly ConcurrentDictionary<TimeItem, SemaphoreSlim> _timeGates = new();

    /// <summary>
    /// The last change this session sent to a work item that came back unanswered and is not
    /// settled yet. The gate above is given up when that happens - it has to be, or the check
    /// that would settle it could never run - so the next change to the same item can be
    /// planned from a revision that the earlier one is still about to move. Remembered here so
    /// that when both end up pinned to the same revision, neither claims the one revision the
    /// two of them are competing for.
    ///
    /// This session's sends only. One left behind by a previous copy lives in the plan file,
    /// and is settled through <see cref="CheckTimeWriteAsync"/> before it can matter.
    /// </summary>
    private readonly ConcurrentDictionary<TimeItem, TimeWritePlan> _unanswered = new();

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
        var item = Item(plan.WorkItemId);
        var gate = _timeGates.GetOrAdd(item, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var (landing, now, _) = await SettleAsync(plan, ct);

            // Only these two mean the work item itself has spoken: it made the revision this
            // was pinned to make, or it has moved past that revision without it. The expired
            // answer below is this copy giving up on the wait, and the item standing still
            // says nothing at all - so nothing is forgotten on the strength of it.
            if (landing is Landing.Landed or Landing.Lost) ForgetUnanswered(item, plan);

            return landing switch
            {
                Landing.Landed => (Landed: (bool?)true, Result: ResultFrom(plan, now!)),
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
    /// Stops watching for a change once the work item has settled it either way.
    ///
    /// Only ever this very change: a plan left behind by a previous session and one this
    /// session sent can be pinned to the same revision and describe the same move, and
    /// settling the old one says nothing whatever about the live one. Matched by identity,
    /// because that is the question - not "a change like this one" but "this one".
    /// </summary>
    private void ForgetUnanswered(TimeItem item, TimeWritePlan settled)
    {
        if (_unanswered.TryGetValue(item, out var held) && ReferenceEquals(held, settled))
            _unanswered.TryRemove(item, out _);
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
        // Worked out once, so that an organization switched while this is in flight cannot
        // file the answer under a different item than the one the wait was taken out on.
        var item = Item(id);

        var gate = _timeGates.GetOrAdd(item, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Whatever the last change to this item left unanswered is put to the work item
            // first, so this one is planned from where the item really stands rather than
            // from a revision the other is still about to move.
            await SettleUnansweredAsync(item, ct);

            var result = await AdjustTimeLockedAsync(id, completedDelta, remainingDelta, pinning, ct);

            // A change that was actually sent and landed made a revision of its own, so
            // anything still pinned behind it had its chance and missed it. The one return
            // that sends nothing - an undo worked out against an item already at zero - comes
            // back without a plan and makes no revision, so a straggler behind it is still
            // free to land and stays remembered.
            if (result.Plan is not null) _unanswered.TryRemove(item, out _);
            return result;
        }
        catch (TimeWriteUnconfirmedException ex)
        {
            // Still out there, and still able to land at any moment until the item moves past
            // the revision it is pinned to.
            _unanswered[item] = ex.Plan;
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Looks once at a change to this work item that went out unanswered, and forgets it when
    /// the item itself has settled it either way. What cannot be settled stays remembered: it
    /// is still free to land, and the next change is planned knowing that.
    /// </summary>
    private async Task SettleUnansweredAsync(TimeItem item, CancellationToken ct)
    {
        if (!_unanswered.TryGetValue(item, out var earlier)) return;

        var (landing, _, _) = await SettleAsync(earlier, ct);

        // The expired answer counts here, unlike in CheckTimeWriteAsync: what is being given
        // up on is this very pin, which is the one thing its own wait has any say over.
        if (landing is Landing.Landed or Landing.Lost
            || (landing is Landing.NotYet && !earlier.CouldStillLand))
            _unanswered.TryRemove(item, out _);
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

        // It moved the field this change moves, exactly as this change would - but left the
        // other one somewhere this change would not have. Ours with a process rule on top of
        // it, or somebody else's edit that happens to line up; either way, filing this as ours
        // would write down an amount the work item does not agree with, and a later Undo would
        // then put back hours that were never taken off. Not claimed, and not ruled out.
        if (!revision.RemainingAgrees(plan))
            return Undecided(plan, now,
                $"revision {plan.Rev + 1} of #{plan.WorkItemId} makes the change this one makes, but "
                + (revision.RemainingChanged
                    ? $"moved Remaining Work from {revision.RemainingOld ?? 0:0.##}h to "
                      + $"{revision.RemainingNew ?? 0:0.##}h"
                    : $"left Remaining Work at {plan.RemainingBefore:0.##}h")
                // Said of what this change does to the field rather than of whether it writes
                // it. Our patch carries Remaining Work whenever the change sets it at all -
                // including when what it sets is the value already there, which is what
                // reducing remaining on an item that has never had any comes to - and calling
                // that "moves it from 0h to 0h" describes a re-write as a move.
                + (!plan.SetsRemaining
                    ? ", which this change does not touch"
                    : Same(plan.RemainingBefore, plan.RemainingAfter)
                        ? $", where this change leaves it at {plan.RemainingAfter:0.##}h"
                        : $", where this change moves it from {plan.RemainingBefore:0.##}h to "
                          + $"{plan.RemainingAfter:0.##}h"));

        // The same question the other way round, for a change that moves Remaining Work alone -
        // an undo whose Completed Work delta clamped to nothing, because the work item's
        // Completed Work is already at nothing. Filing that revision as ours would write down
        // an applied Completed of zero against a revision that did move Completed Work, the
        // sweep would let go of whatever really made that move, and a later Undo would put the
        // zero back: the field left permanently out by the difference.
        if (!revision.CompletedAgrees(plan))
            return Undecided(plan, now,
                $"revision {plan.Rev + 1} of #{plan.WorkItemId} makes the change this one makes, but also "
                + $"moved Completed Work to {revision.CompletedNew ?? 0:0.##}h, which this change leaves "
                + $"at {plan.CompletedAfter:0.##}h");

        // Another change of ours, pinned to the same revision, that went out without an answer
        // and could still be the one sitting here. Only one of the two can have made it, and
        // nothing on the revision itself can say which, so neither may claim it.
        if (_unanswered.TryGetValue(Item(plan.WorkItemId), out var other)
            && !ReferenceEquals(other, plan) && other.Rev == plan.Rev && revision.Made(other))
            return Undecided(plan, now,
                $"an earlier change to #{plan.WorkItemId} pinned to the same revision went out without an "
                + "answer and makes the same change, so which of the two this revision is cannot be told");

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
        var mine = await TryReadMyNamesAsync(refresh: false, ct);
        if (Mismatched(revision, mine))
        {
            // What is held may have been read for an account this connection no longer uses.
            // Read again before a change of ours is put down to somebody else - that reading
            // is the one that leads to hours on the work item being discarded from here.
            //
            // Asked of connectionData itself rather than through the names, because a read
            // that failed comes back as no names at all - which is never a mismatch - and a
            // 401 while a token renews would turn the one piece of evidence that says "not
            // ours" into a claim on somebody else's hours. A reading that could not be had
            // says nothing either way, so the mismatch already in hand stands.
            var reread = await ReadConnectionAsync(refresh: true, ct);

            if (reread is null || Mismatched(revision, reread.Names))
                return Undecided(plan, now,
                    $"revision {plan.Rev + 1} of #{plan.WorkItemId} made the change, but Azure DevOps says it was made by "
                    + $"[{string.Join(", ", revision.Names)}] and this sign-in answers to "
                    + $"[{string.Join(", ", reread?.Names ?? mine)}]");
        }

        return (Landing.Landed, now, null);
    }

    /// <summary>
    /// True when the service plainly names somebody else as the hand behind a revision. Names
    /// neither side gave say nothing either way, and are never a mismatch.
    /// </summary>
    private static bool Mismatched(RevisionChange revision, HashSet<string> mine) =>
        mine.Count > 0 && revision.Names.Count > 0 && !revision.Names.Overlaps(mine);

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
    /// Whether two readings of an hours field are the same amount. The service keeps them as
    /// floating point, so exact equality answers no to two numbers that are the same to any
    /// precision a timesheet has. Used by the reasoning below and by what it says afterwards,
    /// so that a change which only re-writes a field is never described as moving it.
    /// </summary>
    private static bool Same(double a, double b) => Math.Abs(a - b) < 0.001;

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
        /// account here, because a false answer from this one is a hard "never went on": the
        /// change is worked out again and sent, and hours already on the item go on twice.
        /// What each field did is asked separately, by <see cref="RemainingAgrees"/> and
        /// <see cref="CompletedAgrees"/>, where a no leaves the question open instead of
        /// answering it.
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

        /// <summary>
        /// True when this revision also left Remaining Work where the change would have.
        ///
        /// Judged on where the field ended up rather than on whether the revision touched it.
        /// Our patch carries Remaining Work whenever the change sets it at all - including when
        /// what it sets is the value already there, which is what booking against a work item
        /// that has never had a Remaining Work comes to, both ends being nothing - and Azure
        /// DevOps records that as a change of the field. Asking whether the field was touched
        /// therefore refused the very revision our own PATCH had made.
        ///
        /// Both directions still matter, because the applied amounts are what an Undo puts
        /// back: an entry filed for a revision that moved Remaining Work by something other
        /// than what the entry says would leave that field permanently out by the difference.
        /// </summary>
        public bool RemainingAgrees(TimeWritePlan plan) =>
            RemainingChanged
                ? Same(RemainingOld ?? 0, plan.RemainingBefore) && Same(RemainingNew ?? 0, plan.RemainingAfter)
                : Same(plan.RemainingBefore, plan.RemainingAfter);

        /// <summary>
        /// The same for Completed Work, which matters in the direction <see cref="Made"/>
        /// answers on Remaining alone: an undo whose Completed delta clamped to zero, against
        /// an item whose Completed Work is already nothing. Nothing there held Completed to
        /// account, so a revision that moved Remaining exactly as the change would and also
        /// moved Completed Work was claimed as ours, filed with an applied Completed of zero.
        /// </summary>
        public bool CompletedAgrees(TimeWritePlan plan) =>
            CompletedChanged
                ? Same(CompletedOld ?? 0, plan.CompletedBefore) && Same(CompletedNew ?? 0, plan.CompletedAfter)
                : Same(plan.CompletedBefore, plan.CompletedAfter);
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
    /// What connectionData said, for the connection it was read from: the organization's own
    /// id, the display name of the account, and every name that account answers to.
    ///
    /// One object, swapped in whole, because the parts have to agree with each other. Kept as
    /// two fields assigned one after the other - and read from calls holding different locks -
    /// it let a reader pair one connection's key with another's names, and whose hand a
    /// revision was is exactly what that pairing decides.
    /// </summary>
    private sealed record ConnectionIdentity(
        string Key, string Org, string InstanceId, string DisplayName, HashSet<string> Names);

    private volatile ConnectionIdentity? _connection;

    /// <summary>
    /// The id Azure DevOps gives the organization this client is pointed at, once
    /// connectionData has been read for it; empty until then, and empty again the moment the
    /// URL moves on. Stamped onto time entries because it is the one part of an organization
    /// that a rename, Microsoft's move to dev.azure.com or a new server address leaves alone.
    /// </summary>
    public string OrganizationId => _connection is { } held && held.Org == OrgUrl ? held.InstanceId : "";

    /// <summary>Forgets who this connection is, for when the credential behind it changes.</summary>
    private void ForgetConnectionIdentity() => _connection = null;

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
    ///
    /// Remembered for as long as the organization, the way of signing in and the account
    /// itself all stay put. Null when it could not be read, which simply means the identity
    /// has no say in whether a change was ours.
    /// </summary>
    private async Task<ConnectionIdentity?> ReadConnectionAsync(bool refresh, CancellationToken ct)
    {
        var org = OrgUrl;
        var key = $"{org}|{settings.Current.Ado.AuthMode}|{await CredentialKeyAsync()}";

        // Read once into a local: what is swapped in below is complete before anyone sees it.
        var held = _connection;
        if (!refresh && held is not null && held.Key == key) return held;

        try
        {
            using var doc = await SendAsync(HttpMethod.Get,
                $"{org}/_apis/connectionData?api-version={ApiVersion}-preview", null, ct);

            var names = NewNameSet();
            var display = "";

            foreach (var which in ConnectionIdentities)
            {
                if (!doc.RootElement.TryGetProperty(which, out var user)) continue;

                AddNames(names, user, "id", "descriptor", "subjectDescriptor", "uniqueName");

                // The sign-in address lives under properties.Account as a typed value.
                if (user.TryGetProperty("properties", out var properties)
                    && properties.ValueKind == JsonValueKind.Object
                    && properties.TryGetProperty("Account", out var account))
                    AddNames(names, account, "$value");

                // authenticatedUser comes first, so its name is the one shown.
                if (display.Length == 0
                    && user.TryGetProperty("providerDisplayName", out var name)
                    && name.ValueKind == JsonValueKind.String)
                    display = name.GetString() ?? "";
            }

            var read = new ConnectionIdentity(key, org, Str(doc.RootElement, "instanceId"), display, names);
            _connection = read;
            return read;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CrashLog.WriteLine($"Could not read who this sign-in is from {org}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// What tells one account on an organization from another. A personal access token is
    /// taken by its fingerprint rather than its text: two tokens are two people, and only one
    /// of them made any given revision. An Entra sign-in is told apart by the account signed in.
    ///
    /// Without this, swapping a token for a colleague's - or signing out and back in as
    /// somebody else, which changes neither the URL nor the way of signing in - left the
    /// previous account's names in hand for the rest of the session. A change that really was
    /// ours would then read as somebody else's, and worse, one of theirs could be claimed as
    /// ours and an entry filed for hours nobody here booked.
    /// </summary>
    private async Task<string> CredentialKeyAsync()
    {
        var ado = settings.Current.Ado;
        if (ado.AuthMode != AdoAuthMode.Entra)
            return "pat:" + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(ado.PersonalAccessToken)))[..16];

        return "entra:" + ((await auth.GetStatusAsync()).Username ?? "");
    }

    private async Task<HashSet<string>> TryReadMyNamesAsync(bool refresh, CancellationToken ct) =>
        (await ReadConnectionAsync(refresh, ct))?.Names ?? NewNameSet();

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
