using System.Security.Cryptography;
using System.Text;

namespace AIMaster.Services;

internal static class LocalSecretProtection
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIMaster.SharedUsage.v1");

    public static string GenerateSyncKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Protect(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(secret.Trim());
        try
        {
            return "u:" + Convert.ToBase64String(
                ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return "m:" + Convert.ToBase64String(
                ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine));
        }
    }

    public static string Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret)) return string.Empty;
        try
        {
            var scope = protectedSecret.StartsWith("m:", StringComparison.Ordinal)
                ? DataProtectionScope.LocalMachine
                : DataProtectionScope.CurrentUser;
            var encoded = protectedSecret.StartsWith("u:", StringComparison.Ordinal) ||
                          protectedSecret.StartsWith("m:", StringComparison.Ordinal)
                ? protectedSecret[2..]
                : protectedSecret;
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(encoded), Entropy, scope);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }
}
