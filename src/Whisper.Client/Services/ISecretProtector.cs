using System.Security.Cryptography;
using System.Text;

namespace Whisper.Client.Services;

/// <summary>
/// Wraps the platform secret store. Behind an interface so profile persistence can be
/// tested without touching the current user's DPAPI keys.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    bool TryUnprotect(string protectedText, out string plainText);
}

public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Whisper.ServerProfile.v1");

    public string Protect(string plainText) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser));

    public bool TryUnprotect(string protectedText, out string plainText)
    {
        plainText = string.Empty;

        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedText),
                Entropy,
                DataProtectionScope.CurrentUser);

            plainText = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            // Copied to another machine or another user account: the saved password is
            // simply unavailable, which is not a reason to fail loading profiles.
            return false;
        }
    }
}
