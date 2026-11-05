using System.Security.Cryptography;
using System.Text;

namespace AIMaster.Services;

internal static class LocalSecretProtection
{
    private const string SharedUsagePurpose = "AIMaster.SharedUsage.v1";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(SharedUsagePurpose);

    public static string GenerateSyncKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string Protect(string? secret) => Protect(secret, SharedUsagePurpose);

    public static string Protect(string? secret, string purpose)
    {
        if (string.IsNullOrWhiteSpace(secret)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(secret.Trim());
        var entropy = EntropyFor(purpose);
        try
        {
            return "u:" + Convert.ToBase64String(
                ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return "m:" + Convert.ToBase64String(
                ProtectedData.Protect(bytes, entropy, DataProtectionScope.LocalMachine));
        }
    }

    public static string Unprotect(string? protectedSecret) => Unprotect(protectedSecret, SharedUsagePurpose);

    public static string Unprotect(string? protectedSecret, string purpose)
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
                Convert.FromBase64String(encoded), EntropyFor(purpose), scope);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }

    private static byte[] EntropyFor(string purpose) => purpose == SharedUsagePurpose
        ? Entropy
        : Encoding.UTF8.GetBytes(purpose);
}
