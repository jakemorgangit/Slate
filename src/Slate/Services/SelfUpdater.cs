using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Windows;
using Slate.Services.Storage;

namespace Slate.Services;

public enum UpdatePhase { Idle, Downloading, Verifying, Installing }

/// <summary>An install that did not happen. The message is written for the person using the app.</summary>
public sealed class SelfUpdateException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    /// False when the failure left something behind that the message already explains - the new
    /// version still standing where Slate runs from, or nothing there at all. The reassuring
    /// "nothing was changed" a caller otherwise adds would flatly contradict it.
    /// </summary>
    public bool NothingChanged { get; init; } = true;
}

/// <summary>
/// An install stopped part way because this copy of Slate was on its way out, with everything
/// put back. A cancellation rather than a failure - the failure path would open the release
/// page in a browser while Windows is signing out - but one with something of its own to say
/// about what stopped it, which is why it is not a plain <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class UpdateInterruptedException(string message) : OperationCanceledException(message);

/// <summary>
/// Replaces the running .exe with the release GitHub reports as newer, then restarts into it.
///
/// Slate is a single file kept wherever the user put it, not something an installer owns, so
/// the update happens in place: the release asset of the same flavour is downloaded next to
/// the running .exe, checked against the SHA-256 GitHub published for it, and swapped in.
/// Windows lets a running .exe be renamed but not overwritten, so the running copy steps
/// aside to "&lt;name&gt;.old", the download takes its name, and the new copy is started from
/// the original path - which keeps shortcuts and pins working.
///
/// Every step that can fail before the old copy exits is undone if it does - including the old
/// copy being ended from outside, by Windows signing out, while the new one is still starting -
/// so the original path holds a working Slate again. Nothing is ever waited out while that path
/// stands empty, and where putting the old copy back turns out to be impossible the new one is
/// left there instead and said so, rather than reported as a rollback that happened. Ended
/// before the swap, the install stops there instead and moves nothing at all.
///
/// Settings and the plan live in the data
/// folder and are written as they change. Before any file moves the old copy finishes whatever
/// it was writing and refuses anything new, and from the moment it starts the new copy it
/// writes nothing into the data folder at all unless the update is undone, so restarting
/// neither loses a change nor writes one over the new copy's.
/// </summary>
public sealed class SelfUpdater
{
    /// <summary>Tells the new copy it was started by an update, and from which version.</summary>
    private const string UpdatedFromFlag = "--updated-from";

    /// <summary>Names the event the new copy sets once its window has rendered.</summary>
    private const string ReadyFlag = "--update-ready";

    /// <summary>Where a release download starts. Anything else is not one of this repository's files.</summary>
    private const string DownloadPathPrefix = "/jakemorgangit/Slate/releases/download/";

