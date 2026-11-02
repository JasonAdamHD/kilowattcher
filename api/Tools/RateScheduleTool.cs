using System.Text.Json;

namespace Kilowattcher.Api.Tools;

public sealed record RatePeriod(string Name, string Start, string End, decimal PricePerKwh);

public sealed record RateSchedule(
    string Utility,
    string Plan,
    string Date,
    string TimeZone,
    string Currency,
    IReadOnlyList<RatePeriod> Periods);

/// <summary>Mock time-of-use plan. The same periods apply every day.</summary>
public sealed class RateScheduleTool(HomeClock clock) : ITool
{
    public const string Utility = "Brightline Energy";
    public const string Plan = "Sunrise Saver";

    public static readonly IReadOnlyList<RatePeriod> Periods =
    [
        new("Super off-peak", "00:00", "06:00", 0.11m),
        new("Off-peak", "06:00", "16:00", 0.24m),
        new("Peak", "16:00", "21:00", 0.48m),
        new("Off-peak", "21:00", "24:00", 0.24m),
    ];

    public string Name => "get_rate_schedule";

    public string Description =>
        "Time-of-use electricity prices for the home's plan on a given local date: each period's " +
        "start and end time (24-hour clock) and price in USD per kWh.";

    public string ParametersSchema => ToolArguments.DateOnlySchema;

    public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromResult(Execute(arguments));

    internal object Execute(JsonElement arguments)
    {
        if (!ToolArguments.TryGetDate(arguments, out var date))
        {
            return new ToolError("date must be a local date formatted YYYY-MM-DD.");
        }

        if (date < clock.Today.AddDays(-30) || date > clock.Today.AddDays(30))
        {
            return new ToolError("Rates are available within 30 days of today.");
        }

        return new RateSchedule(Utility, Plan, date.ToString("yyyy-MM-dd"), clock.Label, "USD", Periods);
    }
}
