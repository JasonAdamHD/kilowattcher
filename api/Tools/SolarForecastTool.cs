using System.Text.Json;

namespace Kilowattcher.Api.Tools;

public sealed record SolarHour(string Hour, double Kwh);

public sealed record SolarForecast(
    string Date,
    double SystemSizeKw,
    string Conditions,
    IReadOnlyList<SolarHour> Hourly,
    double TotalKwh);

/// <summary>
/// Mock forecast for a 7.2 kW rooftop system. Every day is sunny except tomorrow, which is cloudy
/// (30% of a sunny day), so "should I charge from the grid tonight?" has a real answer to find.
/// "Tomorrow" is relative to the home's local date, so evals behave the same on any day.
/// </summary>
public sealed class SolarForecastTool(HomeClock clock) : ITool
{
    public const double SystemSizeKw = 7.2;
    public const double CloudyFactor = 0.3;
    public const int DaysAhead = 6;

    public static readonly IReadOnlyList<SolarHour> SunnyDay =
    [
        new("06:00", 0.1), new("07:00", 0.6), new("08:00", 1.5), new("09:00", 2.7),
        new("10:00", 3.8), new("11:00", 4.6), new("12:00", 5.0), new("13:00", 4.9),
        new("14:00", 4.4), new("15:00", 3.5), new("16:00", 2.3), new("17:00", 1.1),
        new("18:00", 0.3),
    ];

    public string Name => "get_solar_forecast";

    public string Description =>
        "Hourly rooftop solar production estimate in kWh for a local date (today up to 6 days ahead), " +
        "with the day's conditions and total. Hours are the start of each hour on a 24-hour clock.";

    public string ParametersSchema => ToolArguments.DateOnlySchema;

    public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromResult(Execute(arguments));

    internal object Execute(JsonElement arguments)
    {
        if (!ToolArguments.TryGetDate(arguments, out var date))
        {
            return new ToolError("date must be a local date formatted YYYY-MM-DD.");
        }

        var today = clock.Today;
        if (date < today || date > today.AddDays(DaysAhead))
        {
            return new ToolError($"Forecasts cover today through {DaysAhead} days ahead ({today:yyyy-MM-dd} to {today.AddDays(DaysAhead):yyyy-MM-dd}).");
        }

        var cloudy = date == today.AddDays(1);
        var hourly = cloudy
            ? SunnyDay.Select(h => h with { Kwh = Math.Round(h.Kwh * CloudyFactor, 1) }).ToList()
            : SunnyDay.ToList();

        return new SolarForecast(
            date.ToString("yyyy-MM-dd"),
            SystemSizeKw,
            cloudy ? "cloudy" : "sunny",
            hourly,
            Math.Round(hourly.Sum(h => h.Kwh), 1));
    }
}