    /// <summary>The hosts GitHub redirects release downloads to.</summary>
    private static readonly string[] AssetHosts =
        ["objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>Well above the standalone build; a file bigger than this is not ours.</summary>
    private const long MaxDownloadBytes = 512L * 1024 * 1024;

    /// <summary>How long the download may go without a single byte before it is given up on.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the new copy has to show its window before the update is undone. Generous,
    /// because a cold start of the standalone build with a virus scanner reading all of it
    /// can be slow; a healthy start takes a few seconds.
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Redirects are followed by hand so every hop can be checked against the hosts above,
    /// rather than trusting wherever a Location header points. IPv4 first for the same reason
    /// as Azure DevOps: some networks accept IPv6 and then reset it mid-handshake. There is no
    /// overall timeout because the standalone build is large; <see cref="StallTimeout"/> is
    /// the guard instead.
    /// </summary>
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = PreferIPv4.ConnectAsync,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private CancellationTokenSource? _cancel;
    private DateTime _lastReportedAt;

    public UpdatePhase Phase { get; private set; }

    public long Received { get; private set; }

    public long Total { get; private set; }

    public int Percent => Total > 0 ? (int)Math.Clamp(Received * 100 / Total, 0, 100) : 0;

    public bool IsBusy => Phase != UpdatePhase.Idle;

    /// <summary>Only while downloading or checking: once files start moving, the swap runs to the end.</summary>
    public bool CanCancel => Phase is UpdatePhase.Downloading or UpdatePhase.Verifying;

    public event Action? Changed;

    // ---------------------------------------------------------------- what can be installed

    /// <summary>
    /// The running .exe, when this copy is a single-file publish that could be swapped for
    /// another. A build with loose files next to it (dotnet build, dotnet run) would be left
    /// half old and half new by replacing only the .exe, so it never qualifies.
    /// </summary>
    private static string? ExePath =>
        IsSingleFile
        && Environment.ProcessPath is { Length: > 0 } exe
        && File.Exists(exe)
            ? exe
            : null;

    /// <summary>An assembly bundled into a single-file .exe has no path of its own.</summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "The empty Location that IL3000 warns about is exactly what is being tested for.")]
    private static bool IsSingleFile => string.IsNullOrEmpty(typeof(SelfUpdater).Assembly.Location);

    /// <summary>
    /// The asset this copy would install for <paramref name="release"/>: the same flavour and
    /// runtime, with a checksum to hold it to. Null means "open the release page instead".
    /// </summary>
    private static ReleaseAsset? AssetFor(ReleaseInfo release) =>
        release.AssetFor(AppInfo.ReleaseAssetName(release.Version)) is { Sha256.Length: > 0 } asset ? asset : null;

    /// <summary>True when "Install and restart" can be offered for this release.</summary>
    public bool CanInstall(ReleaseInfo? release) =>
        release is not null && ExePath is not null && AssetFor(release) is not null;

    // ---------------------------------------------------------------- install

    /// <summary>
    /// Downloads, verifies and swaps in <paramref name="release"/>, starts it, and shuts this
    /// copy down once the new one is up. Throws <see cref="SelfUpdateException"/> with
    /// something to tell the user when it cannot, having put everything back as it was, or
    /// <see cref="OperationCanceledException"/> after <see cref="Cancel"/> - or once
    /// <see cref="SettleBeforeExit"/> has put everything back because this copy is ending.
    /// </summary>
    /// <param name="beforeSwap">
    /// Runs once the download has checked out and before any file moves: brings the rest of
    /// the app to a standstill, so nothing is cut off halfway by the shutdown at the end or
    /// written after the new copy has read it. False abandons the install.
    /// </param>
    /// <param name="onRolledBack">
    /// Runs when an update that had already swapped the files is put back, beside the thaw of
    /// the data folder: it lets the rest of the app out of the standstill
    /// <paramref name="beforeSwap"/> put it in. Called from wherever the rollback happened, so
    /// it must take no lock this class's callers hold and must not wait on anything. The caller
    /// undoes the standstill itself for everything that fails before the files move - there is
    /// no handover to roll back then - so this is only for the stretch afterwards, where the
    /// rollback can happen on a thread the install is no longer waiting on.
    /// </param>
    public async Task InstallAsync(
        ReleaseInfo release, Func<Task<bool>>? beforeSwap = null, Action? onRolledBack = null)
    {
        if (IsBusy) return;

        var exe = ExePath ?? throw new SelfUpdateException("This copy of Slate cannot replace itself.");
        var name = AppInfo.ReleaseAssetName(release.Version);
        var asset = release.AssetFor(name)
                    ?? throw new SelfUpdateException($"The release has no {name ?? "matching download"}.");
        if (asset.Sha256.Length == 0)
            throw new SelfUpdateException("GitHub gave no checksum for the download, so there is nothing to check it against.");
        if (asset.Size > MaxDownloadBytes)
            throw new SelfUpdateException("The download is far larger than a Slate release should be.");

        var download = exe + ".download";
        var old = exe + ".old";

        // Out here so the catch below can ask what the swap and any rollback actually left at
        // the original path, rather than judging it by something merely being there.
        Handover? handover = null;

        _cancel = new CancellationTokenSource();
        _cleanupStopped = true;
        Received = 0;
        Total = asset.Size;
        SetPhase(UpdatePhase.Downloading);

        try
        {
            await DownloadAsync(asset, download, _cancel.Token).ConfigureAwait(false);

            SetPhase(UpdatePhase.Verifying);
            await VerifyAsync(download, asset.Sha256, _cancel.Token).ConfigureAwait(false);

            SetPhase(UpdatePhase.Installing);
            _handingOver = true;

            if (beforeSwap is not null && !await beforeSwap().ConfigureAwait(false))
                throw new SelfUpdateException(
                    "Slate was still in the middle of saving a change or signing in, and restarting now would have cut it off.");

            // Both on a pool thread, whichever thread got this far: they block while holding
            // the handover lock, and the window's own thread has to stay free to answer
            // Windows - which, when signing out, waits on that same lock (SettleBeforeExit).
            handover = new Handover(exe, download, old, release.Version, onRolledBack);
            await Task.Run(() => SwapIn(handover)).ConfigureAwait(false);
            await Task.Run(() => StartAndWaitForNewCopyAsync(handover)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Only once a working Slate is known to be back at the original path. Something
            // being there is not that: a rollback that could only leave the new, unproven copy
            // - or nothing at all - makes this verified download the one Slate left to recover
            // from, and throwing it away would take that away too. Read under the lock the
            // rollback sets it under, since the rollback may have happened on another thread.
            Restored restored;
            lock (HandoverLock) restored = handover?.Restored ?? Restored.OldVersion;
            if (restored == Restored.OldVersion) TryDelete(download);

            // Everything that could be put back has been by now.
            _handingOver = false;
            SetPhase(UpdatePhase.Idle);

            if (ex is OperationCanceledException) throw;

            CrashLog.WriteLine($"Updating to {release.Version} failed: {ex}");
            if (ex is SelfUpdateException) throw;
            throw new SelfUpdateException("Something unexpected got in the way.", ex);
        }
        finally
        {
            // Not disposed: Cancel may be reading it on the UI thread at this very moment,
            // and a source with no timer holds nothing that needs releasing.
            _cancel = null;
        }

        // Out here, past the catch above: with the new copy up, nothing that happens from
        // now on may put the old one back or let this copy write again.
        CrashLog.WriteLine($"Updating from {AppInfo.Version} to {release.Version}; handing over to the new copy.");

        // Queued rather than waited for: the window's thread may already be on its way out -
        // Windows signing out is one way to arrive here - and there is nothing to wait for.
        _ = Application.Current?.Dispatcher.InvokeAsync(() => Application.Current?.Shutdown());
    }

    public void Cancel()
    {
        if (CanCancel) _cancel?.Cancel();
    }

    /// <summary>
    /// Straight into a file beside the running .exe, so the swap is a rename on the same
    /// volume rather than a copy that could be interrupted halfway. Creating the file first
    /// also finds out before a long download whether this folder can be written at all.
    /// </summary>
    private async Task DownloadAsync(ReleaseAsset asset, string path, CancellationToken ct)
    {
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SelfUpdateException(
                $"Slate cannot write to the folder it runs from ({Path.GetDirectoryName(path)}), so it cannot replace itself there.", ex);
        }

        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var url))
            throw new SelfUpdateException("The release lists a download address that is not one.");

        await using (file)
        {
            using var response = await GetFollowingRedirectsAsync(url, ct);
            if (response.Content.Headers.ContentLength is { } length && asset.Size > 0 && length != asset.Size)
                throw new SelfUpdateException("GitHub is serving a different size of file than the release lists.");

            var limit = asset.Size > 0 ? asset.Size : MaxDownloadBytes;
            if (Total <= 0 && response.Content.Headers.ContentLength is { } announced) Total = announced;

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920];

            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                int read;
                try
                {
                    read = await body.ReadAsync(buffer, stall.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new SelfUpdateException("The download stopped arriving.");
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    throw new SelfUpdateException("The connection dropped during the download.", ex);
                }

                if (read == 0) break;
                if (Received + read > limit)
                    throw new SelfUpdateException("The download is larger than the release says it should be.");

                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                Received += read;
                ReportProgress();
            }

            if (asset.Size > 0 && Received != asset.Size)
                throw new SelfUpdateException("The download finished early.");

