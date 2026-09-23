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
public sealed class SelfUpdateException(string message, Exception? inner = null) : Exception(message, inner);

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
/// so the original path always holds a working Slate. Ended before the swap, the install stops
/// there instead and moves nothing at all. Settings and the plan live in the data
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
    public async Task InstallAsync(ReleaseInfo release, Func<Task<bool>>? beforeSwap = null)
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
            var handover = new Handover(exe, download, old, release.Version);
            await Task.Run(() => SwapIn(handover)).ConfigureAwait(false);
            await Task.Run(() => StartAndWaitForNewCopyAsync(handover)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Only while something is at the original path: if every way of putting a copy
            // back there failed, the download may be the one working Slate left to recover.
            if (File.Exists(exe)) TryDelete(download);

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
    private sealed class Handover(string exe, string download, string old, string version)
    {
        public string Exe { get; } = exe;
        public string Download { get; } = download;
        public string Old { get; } = old;
        public string Version { get; } = version;

        /// <summary>The new copy, once it has been started.</summary>
        public Process? Child { get; set; }

        /// <summary>What the new copy sets once its window is up.</summary>
        public EventWaitHandle? Ready { get; set; }

        public HandoverOutcome Outcome { get; set; }

        /// <summary>Why it was put back from outside the wait, when it was.</summary>
        public string? Interruption { get; set; }
    }

    private enum HandoverOutcome { Pending, HandedOver, RolledBack }

    /// <summary>Guards <see cref="_unconfirmed"/> and every step that swaps, starts or puts back.</summary>
    private static readonly Lock HandoverLock = new();

    private static Handover? _unconfirmed;

    /// <summary>
    /// Why this copy is on its way out, from the moment <see cref="SettleBeforeExit"/> is
    /// first called. Held under <see cref="HandoverLock"/>, because it is what stops an
    /// install that has not swapped anything yet from starting to. Only ever set: a copy told
    /// it is ending does not un-end.
    /// </summary>
    private static string? _exitingBecause;

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
            if (_exitingBecause is { } why)
                throw new OperationCanceledException(
                    $"{why} before Slate {handover.Version} was swapped in, so nothing was changed.");

            Swap(handover.Exe, handover.Download, handover.Old);
            _unconfirmed = handover;
        }
    }

    /// <summary>
    /// Running .exe to .old, download to the .exe's name. If the second rename fails the
    /// first is undone straight away, before any retry, so the original path is only ever
    /// empty for the moment between two renames - never while waiting out whatever is
    /// holding the download.
    /// </summary>
    private static void Swap(string exe, string download, string old)
    {
        try
        {
            if (File.Exists(old)) Retry(() => DeleteFile(old));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SelfUpdateException($"An earlier copy ({Path.GetFileName(old)}) is still in use, so there is nowhere to set this one aside.", ex);
        }

        IsSwapping = true;
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
                    if (attempt >= RenameAttempts)
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
                    Restore(exe, old, displaced: null);
                    if (attempt >= RenameAttempts || !File.Exists(exe))
                        throw new SelfUpdateException("Windows would not let the new version take Slate's place. Something may be scanning it.", ex);
                    Thread.Sleep(150 * attempt);
                }
            }
        }
        finally
        {
            IsSwapping = false;
        }
    }

    /// <summary>
    /// True for the instant the running .exe is renamed aside. The window refuses to close
    /// meanwhile, since ending the process between the two renames is the one way to leave
    /// the original path empty.
    /// </summary>
    public static bool IsSwapping { get; private set; }

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
    /// Puts the old .exe back under its own name. <paramref name="displaced"/> is where the new
    /// one goes, when it made it into place. Should the rename back fail, a copy of the old
    /// one is tried (a running .exe can still be read), then the new one: a newer Slate at the
    /// path beats no Slate at all.
    /// </summary>
    private static void Restore(string exe, string old, string? displaced)
    {
        try
        {
            if (displaced is not null) Retry(() => File.Move(exe, displaced, overwrite: true));
            Retry(() => File.Move(old, exe));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.WriteLine($"Could not rename {old} back after a failed update: {ex}");
        }

        if (File.Exists(exe)) return;

        foreach (var fallback in new Action[]
                 {
                     () => File.Copy(old, exe),
                     () => { if (displaced is not null) File.Move(displaced, exe); },
                 })
        {
            try
            {
                Retry(fallback);
                if (File.Exists(exe)) return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashLog.WriteLine($"Fallback while restoring {exe} failed too: {ex}");
            }
        }
    }

    /// <summary>
    /// A virus scanner opening a freshly written .exe holds it briefly, and a rename in that
    /// moment fails with a sharing violation that is gone a moment later.
    /// </summary>
    private static void Retry(Action step)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                step();
                return;
            }
            catch (Exception ex) when (attempt < RenameAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(150 * attempt);
            }
        }
    }

    /// <summary>About two seconds in all, backing off: long enough for a scan to finish.</summary>
    private const int RenameAttempts = 6;

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
                throw new SelfUpdateException("The new version could not be started, so Slate put the old one back.", ex);
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
                        throw new SelfUpdateException(
                            $"Slate {handover.Version} closed as soon as it started (exit code {code}), so Slate put the old version back.");
                    }

                    if (DateTime.UtcNow > deadline)
                    {
                        // Alive but never got as far as its window: stuck on a dialog of its
                        // own, such as a slim build asking for a .NET runtime this machine
                        // lacks. RollBack stops it - it is the copy this one just started.
                        RollBack(handover);
                        throw new SelfUpdateException(
                            $"Slate {handover.Version} did not finish starting within {StartupGrace.TotalSeconds:0} seconds, so Slate put the old version back.");
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
    public static void SettleBeforeExit(string why)
    {
        lock (HandoverLock)
        {
            // Recorded whether or not there is anything to settle yet, and kept at the first
            // reason given: an install that has not reached the swap must not reach it now.
            _exitingBecause ??= why;

            if (_unconfirmed is not { } handover) return;

            if (IsReady(handover))
            {
                CrashLog.WriteLine($"{why} as Slate {handover.Version} finished starting, so the update stands.");
                HandOver(handover);
                return;
            }

            CrashLog.WriteLine($"{why} before Slate {handover.Version} had started, so the update was undone.");
            handover.Interruption = why;
            RollBack(handover);
        }
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
        _unconfirmed = null;
    }

    /// <summary>
    /// Stops the new copy if it was started and is still running, puts the old .exe back, and
    /// lets this copy write to the data folder again - in that order, since until the new copy
    /// is gone the folder is still its. Only called under <see cref="HandoverLock"/>.
    ///
    /// Nothing gets out of here: every step is a best effort that logs what it could not do,
    /// so that the steps after it still run. Letting one out would leave the handover on
    /// record with the data folder frozen, and the app carrying on with every save from then
    /// on held in memory and never written - or, from <see cref="SettleBeforeExit"/>, take it
    /// onto the window's thread while Windows is signing out.
    /// </summary>
    private static void RollBack(Handover handover)
    {
        try
        {
            try
            {
                if (handover.Child is { HasExited: false } child)
                {
                    child.Kill(entireProcessTree: true);
                    if (!child.WaitForExit(10_000))
                        CrashLog.WriteLine($"Slate {handover.Version} was still exiting 10 seconds after being stopped.");
                }
            }
            catch (Exception ex)
            {
                // Whatever it is, not only the documented few: ending the whole tree reports a
                // descendant it could not end as an AggregateException, and by now the new copy
                // has WebView2 children of its own. The copy itself is ended first either way,
                // and a stray child of a copy that never showed its window is the lesser harm.
                CrashLog.WriteLine($"Could not stop Slate {handover.Version} after it failed to start: {ex}");
            }

            Restore(handover.Exe, handover.Old, handover.Download);
        }
        catch (Exception ex)
        {
            CrashLog.WriteLine($"Putting the old Slate back after a failed update did not finish: {ex}");
        }
        finally
        {
            handover.Outcome = HandoverOutcome.RolledBack;
            _unconfirmed = null;

            // Even if the new copy could not be confirmed gone, or the old one put back: a
            // folder left frozen would quietly keep every change made from here on off the disk.
            DataFolder.Thaw();
        }
    }

    /// <summary>
    /// A cancellation rather than a failure: this copy is on its way out, and the failure path
    /// would open the release page in a browser while Windows is signing out.
    /// </summary>
    private static OperationCanceledException Interrupted(Handover handover) =>
        new($"{handover.Interruption ?? "Slate was closing"} before Slate {handover.Version} had started, so Slate put the old version back.");

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
            _ = Task.Run(() => CleanUpAfterUpdateAsync(exe));
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
    private static async Task CleanUpAfterUpdateAsync(string exe)
    {
        TryDelete(exe + ".download");

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
