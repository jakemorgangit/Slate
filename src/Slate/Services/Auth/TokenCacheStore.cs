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

    private void OnAfterAccess(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged) return;

        lock (_gate)
        {
            try
            {
                AppPaths.EnsureCreated();
                var bytes = protector.ProtectBytes(args.TokenCache.SerializeMsalV3());
                var temp = AppPaths.TokenCacheFile + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, AppPaths.TokenCacheFile, overwrite: true);
            }
            catch (IOException)
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
                if (File.Exists(AppPaths.TokenCacheFile)) File.Delete(AppPaths.TokenCacheFile);
            }
            catch (IOException) { }
        }
    }
}
