using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Kilowattcher.Api.Auth;

/// <summary>
/// Signs every assistant answer the API sends, and checks those signatures when the browser sends
/// the conversation back as history. A visitor can't invent "assistant" turns (for example a fake
/// "Sure, I can share recipes!") to talk the model out of its rules.
/// Uses a key derived from SESSION_SIGNING_KEY, so there's no new secret to manage.
/// </summary>
public sealed class HistorySigner
{
    private readonly byte[]? _key;

    public HistorySigner(IConfiguration configuration)
    {
        _key = SigningKeys.Derive(configuration, "kilowattcher-history-v1");
    }

    public string Sign(string answer)
    {
        if (_key is null)
        {
            throw new InvalidOperationException("SESSION_SIGNING_KEY is not configured.");
        }

        return WebEncoders.Base64UrlEncode(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(answer)));
    }

    public bool Verify(string? answer, string? signature)
    {
        if (_key is null || answer is null || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(answer));
        var actual = Encoding.ASCII.GetBytes(signature);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
