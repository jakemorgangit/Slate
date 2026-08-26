using System.Security.Cryptography;
using System.Text;

namespace Slate.Services.Storage;

/// <summary>
/// Wraps DPAPI so secrets (the PAT, the MSAL token cache) are encrypted at rest under the
/// current Windows user account. Another user on the machine cannot read them.
/// </summary>
public sealed class SecretProtector
{
    /// <summary>
    /// Deliberately still the old name. This is not a label, it is half the key: every secret
    /// already on disk was encrypted with it, so renaming the app must not rename this or the
    /// stored token and personal access token become undecryptable.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WorkItemPlanner.v1");

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string Unprotect(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Copied from another machine or another user account - treat as "not set".
            return "";
        }
    }

    public byte[] ProtectBytes(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[]? UnprotectBytes(byte[] ciphertext)
    {
        try
        {
            return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
