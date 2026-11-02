using System.Globalization;
using System.Text.Json;
using Kilowattcher.Api.Households;
using Kilowattcher.Api.Weather;

namespace Kilowattcher.Api.Tools;

public sealed record ChargingWindow(
    string Start,
    string End,
    string Description,
    string RatePeriod,
    decimal PricePerKwh);

public sealed record ChargingPlan(
    string SearchedFrom,
    string SearchedTo,
    int StartingPercent,
    string StartingPercentSource,
    int TargetPercent,
    double EnergyToAddKwh,
    double GridEnergyKwh,
    int ChargeMinutes,
    ChargingWindow CheapestWindow,
    decimal EstimatedCost,
    decimal CostIfChargedAtPeak,
    double RoundTripEfficiency,
    string Note,
    NextDaySolar? NextDay = null,
    string Recommendation = "unknown",
    string? RecommendationReason = null);

/// <summary>The day after the charging night: what the panels should make, from the real forecast.</summary>
public sealed record NextDaySolar(string Date, string Weekday, string Conditions, double TotalKwh);

/// <summary>Optional inputs: which night to plan for, and a hypothetical starting charge level.</summary>
public sealed record ChargingRequest(DateOnly? NightOf = null, double? FromPercent = null, double? TargetPercent = null);

/// <summary>
/// Finds the cheapest time to charge the battery from the grid, in code: either in the next 24 hours, or on
/// a chosen night ("Tuesday night" = Tuesday 6 PM to Wednesday 10 AM). Nights span two dates, and the cheapest
/// hours (midnight to 6 AM) belong to the second one, which the model got wrong when it worked windows out
/// itself. The model explains; this computes.
/// </summary>
public sealed class ChargingWindowTool(IWeatherSource weather) : ITool
{
    public const int NightsAhead = 6;
    private const int SlotMinutes = 15;

    public string Name => "find_cheapest_charging_window";

