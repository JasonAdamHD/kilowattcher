using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Kilowattcher.Api.Locations;

/// <summary>A place a visitor can pick: a US city with its coordinates and IANA time zone.</summary>
public sealed record Place(string Name, double Latitude, double Longitude, string TimeZone);

/// <summary>Finds US places by city name or 5-digit ZIP code.</summary>
public interface ILocationSearch
{
    Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken cancellationToken);
}

public sealed class LocationSearchUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The free Open-Meteo geocoding API (no key; GeoNames data, CC BY 4.0). It matches city names and postal
/// codes. Results are cached for a day, since places don't move.
/// </summary>
public sealed partial class OpenMeteoLocationSearch(HttpClient http, TimeProvider time, ILogger<OpenMeteoLocationSearch> logger) : ILocationSearch
{
    public const int MaxResults = 5;
    private const int MaxCacheEntries = 500;
    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset FetchedAt, IReadOnlyList<Place> Places)> _cache = [];

    public async Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var key = query.Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit) && time.GetUtcNow() - hit.FetchedAt < CacheFor)
            {
                return hit.Places;
            }
        }

        var url = "https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json&countryCode=US&name=" +
            Uri.EscapeDataString(query.Trim());
        GeocodingResponse? response;
        try
        {
            response = await http.GetFromJsonAsync<GeocodingResponse>(url, cancellationToken);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                                   && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Open-Meteo geocoding failed: {Error}", ex.GetType().Name);
            throw new LocationSearchUnavailableException("Location search is unavailable.", ex);
        }

        var places = ToPlaces(response);
        lock (_gate)
        {
            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }

            _cache[key] = (time.GetUtcNow(), places);
        }

        return places;
    }

    /// <summary>US results with a known time zone, labelled "City, State", without duplicates.</summary>
    internal static IReadOnlyList<Place> ToPlaces(GeocodingResponse? response) =>
        (response?.Results ?? [])
            .Where(r => r.CountryCode == "US" && !string.IsNullOrWhiteSpace(r.Name) && r.Timezone is not null
                        && r.Latitude is >= -90 and <= 90 && r.Longitude is >= -180 and <= 180
                        && TimeZoneInfo.TryFindSystemTimeZoneById(r.Timezone, out _))
            .Select(r => new Place(Label(r.Name!, r.Admin1), Math.Round(r.Latitude, 4), Math.Round(r.Longitude, 4), r.Timezone!))
            .Where(p => p.Name.Length > 0)
            .DistinctBy(p => p.Name)
            .Take(MaxResults)
            .ToList();

    /// <summary>
    /// "Austin, Texas". Only letters, digits, spaces and simple punctuation survive, since the name reaches
    /// the system prompt.
    /// </summary>
    internal static string Label(string name, string? state)
    {
        var label = string.IsNullOrWhiteSpace(state) || state == name ? name : $"{name}, {state}";
        label = Unsafe().Replace(label, "").Trim();
        return label.Length > LocationSigner.MaxNameLength ? label[..LocationSigner.MaxNameLength].Trim() : label;
    }

    [GeneratedRegex(@"[^\p{L}\p{N} ,.'\-()]")]
    private static partial Regex Unsafe();
}

public sealed record GeocodingResponse([property: JsonPropertyName("results")] IReadOnlyList<GeocodingResult>? Results);

public sealed record GeocodingResult(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("latitude")] double Latitude,
    [property: JsonPropertyName("longitude")] double Longitude,
    [property: JsonPropertyName("timezone")] string? Timezone,
    [property: JsonPropertyName("country_code")] string? CountryCode,
    [property: JsonPropertyName("admin1")] string? Admin1);
