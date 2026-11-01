using System.Security.Cryptography;
using System.Text;

namespace Kilowattcher.Api.Auth;

/// <summary>
/// Checks a submitted invite code against INVITE_CODE_HASH (hex SHA-256).
/// Only the hash is ever stored; the plain code is shared by hand.
/// </summary>
public sealed class InviteCodeVerifier
{
    public const int MaxCodeLength = 128;

    private readonly byte[]? _expectedHash;

    public InviteCodeVerifier(IConfiguration configuration, ILogger<InviteCodeVerifier> logger)
    {
        _expectedHash = TryParseHash(configuration["INVITE_CODE_HASH"]);
        if (_expectedHash is null)
        {
            logger.LogWarning("INVITE_CODE_HASH is missing or not a 64-character hex SHA-256; every invite code will be rejected.");
        }
    }

    public bool IsConfigured => _expectedHash is not null;

    public bool Verify(string? code)
    {
        if (_expectedHash is null || string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength)
        {
            return false;
        }

        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim()));

        // Constant-time comparison so response timing reveals nothing about the hash.
        return CryptographicOperations.FixedTimeEquals(actualHash, _expectedHash);
    }

    public static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static byte[]? TryParseHash(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Trim().Length != SHA256.HashSizeInBytes * 2)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(hex.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