            await file.FlushAsync(ct);
            file.Flush(flushToDisk: true);
        }

        ReportProgress(force: true);
    }

    private static async Task<HttpResponseMessage> GetFollowingRedirectsAsync(Uri url, CancellationToken ct)
    {
        for (var hop = 0; hop < 6; hop++)
        {
            if (!IsAllowed(url, firstHop: hop == 0))
                throw new SelfUpdateException($"The download pointed somewhere other than GitHub ({url.Host}), so it was refused.");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Slate", AppInfo.Version));

            // Headers must arrive within the stall window too; the body is timed separately.
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headers.CancelAfter(StallTimeout);

            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SelfUpdateException("GitHub did not answer in time.");
            }
            catch (HttpRequestException ex)
            {
                throw new SelfUpdateException("GitHub could not be reached.", ex);
            }

            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new SelfUpdateException("GitHub redirected the download to nowhere.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new SelfUpdateException($"GitHub refused the download (HTTP {status}).");
            }

            return response;
        }

        throw new SelfUpdateException("GitHub redirected the download too many times.");
    }

    /// <summary>
    /// HTTPS to this repository's release downloads, then only to GitHub's own asset hosts.
    /// The Debug-only test feed may also serve from its own origin, so the whole flow can be
    /// tried against a local listener; in a Release build <see cref="UpdateChecker.TestFeed"/>
    /// is always null and this relaxation does not exist.
    /// </summary>
    private static bool IsAllowed(Uri url, bool firstHop)
    {
        if (UpdateChecker.TestFeed is { } feed
            && Uri.Compare(url, feed, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            return true;

        if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length > 0) return false;

        var isReleasePath = url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                            && url.AbsolutePath.StartsWith(DownloadPathPrefix, StringComparison.OrdinalIgnoreCase);

        return firstHop
            ? isReleasePath
            : isReleasePath || AssetHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hashes what actually landed on disk rather than what went past in memory, so anything
    /// that touched the file after it was written is caught too.
    /// </summary>
    private static async Task VerifyAsync(string path, string expected, CancellationToken ct)
    {
        byte[] hash;
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            hash = await SHA256.HashDataAsync(file, ct);

        if (!string.Equals(Convert.ToHexStringLower(hash), expected, StringComparison.Ordinal))
            throw new SelfUpdateException(
                "The download did not match the checksum GitHub published for it, so it was thrown away.");
    }

    // ---------------------------------------------------------------- swap

    /// <summary>
    /// An update whose files have been swapped but which has not yet either handed over to the
    /// new copy or been put back. Held under <see cref="HandoverLock"/> from the moment the
    /// .exe changes until one of those happens, so whichever gets there first - the new copy
    /// saying it is up, the wait for it giving up, or this copy being ended from outside - is
    /// the only one that acts on it.
    /// </summary>
    private sealed class Handover(string exe, string download, string old, string version, Action? onRolledBack)
    {
        public string Exe { get; } = exe;
        public string Download { get; } = download;
        public string Old { get; } = old;
        public string Version { get; } = version;

        /// <summary>Lets the rest of the app carry on once this was put back. See InstallAsync.</summary>
        public Action? OnRolledBack { get; } = onRolledBack;

        /// <summary>The new copy, once it has been started.</summary>
        public Process? Child { get; set; }

        /// <summary>What the new copy sets once its window is up.</summary>
        public EventWaitHandle? Ready { get; set; }

        public HandoverOutcome Outcome { get; set; }

        /// <summary>Why it was put back from outside the wait, when it was.</summary>
        public string? Interruption { get; set; }

        /// <summary>
        /// What the last attempt to put things back actually left at <see cref="Exe"/>. It
        /// starts as the old version because that is what is there before anything moves; a
        /// swap or a rollback that could not put it back says so here. Both the message the
        /// user is given and the decision whether the verified download may be thrown away go
        /// by this rather than by something merely existing at the path. Written and read
        /// under <see cref="HandoverLock"/>.
        /// </summary>
        public Restored Restored { get; set; } = Restored.OldVersion;
    }

    private enum HandoverOutcome { Pending, HandedOver, RolledBack }

    /// <summary>What is at the original .exe path after something had to be put back there.</summary>
    private enum Restored
    {
        /// <summary>The version this copy is running is back under its own name: the update is undone.</summary>
        OldVersion,

        /// <summary>
        /// The new, unproven copy is there instead - a newer Slate beats none, but it is not
        /// what a rollback promises, and the working copy is only beside it as .old.
        /// </summary>
        NewVersion,

        /// <summary>Nothing is at the original path at all.</summary>
        Nothing,
    }

    /// <summary>Guards <see cref="_unconfirmed"/> and every step that swaps, starts or puts back.</summary>
    private static readonly Lock HandoverLock = new();

    private static Handover? _unconfirmed;

    /// <summary>
    /// Why this copy is on its way out, from the moment <see cref="SettleBeforeExit"/> is
    /// first called: it is what stops an install that has not swapped anything yet from
    /// starting to.
    ///
    /// Volatile rather than held under <see cref="HandoverLock"/>, because it has to be
    /// recorded before that lock is so much as asked for - a rollback already running on a
    /// pool thread holds it, and is exactly the case this has to reach. Kept at the first
    /// reason given, and lifted again only by <see cref="SessionEndAbandoned"/>, for the one
    /// way a copy told it is ending does not end after all.
    /// </summary>
    private static volatile string? _exitingBecause;

    /// <summary>
    /// True while Windows is waiting on this copy's answer to a sign-out or shutdown before it
    /// goes on. Read by work already under way on another thread - a rollback the sign-out
    /// arrived in the middle of - so it can cut short every wait it was about to make; see
    /// <see cref="RollBack"/>.
    /// </summary>
    private static volatile bool _pressedToExit;

    /// <summary>
    /// Swaps the files and registers the handover in one hold of the lock, so there is no
    /// instant at which the new .exe is in place with nothing on record to put the old one back.
    /// </summary>
    private static void SwapIn(Handover handover)
    {
        lock (HandoverLock)
        {
            // Nothing may move once this copy has been told it is ending. The install runs on
            // a pool thread that no shutdown waits for, so the process can be torn down at any
            // point from here on: between the two renames in Swap, which would leave nothing
            // at Slate's own path, or after the new copy is started, which would leave the new
            // .exe in place and the good one only as .old with nobody left to put it back.
            // SettleBeforeExit covers that from the moment the swap is on record; this covers
            // the stretch before it, where all it would have found is an install still
            // downloading, checking, or waiting for writes in flight.
            // Interrupted rather than cancelled, so this wording is what reaches the user
            // rather than the flat "Update cancelled" a plain cancellation is reported as.
            if (_exitingBecause is { } why)
                throw new UpdateInterruptedException(
                    $"{why} before Slate {handover.Version} was swapped in, so nothing was changed.");

            Swap(handover);
            _unconfirmed = handover;
        }
    }

    /// <summary>
    /// Running .exe to .old, download to the .exe's name.
    ///
    /// Between those two renames the original path is empty, and that is the one state that is
    /// never waited in: if the second rename fails - a virus scanner or the indexer holding the
    /// freshly written download is the likeliest failure of the lot - the path is filled again
    /// before anything is backed off, with the old copy if it will go and the new one if it will
    /// not. Only once something is there is the next attempt waited for.
    /// </summary>
    private static void Swap(Handover handover)
    {
        var (exe, download, old) = (handover.Exe, handover.Download, handover.Old);

        try
        {
            if (File.Exists(old)) Retry(() => DeleteFile(old));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SelfUpdateException($"An earlier copy ({Path.GetFileName(old)}) is still in use, so there is nowhere to set this one aside.", ex);
        }

        _swapping = true;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(exe, old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Nothing has moved, so there is nothing to put back: the copy that is
                    // running is still at its own path.
                    if (attempt >= RenameAttempts || _pressedToExit)
                        throw new SelfUpdateException("Windows would not let Slate move its own file aside.", ex);
                    Thread.Sleep(150 * attempt);
                    continue;
                }

                try
                {
                    File.Move(download, exe);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The path is empty as of this instant, and nothing below waits until it
                    // is not; see FillEmptyPath.
                    var filled = FillEmptyPath(exe, old, download);
                    handover.Restored = filled;

                    // The new copy went in after all - which is the very rename that just
                    // failed, so the swap is done and there is nothing left to retry.
                    if (filled == Restored.NewVersion) return;

                    // Neither rename would go. Said plainly, because this is the one failure
                    // here that leaves the user with something to do by hand.
                    if (filled == Restored.Nothing)
                        throw Failed(handover, "Windows would not let the new version take Slate's place", ex);

                    if (attempt >= RenameAttempts || _pressedToExit)
                        throw new SelfUpdateException("Windows would not let the new version take Slate's place. Something may be scanning it.", ex);

                    Thread.Sleep(150 * attempt);
                }
            }
        }
        finally
        {
            _swapping = false;
        }
    }

    private static volatile bool _swapping;

    /// <summary>
    /// True for the instant the running .exe is renamed aside. The window refuses to close
    /// meanwhile, since ending the process between the two renames is the one way to leave
    /// the original path empty. Volatile, like its sibling <see cref="_handingOver"/>: it is
    /// set on the install's thread and read on the window's.
    /// </summary>
    public static bool IsSwapping => _swapping;

    private static volatile bool _handingOver;

    /// <summary>
    /// True from the moment the install can no longer be cancelled until this copy has either
    /// put everything back or handed over to the new copy - and after a handover, for the
    /// rest of its life.
    ///
    /// The window refuses to close meanwhile: this copy is what notices a new version that
    /// cannot start and puts the old one back, so if it goes before the new copy is up
    /// nothing is left to undo a bad update. A display driver reset does not restart the app
    /// meanwhile either, which would start a second copy beside the one this update starts
    /// or puts back. Left set after a handover because the shutdown that follows ignores a
    /// refused close, and nothing should restart a copy that is on its way out.
    /// </summary>
    public static bool IsHandingOver => _handingOver;

    /// <summary>
    /// Puts the old .exe back under its own name and says what it actually managed to leave at
    /// that path, which is not always what was asked for - the caller has to tell the truth
    /// about it rather than report a rollback that did not happen.
    ///
    /// <paramref name="displaced"/> is where a new copy standing at <paramref name="exe"/>
    /// goes; it doubles as where that copy then is, so it can be put back if the old one
    /// cannot be. Null when there is no new copy to move out of the way.
    ///
    /// Waiting happens only while something is at the original path: a scanner holding the
    /// file is gone a moment later, and the new copy standing there is at least a Slate. The
    /// instant the path is empty that stops - see <see cref="FillEmptyPath"/> - and the slow
    /// last resort below is skipped outright when Windows is waiting on this copy.
    /// </summary>
    /// <param name="attempts">
    /// How many tries the rename gets. Small when this copy is being ended by Windows and
    /// every one of those tries is time Windows is counting against it; see RollBack.
    /// </param>
    private static Restored Restore(string exe, string old, string? displaced, int attempts = RenameAttempts)
    {
        // Nothing at the path to begin with - a swap caught between its own two renames. There
        // is no time to spend on attempts and back-offs while that is true.
        if (!File.Exists(exe)) return FillEmptyPath(exe, old, displaced);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (displaced is not null) File.Move(exe, displaced, overwrite: true);
                File.Move(old, exe);
                return Restored.OldVersion;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Before anything else and before any waiting: if the new copy was moved out
                // of the way and the old one would not go back, the path every shortcut and
                // pin points at is empty at this instant.
                if (!File.Exists(exe)) return FillEmptyPath(exe, old, displaced);

                if (attempt >= attempts || _pressedToExit)
                {
                    CrashLog.WriteLine(
                        $"Could not rename {Path.GetFileName(old)} back over {Path.GetFileName(exe)} after a failed update: {ex}");
                    break;
                }

                Thread.Sleep(150 * attempt);
            }
        }

        // The path is not empty - the new copy is still standing at it - so there is time for
        // the one thing left that is not a rename. Not while Windows is waiting on this copy,
        // though: copying the standalone build takes seconds, and a newer Slate at the path
        // beats being ended part way through writing one.
        if (_pressedToExit) return Restored.NewVersion;

        return CopyOldBack(exe, old, attempts) ? Restored.OldVersion : Restored.NewVersion;
    }

    /// <summary>
    /// The original .exe path is empty, which is the one state this class must never wait in:
    /// every shortcut and pin points there, and a sign-out arriving now would leave the user
    /// with no Slate at all.
    ///
    /// So the two renames that could fill it are alternated - the old copy first, then the new
    /// one, since a newer Slate there beats none - over and over until one lands. Nothing is
    /// backed off: the tenth of a moment between rounds is there so this does not spin the
    /// disk, not to wait anything out, which is what has whichever file was being held land
    /// the instant it is let go rather than at the next attempt after that. Failing both
    /// inside <see cref="EmptyPathBudget"/>, a copy of the old one goes in instead - by the
    /// same rename as everything else here, so the path is filled in one step.
    /// </summary>
    private static Restored FillEmptyPath(string exe, string old, string? newCopy)
    {
        var since = Stopwatch.StartNew();
        while (true)
        {
            if (TryMove(old, exe)) return Restored.OldVersion;
            if (newCopy is not null && TryMove(newCopy, exe)) return Restored.NewVersion;

            if (since.Elapsed >= EmptyPathBudget) break;
            Thread.Sleep(10);
        }

        CrashLog.WriteLine($"Neither copy could be renamed to {exe} within {since.ElapsedMilliseconds} ms.");

        // A file something else is holding open can still be read, even when it cannot be
        // renamed, so the copy is the one thing left that might work. Not while Windows is
        // waiting on this copy: copying the standalone build takes seconds it does not have,
        // and the honest report is then the better answer.
        if (!_pressedToExit && CopyOldBack(exe, old, RenameAttempts)) return Restored.OldVersion;

        // Only a rename or a copy that actually went through may be claimed. Anything else at
        // the path - there should be nothing, since only Slate writes there - is not something
        // to make promises about.
        CrashLog.WriteLine(
            $"Nothing could be put back at {exe}" +
            $"{(File.Exists(exe) ? ", and something not of Slate's making is there" : "")}.");

        return Restored.Nothing;
    }

    /// <summary>
    /// How long the two renames are tried for before the copy is started instead. A few
    /// hundred milliseconds, because every one of them is spent with the path empty: long
    /// enough for the common case of a scanner letting go of a file it had just opened, and
    /// far short of the seconds a copy would take, which is what the copy is then for.
    /// </summary>
    private static readonly TimeSpan EmptyPathBudget = TimeSpan.FromMilliseconds(400);

    /// <summary>A rename that says whether it went rather than throwing.</summary>
    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The last resort when every rename failed: the old .exe copied back over the new one. A
    /// running image can still be read, so this works where a rename does not.
    ///
    /// Copying is the one step in the whole swap that is not atomic, so it goes to a scratch
    /// name beside the .exe and is renamed into place. Cut short by a full disk, an I/O error
    /// or this copy being ended, that leaves a stray file to clear away - where copying
    /// straight over the .exe would leave a truncated Slate.exe that will not run, that every
    /// retry then refuses to overwrite, and that looks from the outside exactly like a Slate.
    /// </summary>
    private static bool CopyOldBack(string exe, string old, int attempts)
    {
        var scratch = exe + ".restoring";
        try
        {
            Retry(() =>
            {
                File.Copy(old, scratch, overwrite: true);
                File.Move(scratch, exe, overwrite: true);
            }, attempts);

            // The .old is now a duplicate of what is at the path, and leaving it there is the
            // difference between this and the rename it stood in for: a swap that tries again
            // would find nowhere to move the .exe aside to. Best effort, since whatever was
            // holding it may still be.
            TryDelete(old);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.WriteLine($"Could not copy {Path.GetFileName(old)} back over {Path.GetFileName(exe)} either: {ex}");
            TryDelete(scratch);
            return false;
        }
    }

    /// <summary>
    /// A virus scanner opening a freshly written .exe holds it briefly, and a rename in that
    /// moment fails with a sharing violation that is gone a moment later. Given up on at once
    /// when Windows is waiting on this copy: every wait here is time counted against it.
    /// </summary>
    private static void Retry(Action step, int attempts = RenameAttempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                step();
                return;
            }
            catch (Exception ex) when (attempt < attempts && !_pressedToExit
                                       && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(150 * attempt);
            }
        }
    }

    /// <summary>About two seconds in all, backing off: long enough for a scan to finish.</summary>
    private const int RenameAttempts = 6;

    /// <summary>
    /// One retry, barely a wait at all, for a rollback Windows is waiting on: see RollBack.
    /// Enough for the one failure that actually needs a second try - the new copy's image
    /// still being let go of the instant after it was stopped - and nothing beyond it.
    /// </summary>
    private const int RenameAttemptsWhenPressed = 2;

    /// <summary>How many tries a rename gets, given what is waiting on this copy right now.</summary>
    private static int Attempts(bool pressed) =>
        pressed || _pressedToExit ? RenameAttemptsWhenPressed : RenameAttempts;

    // ---------------------------------------------------------------- restart

    /// <summary>
    /// Starts the new copy and waits for it to say its window is up. If it cannot start, exits
    /// first - a slim build whose runtime is missing does exactly that - or never gets as far
    /// as its window, it is stopped and the old copy is put back and keeps running.
    ///
    /// Every look at how the new copy is doing, and whatever is done about it, happens under
    /// <see cref="HandoverLock"/>, so it can never cross with <see cref="SettleBeforeExit"/>
    /// deciding the same thing on the window's thread.
    /// </summary>
    private static async Task StartAndWaitForNewCopyAsync(Handover handover)
    {
        var token = Guid.NewGuid();
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName(token));

        Process process;
        lock (HandoverLock)
        {
            // Windows may have begun signing out while the files were being swapped, in which
            // case they are back where they were and there is nothing left to start.
            if (handover.Outcome != HandoverOutcome.Pending) throw Interrupted(handover);

            // From here until this copy exits or takes back over, the data folder is the new
            // copy's: it reads the plan and the settings as it starts, and anything written
            // here afterwards would never reach it, or would land on top of its own.
            DataFolder.Freeze();

            try
            {
                var start = new ProcessStartInfo(handover.Exe) { UseShellExecute = false };
                start.ArgumentList.Add(UpdatedFromFlag);
                start.ArgumentList.Add(AppInfo.Version);
                start.ArgumentList.Add(ReadyFlag);
                start.ArgumentList.Add(token.ToString("N"));
                process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start it.");
                handover.Child = process;
                handover.Ready = ready;
            }
            catch (Exception ex)
            {
                RollBack(handover);
                throw Failed(handover, "The new version could not be started", ex);
            }
        }

        // Disposed only after the finally below has settled the handover, so nothing else can
        // be looking at the process by then: SettleBeforeExit only acts on one still pending.
        using var owned = process;
        try
        {
            var deadline = DateTime.UtcNow + StartupGrace;
            while (true)
            {
                lock (HandoverLock)
                {
                    switch (handover.Outcome)
                    {
                        case HandoverOutcome.HandedOver: return;
                        case HandoverOutcome.RolledBack: throw Interrupted(handover);
                    }

                    if (ready.WaitOne(0))
                    {
                        HandOver(handover);
                        return;
                    }

                    if (process.HasExited)
                    {
                        var code = process.ExitCode;
                        RollBack(handover);
                        throw Failed(handover,
                            $"Slate {handover.Version} closed as soon as it started (exit code {code})");
                    }

                    if (DateTime.UtcNow > deadline)
                    {
                        // Alive but never got as far as its window: stuck on a dialog of its
                        // own, such as a slim build asking for a .NET runtime this machine
                        // lacks. RollBack stops it - it is the copy this one just started.
                        RollBack(handover);
                        throw Failed(handover,
                            $"Slate {handover.Version} did not finish starting within {StartupGrace.TotalSeconds:0} seconds");
                    }
                }

                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        finally
        {
            // Leaving the wait any other way - an exception nobody expected - is a failed start
            // like the rest, and must not leave the new copy in place with nothing watching it.
            lock (HandoverLock)
            {
                if (handover.Outcome == HandoverOutcome.Pending) RollBack(handover);
            }
        }
    }

    /// <summary>
    /// Settles an update that is still waiting on its new copy before this copy ends by some
    /// other road than the handover's own: Windows signing out or shutting down, the app being
    /// shut down, or a crash. Every one of them ends the process whatever the window says -
    /// signing out calls Shutdown, which ignores a refused close - and ending with the new
    /// .exe in Slate's place and the old one only as .old leaves nothing to put it back if the
    /// new one never starts. So before returning, the new copy is stopped and the old one put
    /// back, unless it has already said it is up, in which case the update simply stands.
    /// </summary>
    /// <param name="pressed">
    /// Windows is signing out and waiting on the answer before it goes on. It waits about five
    /// seconds for one before it offers to end this copy where it stands, so everything here
    /// is cut to a moment - see <see cref="RollBack"/> - and the lock is only tried for rather
    /// than waited on, since whatever is holding it is doing exactly the work being cut short.
    /// </param>
    public static void SettleBeforeExit(string why, bool pressed = false)
    {
        // Recorded before the lock is so much as asked for, whether or not there is anything
        // to settle yet, and kept at the first reason given. Two things go by it: an install
        // that has not reached the swap, which must not reach it now, and a rollback already
        // running on a pool thread - which is holding the lock below, and which this is the
        // only way to reach in time.
        if (pressed) _pressedToExit = true;
        _exitingBecause ??= why;

        if (!pressed)
        {
            lock (HandoverLock) Settle(why, pressed: false);
            return;
        }

        if (!HandoverLock.TryEnter(PressedLockWait))
        {
            // An install is mid-swap or mid-rollback on another thread. It has seen the flag
            // above and is cutting its own waiting short; blocking the window's thread behind
            // it is what gets Slate onto Windows' "these apps are stopping you" screen and
            // ended where it stands.
            CrashLog.WriteLine(
                $"{why}, and an update still held the handover after {PressedLockWait.TotalSeconds:0.#} seconds, " +
                "so Slate answered Windows and left it to settle itself.");
            return;
        }

        try
        {
            Settle(why, pressed: true);
        }
        finally
        {
            HandoverLock.Exit();
        }
    }

    /// <summary>How long the window's thread may spend trying for the lock while Windows waits.</summary>
    private static readonly TimeSpan PressedLockWait = TimeSpan.FromSeconds(2);

    /// <summary>The settling itself. Only called under <see cref="HandoverLock"/>.</summary>
    private static void Settle(string why, bool pressed)
    {
        if (_unconfirmed is not { } handover) return;

        if (IsReady(handover))
        {
            CrashLog.WriteLine($"{why} as Slate {handover.Version} finished starting, so the update stands.");
            HandOver(handover);
            return;
        }

        CrashLog.WriteLine($"{why} before Slate {handover.Version} had started, so the update is being undone.");
        handover.Interruption = why;
        RollBack(handover, exiting: true, pressed: pressed);
    }

    /// <summary>
    /// Windows abandoned the sign-out or shutdown it had asked about - another app refused it,
    /// the user pressed Cancel - so this copy is not ending after all, and the latch
    /// <see cref="SettleBeforeExit"/> set is lifted again. Left standing it would refuse every
    /// later "Install and restart" for the rest of this copy's life, each one downloading the
    /// whole release and checking it before saying it was stopped by a sign-out that never
    /// happened. The latch set on the way out or by a crash is never lifted: those really are
    /// the end.
    ///
    /// Usually there is nothing left to lift: WPF answers the question by shutting the app
    /// down whether or not the session end goes ahead, and this copy is gone a fraction of a
    /// second later. But that shutdown is queued, not immediate, and nothing here may depend
    /// on it finishing - so for as long as this copy is still running, the truth about whether
    /// it is ending is kept up to date.
    ///
    /// Asked for under <see cref="HandoverLock"/> so it cannot land in the middle of
    /// <see cref="SwapIn"/> reading the latch, but only asked for, since this runs on the
    /// window's thread - and lifted either way, because a session that is not ending is never
    /// a reason to go on refusing an install.
    /// </summary>
    public static void SessionEndAbandoned()
    {
        var taken = HandoverLock.TryEnter(PressedLockWait);
        try
        {
            _pressedToExit = false;
            _exitingBecause = null;
        }
        finally
        {
            if (taken) HandoverLock.Exit();
        }

        CrashLog.WriteLine("Windows did not sign out after all, so Slate can install updates again.");
    }

    /// <summary>
    /// Whether the new copy has said its window is up. Anything thrown by the handle - it is
    /// disposed once the wait for the new copy is over, which only happens after the handover
    /// has been settled and taken off the record, so this should not be reachable - counts as
    /// "not up": the worst that does is put back a Slate that was already working, whereas
    /// letting it throw would take the exception onto the window's thread mid sign-out and
    /// leave the new .exe in place with nothing watching it. Only called under
    /// <see cref="HandoverLock"/>.
    /// </summary>
    private static bool IsReady(Handover handover)
    {
        try
        {
            return handover.Ready?.WaitOne(0) == true;
        }
        catch (Exception ex)
        {
            CrashLog.WriteLine($"Could not tell whether Slate {handover.Version} had started: {ex}");
            return false;
        }
    }

    /// <summary>The new copy is up: the update stands. Only called under <see cref="HandoverLock"/>.</summary>
    private static void HandOver(Handover handover)
    {
        handover.Outcome = HandoverOutcome.HandedOver;
        _handedOver = true;
        _unconfirmed = null;
    }

    private static volatile bool _handedOver;

    /// <summary>
    /// True once an update has been confirmed: the new copy has said its window is up, and this
    /// copy is only still here to exit. From then on nothing in this copy may write to the data
    /// folder or come out of the standstill the handover put it in - the folder is the new
    /// copy's, and what this one wrote would land under its feet.
    /// </summary>
    public static bool HasHandedOver => _handedOver;

    /// <summary>
    /// Stops the new copy if it was started and is still running, puts the old .exe back, waits
    /// for the new copy to actually go, and only then lets this copy write to the data folder
    /// again - the folder is the new copy's until it has gone.
    ///
    /// The .exe goes back before that wait rather than after it. Windows lets a running image
    /// be renamed, so nothing is gained by waiting first, and the wait is seconds this copy may
    /// not have: Windows signing out is one of the ways to arrive here, and it offers to end an
    /// app that has not answered in about five. Those are exactly the seconds that must not be
    /// spent with nothing at the path the shortcuts and pins point at. If it did not go back -
    /// the just-stopped copy's image can still be mapped for an instant after it was killed -
    /// it is tried once more after the wait, when that instant has passed, and what is reported
    /// afterwards is whatever actually ended up there.
    ///
    /// Nothing gets out of here: every step is a best effort that logs what it could not do,
    /// so that the steps after it still run. Letting one out would leave the handover on
    /// record with the data folder frozen, and the app carrying on with every save from then
    /// on held in memory and never written - or, from <see cref="SettleBeforeExit"/>, take it
    /// onto the window's thread while Windows is signing out. Only called under
    /// <see cref="HandoverLock"/>.
    /// </summary>
    /// <param name="exiting">
    /// This copy is on its way out, so there is nothing to bring back out of the standstill the
    /// update put the rest of the app in.
    /// </param>
    /// <param name="pressed">
    /// Windows is waiting on this before it signs out, so nothing here may take more than a
    /// moment: the new copy gets a quarter of a second to go rather than ten, each rename a
    /// couple of tries rather than six, and the copy that is the last resort is not made at
    /// all. A sign-out that arrives after this began sets <see cref="_pressedToExit"/>, which
    /// the same steps read, so it is cut short from wherever it had got to.
    /// </param>
    private static void RollBack(Handover handover, bool exiting = false, bool pressed = false)
    {
        var child = StopNewCopy(handover);

        // What is at the original path as this begins: the new copy, since the swap put it
        // there. Anything that goes wrong below leaves that true, and it is what gets reported
        // unless a restore actually improves on it.
        var restored = Restored.NewVersion;

        try
        {
            restored = Restore(handover.Exe, handover.Old, handover.Download, Attempts(pressed));
        }
        catch (Exception ex)
        {
            CrashLog.WriteLine($"Putting the old Slate back after a failed update did not finish: {ex}");
        }
        finally
        {
            handover.Outcome = HandoverOutcome.RolledBack;
            _unconfirmed = null;

            WaitForNewCopyToGo(child, handover.Version, pressed || _pressedToExit ? ExitWaitWhenPressed : ExitWait);

            // The copy that was just stopped has let go of its own image by now, which is the
            // usual reason the rename above could not go through. Worth one more go: until the
            // old copy is back under its own name, the original path holds a version that has
            // just failed to start, and saying it was put back would not be true.
            if (restored != Restored.OldVersion)
            {
                try
                {
                    restored = Restore(handover.Exe, handover.Old, handover.Download, Attempts(pressed));
                }
                catch (Exception ex)
                {
                    CrashLog.WriteLine($"Putting the old Slate back once the new copy had gone did not finish either: {ex}");
                }
            }

            handover.Restored = restored;
            CrashLog.WriteLine(Report(handover));

            // Even if the new copy could not be confirmed gone, or the old one put back: a
            // folder left frozen would quietly keep every change made from here on off the disk.
            DataFolder.Thaw();

            // Last, and only for a copy that is carrying on: the data folder is open again by
            // now, so what this lets through has somewhere to land. Guarded because a
            // subscriber that throws must not be what leaves the update half undone.
            if (!exiting)
            {
                try
                {
                    handover.OnRolledBack?.Invoke();
                }
                catch (Exception ex)
                {
                    CrashLog.WriteLine($"Could not let Slate carry on after the update was undone: {ex}");
                }
            }
        }
    }

    /// <summary>
    /// Ends the copy this update started, when there is one still running to end, and hands it
    /// back so the caller can wait for it once the .exe is back. Returns it even when stopping
    /// it reported a problem: the copy itself is ended before any of its children are, so there
    /// is still something to wait for.
    /// </summary>
    private static Process? StopNewCopy(Handover handover)
    {
        // Taken before anything that can throw. HasExited itself can, and a copy that could not
        // be asked whether it is running has to count as running - the same answer IsReady
        // gives - or the wait that follows would return at once and the data folder would be
        // thawed while a copy nobody ever stopped is still using it.
        var child = handover.Child;
        if (child is null) return null;

        try
        {
            if (child.HasExited) return null;

            child.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // Whatever it is, not only the documented few: ending the whole tree reports a
            // descendant it could not end as an AggregateException, and by now the new copy
            // has WebView2 children of its own. The copy itself is ended either way, and a
            // stray child of a copy that never showed its window is the lesser harm.
            CrashLog.WriteLine($"Could not stop Slate {handover.Version} after it failed to start: {ex}");
        }

        return child;
    }

    /// <summary>
    /// Waits for the copy that was stopped to actually go, which is what makes the data folder
    /// this copy's again. Best effort like everything else in a rollback: a wait that cannot be
    /// made is logged and the thaw goes ahead regardless, since a copy told to stop is not
    /// writing there.
    /// </summary>
    private static void WaitForNewCopyToGo(Process? child, string version, int milliseconds)
    {
        if (child is null) return;

        try
        {
            if (!child.WaitForExit(milliseconds))
                CrashLog.WriteLine($"Slate {version} was still exiting {milliseconds / 1000d:0.#} seconds after being stopped.");
        }
        catch (Exception ex)
        {
            CrashLog.WriteLine($"Could not wait for Slate {version} to go after it was stopped: {ex}");
        }
    }

    /// <summary>Long enough that a stopped copy has all but certainly gone.</summary>
    private const int ExitWait = 10_000;

    /// <summary>
    /// What that wait comes down to when Windows is waiting on this copy; see RollBack. A copy
    /// that has been killed signals within a few milliseconds, so this is barely ever reached -
    /// and what it holds up is the one rename left to try and the thaw of the data folder, both
    /// of which then have to happen inline on the window's thread while Windows counts.
    /// </summary>
    private const int ExitWaitWhenPressed = 250;

    /// <summary>
    /// A cancellation rather than a failure: this copy is on its way out, and the failure path
    /// would open the release page in a browser while Windows is signing out. Its own wording,
    /// not the generic "cancelled", is what should reach the user - hence the type.
    /// </summary>
    private static UpdateInterruptedException Interrupted(Handover handover) =>
        new($"{handover.Interruption ?? "Slate was closing"} before Slate {handover.Version} had started, {Describe(handover)}");

    /// <summary>
    /// How a rollback ended, in the words the person using Slate sees. It has to be what
    /// actually happened: two of the three leave them with something to do by hand, and a
    /// "Slate put the old version back" that is not true is how someone ends up starting a
    /// version that has already failed once, over the only working copy they had.
    /// </summary>
    private static string Describe(Handover handover) => handover.Restored switch
    {
        Restored.OldVersion => "so Slate put the old version back.",
        Restored.NewVersion =>
            "and Slate could not move it out of the way again. The version you were on is beside it as " +
            $"{Path.GetFileName(handover.Old)} - rename that over {Path.GetFileName(handover.Exe)} to go back to it.",
        _ =>
            "and nothing could be put back where Slate runs from. The version you were on is in that folder as " +
            $"{Path.GetFileName(handover.Old)} - rename that to {Path.GetFileName(handover.Exe)} to start Slate again.",
    };

    /// <summary>
    /// A failure told with what it left behind, and marked so the caller does not tack
    /// "nothing was changed" onto an explanation of what was.
    /// </summary>
    private static SelfUpdateException Failed(Handover handover, string what, Exception? inner = null) =>
        new($"{what}, {Describe(handover)}", inner)
        {
            NothingChanged = handover.Restored == Restored.OldVersion,
        };

    /// <summary>The same, for the crash log, where the whole path is more use than the advice.</summary>
    private static string Report(Handover handover) => handover.Restored switch
    {
        Restored.OldVersion => $"Slate {AppInfo.Version} is back at {handover.Exe}.",
        Restored.NewVersion =>
            $"Slate {handover.Version} could not be moved off {handover.Exe}; " +
            $"Slate {AppInfo.Version} is beside it as {Path.GetFileName(handover.Old)}.",
        _ =>
            $"Nothing could be put back at {handover.Exe}; " +
            $"Slate {AppInfo.Version} is beside it as {Path.GetFileName(handover.Old)}.",
    };

    private static string ReadyEventName(Guid token) => $@"Local\Slate.UpdateReady.{token:N}";

    // ---------------------------------------------------------------- after a restart

    /// <summary>The version this copy replaced, when it was started by an update and has not said so yet.</summary>
    private static string? _updatedFrom;
    private static Guid? _readyToken;
    private static bool _cleanupStarted;
    private static bool _cleanupStopped;

    /// <summary>
    /// True in a copy started by an update until it has told the copy that started it that
    /// it is up. Until then that copy may yet stop this one and put itself back, so this one
    /// must not start another copy of itself in the meantime.
    /// </summary>
    public static bool AwaitingReadySignal => _readyToken is not null;

    /// <summary>Reads the flags an update starts the new copy with. Called once, early in startup.</summary>
    public static void OnStartup(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == UpdatedFromFlag && System.Version.TryParse(args[i + 1], out var from))
                _updatedFrom = from.ToString();
            else if (args[i] == ReadyFlag && Guid.TryParseExact(args[i + 1], "N", out var token))
                _readyToken = token;
        }

        if (_updatedFrom is not null)
            CrashLog.WriteLine($"Updated from {_updatedFrom} to {AppInfo.Version}.");
    }

    /// <summary>
    /// Called once the window has rendered. Tells the copy that started this one that it is
    /// up, clears away what the last update left next to the .exe, and returns the version
    /// this one replaced - once, so the "updated" note is shown only the first time.
    /// </summary>
    public static string? TakeUpdatedFrom()
    {
        // Taken before the flags below are cleared: it is what decides whether the .old beside
        // this copy is this update's leftover or somebody's only working Slate.
        var startedByUpdate = _updatedFrom is not null || _readyToken is not null;

        if (_readyToken is { } token)
        {
            _readyToken = null;
            try
            {
                if (EventWaitHandle.TryOpenExisting(ReadyEventName(token), out var ready))
                    using (ready) ready.Set();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
            {
                // The old copy has already gone its own way; nothing is waiting.
            }
        }

        // Not at startup: until this copy has shown it can get this far, the .old beside it
        // may be the only working Slate there is. The copy that started this one puts it
        // back if this one never renders - and if that copy has died meanwhile, it is what
        // the user has left to go back to.
        if (!_cleanupStarted && ExePath is { } exe)
        {
            _cleanupStarted = true;
            _ = Task.Run(() => CleanUpAfterUpdateAsync(exe, startedByUpdate));
        }

        var from = _updatedFrom;
        _updatedFrom = null;
        return from;
    }

    /// <summary>
    /// The .old copy cannot be deleted while it is still running, and straight after an
    /// update it is - it waits for this copy's window before it exits - so this keeps trying
    /// for a minute. A stray .download from an update that was cut short goes too.
    /// </summary>
    /// <param name="startedByUpdate">
    /// Whether an update actually started this copy. The .old is only that update's leftover
    /// when it did; in any other copy it may be the version a rollback could not put back - the
    /// one working Slate there is - and deleting it would leave only the build that failed.
    /// Nothing is lost by leaving it: <see cref="Swap"/> clears a stale .old out of the way
    /// before the next update, whenever that comes.
    /// </param>
    private static async Task CleanUpAfterUpdateAsync(string exe, bool startedByUpdate)
    {
        // Unconditional, unlike the .old: a stray download, or the scratch file a copy back
        // into place is made through, is only ever left by something that was cut short - and
        // this copy started from the .exe beside them, so they are nothing but files taking up
        // room. Neither is ever the Slate to go back to; the .old is.
        TryDelete(exe + ".download");
        TryDelete(exe + ".restoring");

        if (!startedByUpdate) return;

        var old = exe + ".old";
        for (var attempt = 0; attempt < 30 && !_cleanupStopped && File.Exists(old); attempt++)
        {
            if (TryDelete(old)) return;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            DeleteFile(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// File.Delete refuses a read-only file, and a .exe copied off a read-only share or disc
    /// keeps that attribute through the rename to .old. Left alone, that .old could never be
    /// cleared away and would block every later update, looking like a copy still running.
    /// </summary>
    private static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

        File.Delete(path);
    }

    // ---------------------------------------------------------------- progress

    private void SetPhase(UpdatePhase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }

    /// <summary>At most a few times a second: a re-render per 80 KB chunk would be absurd.</summary>
    private void ReportProgress(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastReportedAt < TimeSpan.FromMilliseconds(200)) return;

        _lastReportedAt = now;
        Changed?.Invoke();
    }
}
