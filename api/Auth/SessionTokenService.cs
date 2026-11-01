using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Kilowattcher.Api.Auth;

/// <summary>
/// Issues and checks the session cookie value: "{issuedAt}.{expiresAt}.{signature}",
/// where the signature is HMAC-SHA256 over the two Unix timestamps using SESSION_SIGNING_KEY.
/// The cookie proves the invite code was entered; it carries no identity.
/// </summary>
public sealed class SessionTokenService
{
    public const string CookieName = "kw_session";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private const int MinKeyBytes = 32;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly byte[]? _key;
    private readonly TimeProvider _time;

    public SessionTokenService(IConfiguration configuration, TimeProvider time, ILogger<SessionTokenService> logger)
    {
        _time = time;
        _key = TryParseKey(configuration["SESSION_SIGNING_KEY"]);
        if (_key is null)
        {
            logger.LogWarning("SESSION_SIGNING_KEY is missing or shorter than 32 bytes; sessions can't be issued.");
        }
    }

    public bool IsConfigured => _key is not null;

    public (string Token, DateTimeOffset ExpiresAt) Issue()
    {
        if (_key is null)
        {
            throw new InvalidOperationException("SESSION_SIGNING_KEY is not configured.");
        }

        var issuedAt = _time.GetUtcNow();
        var expiresAt = issuedAt + Lifetime;
        var payload = Payload(issuedAt.ToUnixTimeSeconds(), expiresAt.ToUnixTimeSeconds());

        return ($"{payload}.{Sign(_key, payload)}", expiresAt);
    }

    /// <summary>Returns the expiry time when the token is genuine and unexpired; otherwise null.</summary>
    public DateTimeOffset? Validate(string? token)
    {
        if (_key is null || string.IsNullOrEmpty(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 3
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var issuedAt)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAt))
        {
            return null;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(_key, Payload(issuedAt, expiresAt)));
        var actual = Encoding.ASCII.GetBytes(parts[2]);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var issued = DateTimeOffset.FromUnixTimeSeconds(issuedAt);
        var expires = DateTimeOffset.FromUnixTimeSeconds(expiresAt);

        // Reject expired tokens, tokens from the future, and lifetimes longer than we ever issue.
        if (expires <= now || issued > now + ClockSkew || expires - issued > Lifetime)
        {
            return null;
        }

        return expires;
    }

    private static string Payload(long issuedAt, long expiresAt) =>
        string.Create(CultureInfo.InvariantCulture, $"{issuedAt}.{expiresAt}");

    private static string Sign(byte[] key, string payload) =>
        WebEncoders.Base64UrlEncode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(payload)));

    private static byte[]? TryParseKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var key = Convert.FromBase64String(base64.Trim());
            return key.Length >= MinKeyBytes ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
