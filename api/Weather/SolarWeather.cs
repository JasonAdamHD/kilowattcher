using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kilowattcher.Api.Tools;

namespace Kilowattcher.Api.Weather;

/// <summary>One forecast hour: sunlight on a tilted panel (W/m²), cloud cover (%), and temperature (°C).</summary>
public sealed record WeatherHour(DateTime LocalTime, double TiltedIrradiance, double CloudCover, double Temperature);

/// <summary>Hourly weather for the home's location, local time, today and the next days.</summary>
public interface IWeatherSource
{
    /// <summary>Human-readable source for tool results, such as "Open-Meteo live forecast".</summary>
    string Description { get; }

    /// <param name="clock">The home's clock: its location says where, its time zone says which day is which.</param>
    /// <returns>The hours for that local date, or empty when the date is outside the forecast.</returns>
    Task<IReadOnlyList<WeatherHour>> GetDayAsync(HomeClock clock, DateOnly date, CancellationToken cancellationToken);
}

public sealed class WeatherUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The raw Open-Meteo response shape we ask for (hourly arrays, local times).</summary>
public sealed record OpenMeteoResponse(
    [property: JsonPropertyName("timezone")] string? Timezone,
    [property: JsonPropertyName("hourly")] OpenMeteoHourly? Hourly)
{
    public IReadOnlyList<WeatherHour> ToHours()
    {
        var h = Hourly ?? throw new WeatherUnavailableException("Open-Meteo returned no hourly data.");
        if (h.Time.Count != h.TiltedIrradiance.Count || h.Time.Count != h.CloudCover.Count || h.Time.Count != h.Temperature.Count)
        {
            throw new WeatherUnavailableException("Open-Meteo returned hourly arrays of different lengths.");
        }

        return h.Time.Select((t, i) => new WeatherHour(
                DateTime.ParseExact(t, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
                h.TiltedIrradiance[i] ?? 0,
                h.CloudCover[i] ?? 0,
                h.Temperature[i] ?? 0))
            .ToList();
    }
}

public sealed record OpenMeteoHourly(
    [property: JsonPropertyName("time")] IReadOnlyList<string> Time,
    [property: JsonPropertyName("global_tilted_irradiance")] IReadOnlyList<double?> TiltedIrradiance,
    [property: JsonPropertyName("cloud_cover")] IReadOnlyList<double?> CloudCover,
    [property: JsonPropertyName("temperature_2m")] IReadOnlyList<double?> Temperature);

/// <summary>
/// Live forecast from the free Open-Meteo API (no key; data by Open-Meteo.com, CC BY 4.0).
/// One 7-day request per location is cached for 30 minutes, so a busy demo makes a handful of calls an hour.
/// </summary>
public sealed class OpenMeteoWeatherSource(HttpClient http, TimeProvider time, ILogger<OpenMeteoWeatherSource> logger) : IWeatherSource
{
    public const int ForecastDays = 7;

    // A typical rooftop: 25° tilt, facing south (Open-Meteo azimuth 0 = south).
    private const int PanelTilt = 25;
    private const int PanelAzimuth = 0;

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset FetchedAt, IReadOnlyList<WeatherHour> Hours)> _cache = [];

    public string Description => "Open-Meteo live forecast";

    public async Task<IReadOnlyList<WeatherHour>> GetDayAsync(HomeClock clock, DateOnly date, CancellationToken cancellationToken)
    {
        var hours = await GetForecastAsync(clock.Location, cancellationToken);
        return hours.Where(h => DateOnly.FromDateTime(h.LocalTime) == date).ToList();
    }

    private async Task<IReadOnlyList<WeatherHour>> GetForecastAsync(HomeLocation location, CancellationToken cancellationToken)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{location.Latitude:F3},{location.Longitude:F3},{location.TimeZone}");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(key, out var cached) && time.GetUtcNow() - cached.FetchedAt < CacheFor)
            {
                return cached.Hours;
            }

            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={location.Latitude}&longitude={location.Longitude}" +
                $"&hourly=global_tilted_irradiance,cloud_cover,temperature_2m&tilt={PanelTilt}&azimuth={PanelAzimuth}" +
                $"&timezone={Uri.EscapeDataString(location.TimeZone)}&forecast_days={ForecastDays}");

            try
            {
                var response = await http.GetFromJsonAsync<OpenMeteoResponse>(url, cancellationToken)
                    ?? throw new WeatherUnavailableException("Open-Meteo returned an empty body.");
                var hours = response.ToHours();
                _cache[key] = (time.GetUtcNow(), hours);
                logger.LogInformation("Fetched {Hours} forecast hours from Open-Meteo for {City}.", hours.Count, location.City);
                return hours;
            }
            // HttpClient reports its own timeout as TaskCanceledException; a cancelled request is not "unavailable".
            catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
                || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogWarning("Open-Meteo request failed ({Error}).", ex.GetType().Name);
                throw new WeatherUnavailableException("The weather forecast is unavailable right now.", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Replays a saved Open-Meteo response (api/Data/weather/open-meteo-recorded.json) through the same
/// parsing and solar math, so tests and evals get the same weather on any day. The recording's first
/// day is served as <b>tomorrow</b>: it was a 95%-cloudy day, which keeps "should I charge tonight?"
/// a real decision. Its later days follow in order.
/// </summary>
public sealed class RecordedWeatherSource : IWeatherSource
{
    public const string FileName = "open-meteo-recorded.json";

    private readonly Lazy<IReadOnlyList<WeatherHour>> _hours;

    public RecordedWeatherSource() : this(Path.Combine(AppContext.BaseDirectory, "Data", "weather", FileName))
    {
    }

    internal RecordedWeatherSource(string path)
    {
        _hours = new Lazy<IReadOnlyList<WeatherHour>>(() =>
            (JsonSerializer.Deserialize<OpenMeteoResponse>(File.ReadAllText(path))
                ?? throw new WeatherUnavailableException("The recorded forecast is empty.")).ToHours());
    }

    public string Description => "Recorded Open-Meteo forecast (replay for tests and evals)";

    /// <remarks>The same recording plays for every city: replay is about stable scores, not geography.</remarks>
    public Task<IReadOnlyList<WeatherHour>> GetDayAsync(HomeClock clock, DateOnly date, CancellationToken cancellationToken)
    {
        var hours = _hours.Value;
        var firstRecorded = DateOnly.FromDateTime(hours[0].LocalTime);
        var recordedDate = firstRecorded.AddDays(date.DayNumber - clock.Today.AddDays(1).DayNumber);

        IReadOnlyList<WeatherHour> day = hours
            .Where(h => DateOnly.FromDateTime(h.LocalTime) == recordedDate)
            .Select(h => h with { LocalTime = date.ToDateTime(TimeOnly.FromDateTime(h.LocalTime)) })
            .ToList();

        return Task.FromResult(day);
    }
}
