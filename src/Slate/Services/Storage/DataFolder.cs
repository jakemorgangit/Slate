namespace Slate.Services.Storage;

/// <summary>
/// Every write into the data folder goes through here, so an update can stop this copy writing
/// there at all from the moment it starts the copy that is replacing it.
///
/// The new copy reads the plan and the settings as it starts and from then on writes them from
/// what it holds in memory, so anything this copy wrote afterwards would either never reach it
/// or land on top of what it had written. Stopping every edit before the swap is not enough on
/// its own for that: a work item refresh already on its way when the update began still ends
/// by writing the plan and the cached list. So while frozen nothing is written; the latest
/// write asked for is kept, one per file, and made if the update is undone and this copy
/// carries on. If the update goes ahead they are dropped with this copy - by then only writes
/// the new copy makes again for itself can be left (a refreshed title on a block, the cached
/// work item list), because every change a person makes is finished or refused before any
/// file moves.
///
/// The crash log is left out on purpose: it is only ever appended to, never read back into the
/// app, and it is where the handover itself records how it went.
/// </summary>
public static class DataFolder
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Action> Held = new(StringComparer.OrdinalIgnoreCase);
    private static bool _frozen;

    /// <summary>
    /// Makes <paramref name="write"/> now or, while frozen, keeps it for <see cref="Thaw"/> in
    /// place of whatever was kept for the same file - only the newest matters. It must write
    /// what the file should hold by the time it runs, and take no lock of its own that is ever
    /// held by something waiting to come through here.
    ///
    /// Writes are made one at a time under the gate, which is what lets <see cref="Freeze"/>
    /// wait out one already under way rather than have it land after the new copy has started.
    /// </summary>
    public static void Write(string file, Action write)
    {
        lock (Gate)
        {
            if (_frozen)
            {
                Held[file] = write;
                return;
            }

            // A write made now overtakes one still kept from a freeze: it is the newer of the two.
            Held.Remove(file);
            write();
        }
    }

    /// <summary>
    /// Replaces a file here whole: written to a temp file beside it, then renamed over it, so a
    /// crash part way through never leaves half a file for the next read. The rename is retried
    /// for a moment, because a virus scanner, a backup or the search indexer reading the file
    /// just then makes Windows refuse to replace it - and a save refused for that would throw
    /// out of whatever edit asked for it.
    /// </summary>
    public static void Replace(string path, string contents) =>
        Replace(path, temp => File.WriteAllText(temp, contents));

    /// <inheritdoc cref="Replace(string, string)"/>
    public static void Replace(string path, byte[] contents) =>
        Replace(path, temp => File.WriteAllBytes(temp, contents));

    /// <summary>Eight tries at it, however long each wait between them is.</summary>
    private const int ReplaceAttempts = 8;

    private static void Replace(string path, Action<string> writeTemp)
    {
        AppPaths.EnsureCreated();

        var temp = path + ".tmp";

        var onWindowThread = System.Windows.Application.Current?.Dispatcher.CheckAccess() == true;
        var written = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Retried alongside the rename, not once before it: a leftover .tmp from an
                // interrupted write that a scanner or a backup is holding stops the write just
                // as surely as something holding the file itself, and this was the one step
                // here that got no second chance. Kept once it is written, so a rename that
                // fails does not have the file serialized out again behind it.
                if (!written)
                {
                    writeTemp(temp);
                    written = true;
                }

                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < ReplaceAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(Pause(attempt, onWindowThread));
            }
        }
    }

    /// <summary>
    /// How long to wait before trying again. Backing off to about a second and a half in all,
    /// which is long enough for a scan of the file to finish - except on the window's own
    /// thread, where a save holds that thread and with it the gate above for the whole of it:
    /// a second and a half there is a frozen window with the MSAL token write queued behind it.
    /// So that waits flat and briefly instead, about a fifth of a second in all, which still
    /// covers the usual case of a scanner opening the file just written; what it does not cover
    /// is reported as the failure it is rather than waited out with the window stopped.
    /// </summary>
    private static int Pause(int attempt, bool onWindowThread) => onWindowThread ? 30 : 50 * attempt;

    /// <summary>
    /// Stops every write from here on. Returns only once any write already under way has
    /// finished, so the caller can start the new copy knowing nothing more will land.
    /// </summary>
    public static void Freeze()
    {
        lock (Gate) _frozen = true;
    }

    /// <summary>
    /// Lets writes through again and makes the ones kept while frozen, for when the update was
    /// undone and nothing else is using the folder any more. One that fails is logged and the
    /// rest still go: a rollback must never be what stops this copy saving.
    /// </summary>
    public static void Thaw()
    {
        lock (Gate)
        {
            if (!_frozen) return;
            _frozen = false;

            foreach (var (file, write) in Held)
            {
                try
                {
                    write();
                }
                catch (Exception ex)
                {
                    CrashLog.WriteLine($"Could not write {Path.GetFileName(file)} after an update was undone: {ex}");
                }
            }

            Held.Clear();
        }
    }
}
