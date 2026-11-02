using System.Globalization;
using System.Text.Json;
using Kilowattcher.Api.Households;
using Kilowattcher.Api.Weather;

namespace Kilowattcher.Api.Tools;

public sealed record SolarHour(string Hour, double Kwh, int CloudCoverPercent);

public sealed record SolarForecast(
    string Date,
    double SystemSizeKw,
    string Conditions,
    int DaytimeCloudCoverPercent,
    IReadOnlyList<SolarHour> Hourly,
    double TotalKwh,
    string Source);

/// <summary>
/// Expected rooftop solar production for a date, from real weather: Open-Meteo's hourly sunlight on a
/// tilted panel (W/m²) turned into kWh in code. Each hour: system size (kW) × irradiance ÷ 1000 × 0.8,
/// where 0.8 covers inverter, wiring, heat, and dust losses. A clear Phoenix November day gives about
/// 34–37 kWh, matching solar-basics.md.
/// </summary>
public sealed class SolarForecastTool(IWeatherSource weather) : ITool
{
    public const double PerformanceRatio = 0.8;
    public const int DaysAhead = 6;

    public const string Attribution = "Weather data by Open-Meteo.com (CC BY 4.0)";

    public string Name => "get_solar_forecast";

    public string Description =>
        "Expected rooftop solar production in kWh for a local date (today up to 6 days ahead), hour by hour, from the " +
        "real weather forecast for the home's location, with the day's cloud cover and conditions.";

    public string ParametersSchema => ToolArguments.DateOnlySchema;

    public async Task<object> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (!ToolArguments.TryGetDate(arguments, out var date))
        {
            return new ToolError("date must be a local date formatted YYYY-MM-DD.");
        }

        var today = context.Clock.Today;
        if (date < today || date > today.AddDays(DaysAhead))
        {
            return new ToolError($"Forecasts cover today through {DaysAhead} days ahead ({today:yyyy-MM-dd} to {today.AddDays(DaysAhead):yyyy-MM-dd}).");
        }

        IReadOnlyList<WeatherHour> hours;
        try
        {
            hours = await weather.GetDayAsync(context.Clock, date, cancellationToken);
        }
        catch (WeatherUnavailableException)
        {
            return new ToolError("The weather forecast is unavailable right now. Say you couldn't check the solar forecast.");
        }

        if (hours.Count == 0)
        {
            return new ToolError($"No forecast is available for {date:yyyy-MM-dd} yet.");
        }

        return Build(date, hours, weather.Description, context.Household.SolarSystemKw);
    }

    internal static SolarForecast Build(DateOnly date, IReadOnlyList<WeatherHour> hours, string source, double systemSizeKw = 7.2)
    {
        var hourly = hours
            .Select(h => new SolarHour(
                h.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                Math.Round(systemSizeKw * h.TiltedIrradiance / 1000 * PerformanceRatio, 1),
                (int)Math.Round(h.CloudCover)))
            .Where(h => h.Kwh > 0)
            .ToList();

        // Cloud cover while the sun is meaningfully up (8 AM to 4 PM) decides how the day is described.
        var daytime = hours.Where(h => h.LocalTime.Hour is >= 8 and <= 16).ToList();
        var cloud = daytime.Count == 0 ? 0 : (int)Math.Round(daytime.Average(h => h.CloudCover));
        var conditions = cloud switch
        {
            < 30 => "sunny",
            < 70 => "partly cloudy",
            _ => "cloudy",
        };

        return new SolarForecast(
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            systemSizeKw,
            conditions,
            cloud,
            hourly,
            Math.Round(hours.Sum(h => systemSizeKw * h.TiltedIrradiance / 1000 * PerformanceRatio), 1),
            $"{source}. {Attribution}.");
    }
}
