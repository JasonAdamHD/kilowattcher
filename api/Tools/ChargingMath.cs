using System.Globalization;
using Kilowattcher.Api.Households;

namespace Kilowattcher.Api.Tools;

/// <summary>How much grid charging a request needs: energy into the battery, energy from the grid, and time.</summary>
public sealed record ChargeNeed(int FromPercent, int TargetPercent, double EnergyToAddKwh, double GridEnergyKwh, int ChargeMinutes);

/// <summary>One rate period a charge runs through: when, at what price, how much energy, and what it costs.</summary>
public sealed record PeriodCost(string RatePeriod, string From, string To, decimal PricePerKwh, double GridEnergyKwh, decimal Cost);

/// <summary>
/// The charging math both charging tools share, so "the cheapest window" and "start now" can never disagree
/// about energy, time, or price.
/// </summary>
public static class ChargingMath
{
    /// <returns>A <see cref="ChargeNeed"/>, or a <see cref="ToolError"/> when the percentages don't make sense.</returns>
    public static object Need(Household household, double? fromPercent, double? targetPercent)
    {
        var target = (int)Math.Round(targetPercent ?? 100);
        if (target is < 1 or > 100)
        {
            return new ToolError("target_percent must be between 1 and 100.");
        }

        var from = (int)Math.Round(fromPercent ?? household.StateOfChargePercent);
        if (from is < 0 or > 100)
        {
            return new ToolError("from_percent must be between 0 and 100.");
        }

        if (from >= target)
        {
            return new ToolError($"Starting at {from}%, the battery is already at or above the {target}% target. No grid charging needed.");
        }

        var battery = household.Battery;
        var energyToAdd = battery.CapacityKwh * (target - from) / 100.0;
        return new ChargeNeed(
            from,
            target,
            energyToAdd,
            energyToAdd / battery.RoundTripEfficiency,
            (int)Math.Ceiling(energyToAdd / battery.MaxChargeKw * 60));
    }

    /// <summary>
    /// Prices a charge that starts at <paramref name="start"/> and runs at full rate until done, minute by minute,
    /// so a charge that crosses from one rate period into the next pays each period's price for its share.
    /// </summary>
    public static IReadOnlyList<PeriodCost> Price(DateTimeOffset start, ChargeNeed need, IReadOnlyList<RatePeriod> periods, TimeZoneInfo zone)
    {
        var perMinute = need.GridEnergyKwh / need.ChargeMinutes;
        var parts = new List<(RatePeriod Period, DateTimeOffset From, int Minutes)>();
        for (var minute = 0; minute < need.ChargeMinutes; minute++)
        {
            var local = TimeZoneInfo.ConvertTime(start.AddMinutes(minute), zone);
            var period = PeriodAt(local, periods);
            if (parts.Count > 0 && parts[^1].Period == period)
            {
                parts[^1] = parts[^1] with { Minutes = parts[^1].Minutes + 1 };
            }
            else
            {
                parts.Add((period, local, 1));
            }
        }

        return parts.Select(p =>
        {
            var energy = perMinute * p.Minutes;
            return new PeriodCost(
                p.Period.Name,
                Local(p.From),
                Local(TimeZoneInfo.ConvertTime(p.From.AddMinutes(p.Minutes), zone)),
                p.Period.PricePerKwh,
                Math.Round(energy, 2),
                Math.Round((decimal)energy * p.Period.PricePerKwh, 2));
        }).ToList();
    }

    /// <summary>The total, from unrounded energy so the parts' rounding doesn't add up to a cent off.</summary>
    public static decimal Total(DateTimeOffset start, ChargeNeed need, IReadOnlyList<RatePeriod> periods, TimeZoneInfo zone)
    {
        var priceMinutes = 0m;
        for (var minute = 0; minute < need.ChargeMinutes; minute++)
        {
            priceMinutes += PeriodAt(TimeZoneInfo.ConvertTime(start.AddMinutes(minute), zone), periods).PricePerKwh;
        }

        // Grid energy x the average price over the charge, rounded once.
        return Math.Round((decimal)need.GridEnergyKwh * priceMinutes / need.ChargeMinutes, 2);
    }

    public static RatePeriod PeriodAt(DateTimeOffset localTime, IReadOnlyList<RatePeriod> periods)
    {
        var time = localTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        return periods.First(p => string.CompareOrdinal(time, p.Start) >= 0 && string.CompareOrdinal(time, p.End) < 0);
    }

    public static string Local(DateTimeOffset time) => time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
