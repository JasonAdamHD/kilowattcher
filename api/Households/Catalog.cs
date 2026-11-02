using Kilowattcher.Api.Tools;

namespace Kilowattcher.Api.Households;

/// <summary>
/// A battery product's fixed specs. Each model's manual is tagged with <see cref="Product"/> in its front
/// matter, so search only returns the manual for the battery this household actually has.
/// </summary>
public sealed record BatteryModel(
    string Id,
    string Name,
    double CapacityKwh,
    double MaxChargeKw,
    double MaxDischargeKw,
    double RoundTripEfficiency,
    int BackupFloorPercent,
    int DefaultReservePercent,
    string Product);

public static class BatteryCatalog
{
    public static readonly BatteryModel VoltNest13 = new(
        "voltnest-13", "VoltNest 13", CapacityKwh: 13.5, MaxChargeKw: 5.0, MaxDischargeKw: 5.0,
        RoundTripEfficiency: 0.90, BackupFloorPercent: 5, DefaultReservePercent: 20, Product: "voltnest-13");

    public static readonly BatteryModel HelioPowerCell10 = new(
        "helio-powercell-10", "Helio PowerCell 10", CapacityKwh: 10.0, MaxChargeKw: 3.8, MaxDischargeKw: 3.8,
        RoundTripEfficiency: 0.88, BackupFloorPercent: 10, DefaultReservePercent: 15, Product: "helio-powercell-10");

    public static readonly IReadOnlyList<BatteryModel> All = [VoltNest13, HelioPowerCell10];

    public static BatteryModel? Find(string? id) => All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Cities a visitor can pick in the settings panel; live weather follows the choice.</summary>
public sealed record CityPreset(string Id, string City, double Latitude, double Longitude, string TimeZone)
{
    public HomeLocation ToLocation() => new() { City = City, Latitude = Latitude, Longitude = Longitude, TimeZone = TimeZone };
}

public static class CityCatalog
{
    public static readonly IReadOnlyList<CityPreset> All =
    [
        new("phoenix", "Phoenix, AZ", 33.4484, -112.0740, "America/Phoenix"),
        new("seattle", "Seattle, WA", 47.6062, -122.3321, "America/Los_Angeles"),
        new("miami", "Miami, FL", 25.7617, -80.1918, "America/New_York"),
        new("new-york", "New York, NY", 40.7128, -74.0060, "America/New_York"),
    ];

    public static CityPreset? Find(string? id) => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
}
