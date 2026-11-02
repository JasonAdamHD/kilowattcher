using Kilowattcher.Api.Auth;
using Microsoft.Extensions.Options;

namespace Kilowattcher.Api.Households;

public sealed record BatteryOption(string Id, string Name, double CapacityKwh, double MaxChargeKw, double RoundTripEfficiency);

public sealed record CityOption(string Id, string City);

public sealed record SettingsDefaults(string Battery, int StateOfChargePercent, string? City, string CityName, decimal SuperOffPeakPrice, decimal OffPeakPrice, decimal PeakPrice);

public sealed record SettingsOptions(IReadOnlyList<BatteryOption> Batteries, IReadOnlyList<CityOption> Cities, SettingsDefaults Defaults, decimal MaxPrice);

public static class SettingsEndpoints
{
    /// <summary>GET /api/settings/options: what the settings panel can offer, and the defaults to reset to.</summary>
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings/options", (HttpContext http, SessionTokenService sessions,
            IOptions<HouseholdOptions> options, IOptions<Kilowattcher.Api.Tools.HomeLocation> home) =>
        {
            if (!http.HasValidSession(sessions))
            {
                return ApiErrors.Unauthorized();
            }

            var d = options.Value;
            var defaultCity = CityCatalog.All.FirstOrDefault(c => c.City == home.Value.City);

            return Results.Ok(new SettingsOptions(
                BatteryCatalog.All.Select(b => new BatteryOption(b.Id, b.Name, b.CapacityKwh, b.MaxChargeKw, b.RoundTripEfficiency)).ToList(),
                CityCatalog.All.Select(c => new CityOption(c.Id, c.City)).ToList(),
                new SettingsDefaults(d.Battery, d.StateOfChargePercent, defaultCity?.Id, home.Value.City,
                    d.Prices.SuperOffPeak, d.Prices.OffPeak, d.Prices.Peak),
                HouseholdResolver.MaxPrice));
        });
    }
}
