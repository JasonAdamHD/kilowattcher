using System.Text.Json;
using Kilowattcher.Api.Households;

namespace Kilowattcher.Api.Tools;

public sealed record RatePeriod(string Name, string Start, string End, decimal PricePerKwh);

public sealed record RateSchedule(
    string Utility,
    string Plan,
    string Date,
    string TimeZone,
    string Currency,
    IReadOnlyList<RatePeriod> Periods,
    bool PricesChangedInSettings);

/// <summary>
/// The household's time-of-use plan: fixed hours, with prices from config or the visitor's settings.
/// The same periods apply every day.
/// </summary>
public sealed class RateScheduleTool : ITool
{
    public const string Utility = "Brightline Energy";
    public const string Plan = "Sunrise Saver";

    /// <summary>The published plan prices, as in time-of-use-plan.md (a test keeps them in step).</summary>
    public static IReadOnlyList<RatePeriod> DefaultPeriods { get; } =
        Household.Periods(new RatePrices().SuperOffPeak, new RatePrices().OffPeak, new RatePrices().Peak);

    public string Name => "get_rate_schedule";

    public string Description =>
        "Time-of-use electricity prices for the home's plan on a given local date: each period's " +
        "start and end time (24-hour clock) and price in USD per kWh.";

    public string ParametersSchema => ToolArguments.DateOnlySchema;

    public Task<object> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Execute(arguments, context));

    internal static object Execute(JsonElement arguments, ToolContext context)
    {
        if (!ToolArguments.TryGetDate(arguments, out var date))
        {
            return new ToolError("date must be a local date formatted YYYY-MM-DD.");
        }

        var today = context.Clock.Today;
        if (date < today.AddDays(-30) || date > today.AddDays(30))
        {
            return new ToolError("Rates are available within 30 days of today.");
        }

        var periods = context.Household.RatePeriods;
        return new RateSchedule(Utility, Plan, date.ToString("yyyy-MM-dd"), context.Clock.Label, "USD", periods,
            !periods.SequenceEqual(DefaultPeriods));
    }
}
