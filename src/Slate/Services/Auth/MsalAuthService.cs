using Microsoft.Identity.Client;
using Slate.Services.Storage;

namespace Slate.Services.Auth;

public sealed record SignInStatus(bool IsSignedIn, string? Username, string? DisplayName);

/// <summary>Raised when a token is needed but no cached one will do.</summary>
public sealed class InteractiveSignInRequiredException(string message) : Exception(message);

/// <summary>
/// One MSAL public client shared by both back-ends. Azure DevOps and Microsoft Graph are
/// separate resources, so they get separate tokens from the same signed-in account.
/// </summary>
public sealed class MsalAuthService(SettingsStore settings, TokenCacheStore cacheStore)
{
    /// <summary>First-party resource id for Azure DevOps.</summary>
    private const string AdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    public static readonly string[] GraphScopes = ["User.Read", "Calendars.ReadWrite"];
    public static readonly string[] AdoScopes = [$"{AdoResourceId}/user_impersonation"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPublicClientApplication? _app;
    private string? _builtFor;

    public event Action? SignInChanged;

    private IPublicClientApplication GetApp()
    {
        var entra = settings.Current.Entra;
        if (!entra.IsConfigured)
            throw new InvalidOperationException(
                "No Entra ID application is configured. Add the client ID on the Settings page.");

        var key = $"{entra.ClientId}|{entra.TenantId}";
        if (_app is not null && _builtFor == key) return _app;

        var app = PublicClientApplicationBuilder
            .Create(entra.ClientId)
            .WithAuthority(entra.Authority)
            // Loopback redirect - matches the "Mobile and desktop applications" platform
            // with http://localhost registered.
            .WithRedirectUri("http://localhost")
            .WithClientName("Slate")
            .WithClientVersion("1.0.0")
            .Build();

        cacheStore.Attach(app.UserTokenCache);
        _app = app;
        _builtFor = key;
        return app;
    }

    public async Task<SignInStatus> GetStatusAsync()
    {
        if (!settings.Current.Entra.IsConfigured) return new SignInStatus(false, null, null);

        try
        {
            var account = (await GetApp().GetAccountsAsync()).FirstOrDefault();
            return account is null
                ? new SignInStatus(false, null, null)
                : new SignInStatus(true, account.Username, account.Username);
        }
        catch (Exception ex) when (ex is MsalException or InvalidOperationException)
        {
            return new SignInStatus(false, null, null);
        }
    }

    /// <summary>Opens the system browser for an interactive sign-in and primes the Graph token.</summary>
    public async Task<SignInStatus> SignInAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var app = GetApp();
            var result = await app.AcquireTokenInteractive(GraphScopes)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(ct);

            SignInChanged?.Invoke();
            return new SignInStatus(true, result.Account.Username, result.Account.Username);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (settings.Current.Entra.IsConfigured)
            {
                var app = GetApp();
                foreach (var account in await app.GetAccountsAsync())
                    await app.RemoveAsync(account);
            }
        }
        catch (MsalException)
        {
            // Clearing the on-disk cache below is enough to sign out locally.
        }
        finally
        {
            cacheStore.Clear();
            _app = null;
            _builtFor = null;
            _gate.Release();
            SignInChanged?.Invoke();
        }
    }

    public Task<string> GetGraphTokenAsync(CancellationToken ct = default) => GetTokenAsync(GraphScopes, ct);

    public Task<string> GetAdoTokenAsync(CancellationToken ct = default) => GetTokenAsync(AdoScopes, ct);

    private async Task<string> GetTokenAsync(string[] scopes, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var app = GetApp();
            var account = (await app.GetAccountsAsync()).FirstOrDefault()
                ?? throw new InteractiveSignInRequiredException("Sign in with your Microsoft account to continue.");

            try
            {
                var silent = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Consent for a second resource, expired refresh token, or a new CA policy.
                var interactive = await app.AcquireTokenInteractive(scopes)
                    .WithAccount(account)
                    .ExecuteAsync(ct);
                return interactive.AccessToken;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
