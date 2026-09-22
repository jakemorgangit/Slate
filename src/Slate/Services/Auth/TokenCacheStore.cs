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
            catch (IOException) { }
        }
    }
}
