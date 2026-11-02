using System.Globalization;
using System.Text.Json;
using Kilowattcher.Api.Households;

namespace Kilowattcher.Api.Tools;

public sealed record ChargingCostEstimate(
    string Start,
    string End,
    string Description,
    int StartingPercent,
    string StartingPercentSource,
    int TargetPercent,
    double EnergyToAddKwh,
    double GridEnergyKwh,
    int ChargeMinutes,
    IReadOnlyList<PeriodCost> Breakdown,
    decimal TotalCost,
    ChargingWindow? CheapestWindow,
    decimal? CheapestWindowCost,
    decimal? SavingsIfYouWait,
    string Note);

/// <summary>
/// What charging costs if it starts at a time the visitor picks ("now", or "22:00") and runs at full rate until
/// done. It answers "how much would it cost right now?", which the cheapest-window tool can't, and the model
/// mustn't work out itself. Also returns the cheapest window in the next 24 hours to compare against.
/// </summary>
public sealed class ChargingCostTool : ITool
{
    public string Name => "estimate_charging_cost";

    public string Description =>
        "Estimate what charging the home battery from the grid costs if it starts at a given time and runs at the battery's " +
        "maximum rate until it reaches the target: cost per rate period, total cost including charging losses, when it " +
        "finishes, and how much waiting for the cheapest window in the next 24 hours would save. Use it for \"how much to " +
        "charge right now\" or \"what if I start at 10 PM\". For when it is cheapest to charge, use find_cheapest_charging_window.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "start": {
              "type": "string",
              "description": "When charging starts: \"now\", or a local time on the 24-hour clock such as \"22:00\" (the next time it comes around)."
            },
            "from_percent": {
              "type": ["number", "null"],
              "description": "Starting state of charge, 0 to 100, or null for the battery's current level."
            },
            "target_percent": {
              "type": ["number", "null"],
              "description": "State of charge to reach, 1 to 100, or null for 100."
            }
          },
          "required": ["start", "from_percent", "target_percent"],
          "additionalProperties": false
        }
        """;

    public Task<object> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Execute(arguments, context));

    internal static object Execute(JsonElement arguments, ToolContext context)
    {
        if (ResolveStart(ToolArguments.GetString(arguments, "start"), context.Clock) is not { } start)
        {
            return new ToolError("start must be \"now\" or a 24-hour local time such as \"22:00\".");
        }

        return Estimate(context.Household, context.Clock, start,
            ChargingWindowTool.Number(arguments, "from_percent"), ChargingWindowTool.Number(arguments, "target_percent"));
    }

    /// <summary>"now" is this minute; "22:00" is the next 10 PM, today or tomorrow. The model never does time math.</summary>
    internal static DateTimeOffset? ResolveStart(string? text, HomeClock clock)
    {
        var now = clock.Now;
        var thisMinute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        var word = text?.Trim().ToLowerInvariant();
        if (word is null or "" or "now" or "right now")
        {
            return thisMinute;
        }

        if (!TimeOnly.TryParseExact(word, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return null;
        }

        var today = clock.At(clock.Today, time.Hour, time.Minute);
        return today >= thisMinute ? today : clock.At(clock.Today.AddDays(1), time.Hour, time.Minute);
    }

    internal static object Estimate(Household household, HomeClock clock, DateTimeOffset start, double? fromPercent, double? targetPercent)
    {
        if (ChargingMath.Need(household, fromPercent, targetPercent) is not ChargeNeed need)
        {
            return ChargingMath.Need(household, fromPercent, targetPercent);
        }

        var periods = household.RatePeriods;
        var end = TimeZoneInfo.ConvertTime(start.AddMinutes(need.ChargeMinutes), clock.Zone);
        var total = ChargingMath.Total(start, need, periods, clock.Zone);

        // The best the visitor could do instead, from the same math.
        var cheapest = ChargingWindowTool.Plan(household, clock, new ChargingRequest(null, fromPercent, targetPercent)) as ChargingPlan;

        return new ChargingCostEstimate(
            ChargingMath.Local(start),
            ChargingMath.Local(end),
            ChargingWindowTool.Describe(clock.Today, start, end),
            need.FromPercent,
            fromPercent is null ? "live battery level" : "as asked",
            need.TargetPercent,
            Math.Round(need.EnergyToAddKwh, 2),
            Math.Round(need.GridEnergyKwh, 2),
            need.ChargeMinutes,
            ChargingMath.Price(start, need, periods, clock.Zone),
            total,
            cheapest?.CheapestWindow,
            cheapest?.EstimatedCost,
            cheapest is null ? null : Math.Max(0, total - cheapest.EstimatedCost),
            string.Create(CultureInfo.InvariantCulture,
                $"Charging at {household.Battery.MaxChargeKw} kW, the {household.Battery.Name}'s maximum. Costs include charging losses ({household.Battery.RoundTripEfficiency:P0} round-trip efficiency)."));
    }
}
