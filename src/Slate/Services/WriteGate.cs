namespace Slate.Services;

/// <summary>
/// Counts the writes under way - to Azure DevOps, to Outlook, to the plan and to the settings -
/// and closes to new ones while an update hands this copy of the app over to a new one.
///
/// A write holds its count from before it changes anything until everything it changes is
/// written down, remotely and locally both. That is what lets a handover that has waited for
/// the count to drain promise nothing is half done: no booking in Azure DevOps that the plan
/// does not know about yet, no event in Outlook whose id is not yet on disk, and no edit in
/// memory that has still to be saved - any of which the new copy would either lose or make a
/// second time.
///
/// The plan is edited in memory and saved after, so its count is taken by whoever makes the
/// edit, before making it (<see cref="Planning.AppState.TryEdit"/> and friends). The settings
/// are only ever written in one step, so <see cref="Storage.SettingsStore"/> counts its own.
/// </summary>
public sealed class WriteGate
{
    /// <summary>Why a write was turned away while the gate is closed.</summary>
    public const string ClosedReason = "Slate is restarting to install an update.";

    private volatile bool _closed;
    private int _inFlight;

    /// <summary>True from the start of a handover until it is abandoned.</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Counts a write in, unless the gate is closed; every true must be paired with an
    /// <see cref="Exit"/>. Counting first and looking at the flag second - the mirror image of
    /// <see cref="Close"/> followed by <see cref="WaitForWritesAsync"/> - means a write starting
    /// at the same instant as a handover is either refused here or waited for there, whichever
    /// threads the two are on.
    /// </summary>
    public bool TryEnter()
    {
        Interlocked.Increment(ref _inFlight);
        if (!_closed) return true;

        Interlocked.Decrement(ref _inFlight);
        return false;
    }

    public void Exit() => Interlocked.Decrement(ref _inFlight);

    /// <summary>Turns every new write away from now on. What is already under way carries on.</summary>
    public void Close()
    {
        _closed = true;

        // Seen by every thread before the count is next read; see TryEnter.
        Interlocked.MemoryBarrier();
    }

    /// <summary>
    /// Waits for the writes already under way when the gate closed. False when something is
    /// still going at <paramref name="timeout"/>, which is more likely stuck than slow.
    /// </summary>
    public async Task<bool> WaitForWritesAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _inFlight) > 0)
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(100);
        }

        return true;
    }

    public void Open() => _closed = false;
}