    public string Description =>
        "Find the cheapest time to charge the home battery from the grid, with the energy needed, how long it takes at the " +
        "battery's maximum charge rate, and the estimated cost including charging losses. Plans for the next 24 hours, or for " +
        "a chosen night. night_of takes the words people use: \"tonight\", \"tomorrow\", a weekday such as \"Tuesday\" " +
        "(that evening into the next morning), or a YYYY-MM-DD date. from_percent sets a hypothetical starting charge " +
        "(33 for 'a third full'); null uses the live battery level. Also returns the next day's solar forecast and a " +
        "recommendation (worth_it / not_needed) with the reason.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "night_of": {
              "type": ["string", "null"],
              "description": "Which night: \"tonight\", \"tomorrow\", a weekday name like \"Tuesday\", a YYYY-MM-DD date, or null for the next 24 hours."
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
          "required": ["night_of", "from_percent", "target_percent"],
          "additionalProperties": false
        }
        """;

    public async Task<object> ExecuteAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var result = Execute(arguments, context);
        if (result is not ChargingPlan plan)
        {
            return result;
        }

        // The decision depends on the day after the night: will solar refill the battery for free?
        var windowStart = DateTime.ParseExact(plan.CheapestWindow.Start, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var nextDay = DateOnly.FromDateTime(windowStart).AddDays(windowStart.Hour < 12 ? 0 : 1);

        try
        {
            var hours = await weather.GetDayAsync(context.Clock, nextDay, cancellationToken);
            if (hours.Count == 0)
            {
                return plan with { RecommendationReason = "No solar forecast is available for the next day yet." };
            }

            var forecast = SolarForecastTool.Build(nextDay, hours, weather.Description, context.Household.SolarSystemKw);
            return Recommend(plan, new NextDaySolar(forecast.Date, nextDay.ToString("dddd", CultureInfo.InvariantCulture), forecast.Conditions, forecast.TotalKwh));
        }
        catch (WeatherUnavailableException)
        {
            return plan with { RecommendationReason = "The weather forecast is unavailable, so the next day's solar couldn't be checked." };
        }
    }

    /// <summary>The FAQ's rule: charge at night when the next day is cloudy or partly cloudy; skip it when it's sunny.</summary>
    internal static ChargingPlan Recommend(ChargingPlan plan, NextDaySolar next) => next.Conditions == "sunny"
        ? plan with
        {
            NextDay = next,
            Recommendation = "not_needed",
            RecommendationReason = $"{next.Weekday} looks sunny (about {next.TotalKwh} kWh of solar), so solar should refill the battery for free.",
        }
        : plan with
        {
            NextDay = next,
            Recommendation = "worth_it",
            RecommendationReason = $"{next.Weekday} looks {next.Conditions} (about {next.TotalKwh} kWh of solar), so solar may not refill the battery. " +
                $"Charging {plan.CheapestWindow.Description} costs about ${plan.EstimatedCost} instead of ${plan.CostIfChargedAtPeak} at peak.",
        };

    internal static object Execute(JsonElement arguments, ToolContext context)
    {
        DateOnly? night = null;
        if (ToolArguments.GetString(arguments, "night_of") is { } text)
        {
            night = ResolveNight(text, context.Clock.Today);
            if (night is null)
            {
                return new ToolError("night_of must be \"tonight\", \"tomorrow\", a weekday name, a YYYY-MM-DD date, or null.");
            }
        }

        return Plan(context.Household, context.Clock, new ChargingRequest(night, Number(arguments, "from_percent"), Number(arguments, "target_percent")));
    }

    /// <summary>Turns "tonight", "tomorrow", "Tuesday", or "2026-11-03" into a date, so the model never does date math.</summary>
    internal static DateOnly? ResolveNight(string text, DateOnly today)
    {
        var word = text.Trim().ToLowerInvariant().Replace(" night", "").Replace("this ", "").Replace("next ", "");
        if (word is "tonight" or "today")
        {
            return today;
        }

        if (word is "tomorrow")
        {
            return today.AddDays(1);
        }

        // Weekday names only; Enum.TryParse would also accept numbers like "3".
        if (!word.Any(char.IsDigit) && Enum.TryParse<DayOfWeek>(word, ignoreCase: true, out var weekday))
        {
            return today.AddDays(((int)weekday - (int)today.DayOfWeek + 7) % 7);
        }

        return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    internal static object Plan(Household household, HomeClock clock, ChargingRequest request)
    {
        var battery = household.Status;
        var efficiency = household.Battery.RoundTripEfficiency;
        var periods = household.RatePeriods;
        var now = clock.Now;
        var target = (int)Math.Round(request.TargetPercent ?? 100);
        if (target is < 1 or > 100)
        {
            return new ToolError("target_percent must be between 1 and 100.");
        }

        var start = (int)Math.Round(request.FromPercent ?? battery.StateOfChargePercent);
        if (start is < 0 or > 100)
        {
            return new ToolError("from_percent must be between 0 and 100.");
        }

        if (start >= target)
        {
            return new ToolError($"Starting at {start}%, the battery is already at or above the {target}% target. No grid charging needed.");
        }

        // Where to look: the next 24 hours, or the chosen evening through the next morning.
        DateTimeOffset searchFrom, searchTo;
        if (request.NightOf is { } night)
        {
            if (night < clock.Today || night > clock.Today.AddDays(NightsAhead))
            {
                return new ToolError($"night_of must be between {clock.Today:yyyy-MM-dd} and {clock.Today.AddDays(NightsAhead):yyyy-MM-dd}.");
            }

            var evening = clock.At(night, 18);
            searchFrom = evening > now ? evening : now;
            searchTo = clock.At(night.AddDays(1), 10);
        }
        else
        {
            searchFrom = now;
            searchTo = now.AddHours(24);
        }

        var energyToAdd = battery.CapacityKwh * (target - start) / 100.0;
        var gridEnergy = energyToAdd / efficiency;
        var chargeMinutes = (int)Math.Ceiling(energyToAdd / battery.MaxChargeKw * 60);
        var slotsNeeded = (int)Math.Ceiling(chargeMinutes / (double)SlotMinutes);

        // 15-minute slots from the next quarter hour. Each slot's price comes from its local clock time,
        // so daylight-saving changes inside the window are handled.
        var slots = new List<DateTimeOffset>();
        for (var slot = RoundUpToSlot(searchFrom); slot < searchTo; slot = slot.AddMinutes(SlotMinutes))
        {
            slots.Add(TimeZoneInfo.ConvertTime(slot, clock.Zone));
        }

        if (slots.Count < slotsNeeded)
        {
            return new ToolError("There isn't enough time left in that window to finish charging. Try the next night.");
        }

        // The cheapest run of consecutive slots long enough to finish charging; ties go to the earliest.
        var bestStart = 0;
        var bestAverage = decimal.MaxValue;
        for (var i = 0; i + slotsNeeded <= slots.Count; i++)
        {
            var average = slots.Skip(i).Take(slotsNeeded).Average(s => PeriodAt(s, periods).PricePerKwh);
            if (average < bestAverage)
            {
                bestAverage = average;
                bestStart = i;
            }
        }

        var windowStart = slots[bestStart];
        var windowEnd = windowStart.AddMinutes(chargeMinutes);
        var peakPrice = periods.Max(p => p.PricePerKwh);

        return new ChargingPlan(
            Local(searchFrom),
            Local(searchTo),
            start,
            request.FromPercent is null ? "live battery level" : "as asked",
            target,
            Math.Round(energyToAdd, 2),
            Math.Round(gridEnergy, 2),
            chargeMinutes,
            new ChargingWindow(Local(windowStart), Local(windowEnd), Describe(clock.Today, windowStart, windowEnd), PeriodAt(windowStart, periods).Name, bestAverage),
            Math.Round((decimal)gridEnergy * bestAverage, 2),
            Math.Round((decimal)gridEnergy * peakPrice, 2),
            efficiency,
            string.Create(CultureInfo.InvariantCulture, $"Costs include charging losses ({efficiency:P0} round-trip efficiency for the {household.Battery.Name})."));
    }

    internal static RatePeriod PeriodAt(DateTimeOffset localTime, IReadOnlyList<RatePeriod> periods)
    {
        var time = localTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        return periods.First(p => string.CompareOrdinal(time, p.Start) >= 0 && string.CompareOrdinal(time, p.End) < 0);
    }

    /// <summary>"tonight, midnight to 1:34 AM" or "Tuesday night, midnight to 1:34 AM", so the model doesn't work out dates.</summary>
    internal static string Describe(DateOnly today, DateTimeOffset start, DateTimeOffset end)
    {
        var startDate = DateOnly.FromDateTime(start.DateTime);
        var days = startDate.DayNumber - today.DayNumber;
        var weekday = (DateOnly d) => d.ToString("dddd", CultureInfo.InvariantCulture);

        var when = (days, start.Hour) switch
        {
            (0, < 17) => "today",
            (0, _) => "this evening",
            (1, < 6) => "tonight",
            (1, _) => "tomorrow",
            (_, < 6) => $"{weekday(startDate.AddDays(-1))} night (early {weekday(startDate)})",
            _ => weekday(startDate),
        };

        return $"{when}, {Time(start)} to {Time(end)}";
    }

    private static double? Number(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    private static DateTimeOffset RoundUpToSlot(DateTimeOffset time)
    {
        var trimmed = new DateTimeOffset(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Offset);
        var extra = trimmed.Minute % SlotMinutes;
        return extra == 0 && trimmed == time ? trimmed : trimmed.AddMinutes(SlotMinutes - extra);
    }

    private static string Local(DateTimeOffset time) => time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset t) => (t.Hour, t.Minute) switch
    {
        (0, 0) => "midnight",
        (12, 0) => "noon",
        _ => t.ToString(t.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture),
    };
}
