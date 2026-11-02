using System.Globalization;
using System.Text.Json;

namespace Kilowattcher.Api.Tools;

public sealed record ChargingWindow(
    string Start,
    string End,
    string Description,
    string RatePeriod,
    decimal PricePerKwh);

public sealed record ChargingPlan(
    string Now,
    int CurrentPercent,
    int TargetPercent,
    double EnergyToAddKwh,
    double GridEnergyKwh,
    int ChargeMinutes,
    ChargingWindow CheapestWindow,
    decimal EstimatedCost,
    decimal CostIfChargedAtPeak,
    double RoundTripEfficiency,
    string Note);

/// <summary>
/// Finds the cheapest time in the next 24 hours to charge the battery from the grid, in code. "Tonight"
/// spans two dates (the cheapest hours, midnight to 6 AM, belong to tomorrow's schedule), which the
/// model sometimes got wrong when it worked the window out itself. The model explains; this computes.
/// </summary>
public sealed class ChargingWindowTool(HomeClock clock) : ITool
{
    /// <summary>From api/Data/docs/battery-manual.md, "Charging from the grid"; a test keeps them in step.</summary>
    public const double RoundTripEfficiency = 0.9;

    private const int SlotMinutes = 15;
    private const int HorizonHours = 24;

    public string Name => "find_cheapest_charging_window";

    public string Description =>
        "Find the cheapest time in the next 24 hours to charge the home battery from the grid, with how much energy " +
        "it needs, how long it takes at the battery's maximum charge rate, and the estimated cost including charging losses. " +
        "Pass target_percent to charge to a level other than full, or null for 100%.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "target_percent": {
              "type": ["number", "null"],
              "description": "State of charge to reach, 1 to 100, or null for 100."
            }
          },
          "required": ["target_percent"],
          "additionalProperties": false
        }
        """;

    public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromResult(Execute(arguments));

    internal object Execute(JsonElement arguments)
    {
        double? target = arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("target_percent", out var t) && t.ValueKind == JsonValueKind.Number
                ? t.GetDouble()
                : null;

        return Plan(BatteryStatusTool.Status, clock.Now, target);
    }

    internal static object Plan(BatteryStatus battery, DateTimeOffset now, double? requestedTarget)
    {
        var target = (int)Math.Round(requestedTarget ?? 100);
        if (target is < 1 or > 100)
        {
            return new ToolError("target_percent must be between 1 and 100.");
        }

        if (battery.StateOfChargePercent >= target)
        {
            return new ToolError($"The battery is already at {battery.StateOfChargePercent}%, at or above the {target}% target. No grid charging needed.");
        }

        var energyToAdd = battery.CapacityKwh * (target - battery.StateOfChargePercent) / 100.0;
        var gridEnergy = energyToAdd / RoundTripEfficiency;
        var chargeHours = energyToAdd / battery.MaxChargeKw;
        var slotsNeeded = (int)Math.Ceiling(chargeHours * 60 / SlotMinutes);

        // 15-minute slots from the next quarter hour, for 24 hours. Prices repeat every day.
        var first = RoundUpToSlot(now);
        var slots = Enumerable.Range(0, HorizonHours * 60 / SlotMinutes)
            .Select(i => first.AddMinutes(i * SlotMinutes))
            .ToList();

        // The cheapest run of consecutive slots long enough to finish charging; ties go to the earliest.
        var bestStart = 0;
        var bestAverage = decimal.MaxValue;
        for (var start = 0; start + slotsNeeded <= slots.Count; start++)
        {
            var average = slots.Skip(start).Take(slotsNeeded).Average(s => PeriodAt(s).PricePerKwh);
            if (average < bestAverage)
            {
                bestAverage = average;
                bestStart = start;
            }
        }

        var windowStart = slots[bestStart];
        // Rounded up to the whole minute, so the plan never ends before charging does.
        var chargeMinutes = (int)Math.Ceiling(chargeHours * 60);
        var windowEnd = windowStart.AddMinutes(chargeMinutes);
        var period = PeriodAt(windowStart);
        var peakPrice = RateScheduleTool.Periods.Max(p => p.PricePerKwh);

        return new ChargingPlan(
            Local(now),
            battery.StateOfChargePercent,
            target,
            Math.Round(energyToAdd, 2),
            Math.Round(gridEnergy, 2),
            chargeMinutes,
            new ChargingWindow(Local(windowStart), Local(windowEnd), Describe(now, windowStart, windowEnd), period.Name, bestAverage),
            Math.Round((decimal)gridEnergy * bestAverage, 2),
            Math.Round((decimal)gridEnergy * peakPrice, 2),
            RoundTripEfficiency,
            "Costs include charging losses (90% round-trip efficiency). Combine with tomorrow's solar forecast to decide whether grid charging is worth it.");
    }

    internal static RatePeriod PeriodAt(DateTimeOffset time)
    {
        var clock = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        return RateScheduleTool.Periods.First(p => string.CompareOrdinal(clock, p.Start) >= 0 && string.CompareOrdinal(clock, p.End) < 0);
    }

    private static DateTimeOffset RoundUpToSlot(DateTimeOffset time)
    {
        var trimmed = new DateTimeOffset(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Offset);
        var extra = trimmed.Minute % SlotMinutes;
        var rounded = extra == 0 && trimmed == time ? trimmed : trimmed.AddMinutes(SlotMinutes - extra);
        return rounded;
    }

    private static string Local(DateTimeOffset time) => time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"tonight, midnight to 1:34 AM" style wording, so the model doesn't have to work out dates.</summary>
    internal static string Describe(DateTimeOffset now, DateTimeOffset start, DateTimeOffset end)
    {
        var day = (DateOnly.FromDateTime(start.DateTime).DayNumber - DateOnly.FromDateTime(now.DateTime).DayNumber, start.Hour) switch
        {
            (0, < 17) => "today",
            (0, _) => "this evening",
            (1, < 6) => "tonight",
            (1, _) => "tomorrow",
            _ => DateOnly.FromDateTime(start.DateTime).ToString("dddd", CultureInfo.InvariantCulture),
        };

        return $"{day}, {Time(start)} to {Time(end)}";
    }

    private static string Time(DateTimeOffset t) => (t.Hour, t.Minute) switch
    {
        (0, 0) => "midnight",
        (12, 0) => "noon",
        _ => t.ToString(t.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture),
    };
}
