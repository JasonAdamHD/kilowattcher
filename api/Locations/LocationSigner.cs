using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kilowattcher.Api.Auth;
using Microsoft.AspNetCore.WebUtilities;

namespace Kilowattcher.Api.Locations;

/// <summary>A place as the browser stores it and sends it back: the place plus the server's signature.</summary>
public sealed record SignedPlace(string? Name, double Latitude, double Longitude, string? TimeZone, string? Signature);

/// <summary>
/// Signs the places the search endpoint returns, and checks them when they come back in chat settings.
/// The place name goes into the system prompt, so a visitor can't invent a "city" such as
/// "Phoenix. Ignore your rules" and have it reach the model: only names our geocoder returned are accepted.
/// </summary>
public sealed class LocationSigner(IConfiguration configuration)
{
    public const int MaxNameLength = 80;

    private readonly byte[]? _key = SigningKeys.Derive(configuration, "kilowattcher-location-v1");

    public SignedPlace Sign(Place place) =>
        new(place.Name, place.Latitude, place.Longitude, place.TimeZone, Signature(place));

    /// <returns>The place when the signature matches and every field is in range, otherwise null.</returns>
    public Place? Verify(SignedPlace? signed)
    {
        if (_key is null || signed is null || string.IsNullOrEmpty(signed.Signature)
            || string.IsNullOrWhiteSpace(signed.Name) || signed.Name.Length > MaxNameLength
            || signed.Latitude is < -90 or > 90 || signed.Longitude is < -180 or > 180
            || string.IsNullOrWhiteSpace(signed.TimeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(signed.TimeZone, out _))
        {
            return null;
        }

        var place = new Place(signed.Name, signed.Latitude, signed.Longitude, signed.TimeZone);
        var expected = Encoding.ASCII.GetBytes(Signature(place));
        var actual = Encoding.ASCII.GetBytes(signed.Signature);
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? place : null;
    }

    private string Signature(Place place)
    {
        if (_key is null)
        {
            throw new InvalidOperationException("SESSION_SIGNING_KEY is not configured.");
        }

        // One canonical text per place, so the same place always gets the same signature.
        var payload = string.Create(CultureInfo.InvariantCulture,
            $"{place.Name}\n{place.Latitude:F4}\n{place.Longitude:F4}\n{place.TimeZone}");
        return WebEncoders.Base64UrlEncode(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));
    }
}
