using Kilowattcher.Api.Tools;
using Microsoft.Extensions.Options;

namespace Kilowattcher.Api.Households;

/// <summary>Defaults for every visitor, from appsettings.json ("Household"); the settings panel can override them.</summary>
public sealed class HouseholdOptions
{
    public const string Section = "Household";

    public string Battery { get; set; } = BatteryCatalog.VoltNest13.Id;
    public int StateOfChargePercent { get; set; } = 42;
    public double CurrentLoadKw { get; set; } = 1.2;
    public double SolarSystemKw { get; set; } = 7.2;
    public RatePrices Prices { get; set; } = new();
}

public sealed class RatePrices
{
    public decimal SuperOffPeak { get; set; } = 0.11m;
    public decimal OffPeak { get; set; } = 0.24m;
    public decimal Peak { get; set; } = 0.48m;
}

/// <summary>What a visitor may change, sent with each chat request. Anything left null keeps the default.</summary>
public sealed record HouseholdSettings(
    string? Battery = null,
    int? StateOfChargePercent = null,
    string? City = null,
    decimal? SuperOffPeakPrice = null,
    decimal? OffPeakPrice = null,
    decimal? PeakPrice = null);

/// <summary>Everything the tools know about one household, for one request.</summary>
public sealed record Household(
    BatteryModel Battery,
    int StateOfChargePercent,
    double CurrentLoadKw,
    double SolarSystemKw,
    IReadOnlyList<RatePeriod> RatePeriods,
    HomeLocation Location,
    bool Customized)
{
    /// <summary>The live battery reading the tools report. Discharging at the home's current load.</summary>
    public BatteryStatus Status => new(
        Battery.Name,
        Battery.CapacityKwh,
        StateOfChargePercent,
        Math.Round(Battery.CapacityKwh * StateOfChargePercent / 100.0, 2),
        Battery.DefaultReservePercent,
        Battery.MaxChargeKw,
        Battery.MaxDischargeKw,
        CurrentLoadKw > 0 ? "discharging" : "idle",
        CurrentLoadKw);

    /// <summary>One line for the system prompt, so the model knows which battery and prices are in play.</summary>
    public string Summary =>
        FormattableString.Invariant(
            $"{Battery.Name} battery ({Battery.CapacityKwh} kWh, backup floor {Battery.BackupFloorPercent}%) at {StateOfChargePercent}%, ") +
        FormattableString.Invariant(
            $"{SolarSystemKw} kW of solar, in {Location.City}; prices ${RatePeriods.Min(p => p.PricePerKwh)} to ${RatePeriods.Max(p => p.PricePerKwh)} per kWh") +
        (Customized ? " (the visitor changed some of these in settings)." : ".");

    internal static IReadOnlyList<RatePeriod> Periods(decimal superOffPeak, decimal offPeak, decimal peak) =>
    [
        new("Super off-peak", "00:00", "06:00", superOffPeak),
        new("Off-peak", "06:00", "16:00", offPeak),
        new("Peak", "16:00", "21:00", peak),
        new("Off-peak", "21:00", "24:00", offPeak),
    ];
}

/// <summary>Builds the household for a request: defaults from config, then the visitor's valid settings on top.</summary>
public sealed class HouseholdResolver(IOptions<HouseholdOptions> options, IOptions<HomeLocation> location)
{
    public const decimal MaxPrice = 2.00m;

    public Household Default => Resolve(null, out _)!;

    /// <returns>The household, or null with an error code when a setting is out of range.</returns>
    public Household? Resolve(HouseholdSettings? settings, out string? error)
    {
        error = null;
        var defaults = options.Value;
        var battery = BatteryCatalog.Find(settings?.Battery ?? defaults.Battery);
        if (battery is null)
        {
            error = "unknown_battery";
            return null;
        }

        var charge = settings?.StateOfChargePercent ?? defaults.StateOfChargePercent;
        if (charge is < 0 or > 100)
        {
            error = "invalid_charge";
            return null;
        }

        var home = location.Value;
        if (settings?.City is { } cityId)
        {
            if (CityCatalog.Find(cityId) is not { } city)
            {
                error = "unknown_city";
                return null;
            }

            home = city.ToLocation();
        }

        var prices = new[]
        {
            settings?.SuperOffPeakPrice ?? defaults.Prices.SuperOffPeak,
            settings?.OffPeakPrice ?? defaults.Prices.OffPeak,
            settings?.PeakPrice ?? defaults.Prices.Peak,
        };
        if (prices.Any(p => p <= 0 || p > MaxPrice))
        {
            error = "invalid_price";
            return null;
        }

        var customized = settings is not null && settings != new HouseholdSettings();
        return new Household(
            battery, charge, defaults.CurrentLoadKw, defaults.SolarSystemKw,
            Household.Periods(Math.Round(prices[0], 2), Math.Round(prices[1], 2), Math.Round(prices[2], 2)),
            home, customized);
    }
}

/// <summary>Passed to every tool call: this request's household and a clock in its time zone.</summary>
public sealed record ToolContext(Household Household, HomeClock Clock)
{
    public static ToolContext For(Household household, TimeProvider time) => new(household, new HomeClock(time, household.Location));
}
