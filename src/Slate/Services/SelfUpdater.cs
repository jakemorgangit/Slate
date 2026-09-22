using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Windows;

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
/// Every step that can fail before the old copy exits is undone if it does, so the original
/// path always holds a working Slate. Settings and the plan live in the data folder and are
/// written as they change, so restarting loses nothing.
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
    /// <see cref="OperationCanceledException"/> after <see cref="Cancel"/>.
    /// </summary>
    public async Task InstallAsync(ReleaseInfo release)
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
            await Task.Run(() => Swap(exe, download, old)).ConfigureAwait(false);
            await StartAndHandOverAsync(exe, download, old, release.Version).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Only while something is at the original path: if every way of putting a copy
            // back there failed, the download may be the one working Slate left to recover.
            if (File.Exists(exe)) TryDelete(download);
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
    /// Running .exe to .old, download to the .exe's name. If the second rename fails the
    /// first is undone straight away, before any retry, so the original path is only ever
    /// empty for the moment between two renames - never while waiting out whatever is
    /// holding the download.
    /// </summary>
    private static void Swap(string exe, string download, string old)
    {
        try
        {
            if (File.Exists(old)) Retry(() => File.Delete(old));
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
    /// Starts the new copy and waits for it to say its window is up before leaving. If it
    /// cannot start, or exits first - a slim build whose runtime is missing does exactly
    /// that - the old copy is put back and keeps running.
    /// </summary>
    private async Task StartAndHandOverAsync(string exe, string download, string old, string version)
    {
        var token = Guid.NewGuid();
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName(token));

        Process? process;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add(UpdatedFromFlag);
            start.ArgumentList.Add(AppInfo.Version);
            start.ArgumentList.Add(ReadyFlag);
            start.ArgumentList.Add(token.ToString("N"));
            process = Process.Start(start);
        }
        catch (Exception ex)
        {
            Restore(exe, old, download);
            throw new SelfUpdateException("The new version could not be started, so Slate put the old one back.", ex);
        }

        using (process)
        {
            var deadline = DateTime.UtcNow + StartupGrace;
            while (!ready.WaitOne(0))
            {
                if (process is null || process.HasExited)
                {
                    var code = process?.ExitCode;
                    await Task.Run(() => Restore(exe, old, download));
                    throw new SelfUpdateException(
                        $"Slate {version} closed as soon as it started{(code is { } c ? $" (exit code {c})" : "")}, so Slate put the old version back.");
                }

                if (DateTime.UtcNow > deadline)
                {
                    // Alive but never got as far as its window: stuck on a dialog of its own,
                    // such as a slim build asking for a .NET runtime this machine lacks. It is
                    // the copy this one just started, so it is safe to stop.
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        await Task.Run(() => process.WaitForExit(10_000));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        CrashLog.WriteLine($"Could not stop Slate {version} after it failed to start: {ex}");
                    }

                    await Task.Run(() => Restore(exe, old, download));
                    throw new SelfUpdateException(
                        $"Slate {version} did not finish starting within {StartupGrace.TotalSeconds:0} seconds, so Slate put the old version back.");
                }

                await Task.Delay(250);
            }
        }

        CrashLog.WriteLine($"Updating from {AppInfo.Version} to {version}; handing over to the new copy.");
        Application.Current?.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    private static string ReadyEventName(Guid token) => $@"Local\Slate.UpdateReady.{token:N}";

    // ---------------------------------------------------------------- after a restart

    /// <summary>The version this copy replaced, when it was started by an update and has not said so yet.</summary>
    private static string? _updatedFrom;
    private static Guid? _readyToken;
    private static bool _cleanupStopped;

    /// <summary>
    /// Reads the flags an update starts the new copy with, and clears away what the last
    /// update left next to the .exe. Called once, early in startup.
    /// </summary>
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

        if (ExePath is { } exe) _ = Task.Run(() => CleanUpAfterUpdateAsync(exe));
    }

    /// <summary>
    /// Tells the copy that started this one that it is up, and returns the version it
    /// replaced - once, so the "updated" note is shown only the first time.
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
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
