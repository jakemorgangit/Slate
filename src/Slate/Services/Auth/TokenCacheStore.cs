using Microsoft.Identity.Client;
using Slate.Services.Storage;

namespace Slate.Services.Auth;

/// <summary>
/// Persists MSAL's token cache to disk, DPAPI-encrypted, so you sign in once rather than
/// once per app launch.
/// </summary>
public sealed class TokenCacheStore(SecretProtector protector)
{
    private readonly Lock _gate = new();

    public void Attach(ITokenCache cache)
    {
        cache.SetBeforeAccess(OnBeforeAccess);
        cache.SetAfterAccess(OnAfterAccess);
    }

    private void OnBeforeAccess(TokenCacheNotificationArgs args)
    {
        lock (_gate)
        {
            if (!File.Exists(AppPaths.TokenCacheFile)) return;
            try
            {
                var decrypted = protector.UnprotectBytes(File.ReadAllBytes(AppPaths.TokenCacheFile));
                if (decrypted is not null) args.TokenCache.DeserializeMsalV3(decrypted);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // Unreadable cache just means an interactive sign-in is needed.
            }
        }
    }

    /// <summary>
    /// Through <see cref="DataFolder"/> like every other file here, so a token refreshed by a
    /// request still in flight while an update's new copy starts is not written under it. The
    /// write itself carries the bytes already encrypted and takes no lock of this class's:
    /// this class's lock is held while waiting for the data folder's gate, so a held write
    /// made under that gate when an update is undone must never wait for it in turn.
    ///
    /// Which means that on an update that goes ahead, a refresh that lands after the freeze is
    /// dropped and the new copy starts from the tokens the refresh before it left - the one
    /// thing dropped there that the new copy cannot simply work out again. That is on purpose.
    /// A refresh token for a desktop public client is not spent by being used, so the older one
    /// still works; the worst of it is an extra silent refresh, or at the very worst the
    /// re-login this class already treats as the price of losing this file. Writing it out
    /// anyway would be the dangerous half: there is nothing to say the new copy has not already
    /// read this file and written its own, and landing on top of that would take away tokens
    /// the copy that is actually running is using.
    /// </summary>
    private void OnAfterAccess(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged) return;

        lock (_gate)
        {
            try
            {
                var bytes = protector.ProtectBytes(args.TokenCache.SerializeMsalV3());
                DataFolder.Write(AppPaths.TokenCacheFile, () => DataFolder.Replace(AppPaths.TokenCacheFile, bytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing the cache costs a re-login, nothing more.
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                DataFolder.Write(AppPaths.TokenCacheFile, () =>
                {
                    if (File.Exists(AppPaths.TokenCacheFile)) File.Delete(AppPaths.TokenCacheFile);
                });
            }
            // The same two as the write above: a cache file left read-only, or one whose
            // permissions no longer let this account delete it, refuses this as surely as
            // something holding it open - and signing out has no try of its own to land in.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
