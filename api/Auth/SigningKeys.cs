using System.Security.Cryptography;
using System.Text;

namespace Kilowattcher.Api.Auth;

/// <summary>
/// Derives one HMAC key per purpose from SESSION_SIGNING_KEY, so there's no new secret to manage and a
/// signature made for one purpose (say, a location) can never be replayed as another (a history turn).
/// </summary>
public static class SigningKeys
{
    /// <returns>The derived key, or null when SESSION_SIGNING_KEY is missing, not base64, or under 32 bytes.</returns>
    public static byte[]? Derive(IConfiguration configuration, string purpose)
    {
        var raw = configuration["SESSION_SIGNING_KEY"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var sessionKey = Convert.FromBase64String(raw.Trim());
            return sessionKey.Length >= 32 ? HMACSHA256.HashData(sessionKey, Encoding.ASCII.GetBytes(purpose)) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
