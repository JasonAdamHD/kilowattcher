using Kilowattcher.Api.Households;
using Kilowattcher.Api.Tools;
using Microsoft.Extensions.Options;

namespace Kilowattcher.Api.Tests.TestSupport;

/// <summary>Households for tests: the config defaults (VoltNest 13 at 42% in Phoenix), or variations.</summary>
public static class TestHouseholds
{
    public static HouseholdResolver Resolver { get; } =
        new(Options.Create(new HouseholdOptions()), Options.Create(new HomeLocation()));

    public static Household Default => Resolver.Default;

    public static Household With(HouseholdSettings settings) =>
        Resolver.Resolve(settings, out var error) ?? throw new ArgumentException(error);

    public static Household Helio(int charge = 42) =>
        With(new HouseholdSettings(Battery: BatteryCatalog.HelioPowerCell10.Id, StateOfChargePercent: charge));

    public static ToolContext Context(HomeClock clock, Household? household = null) => new(household ?? Default, clock);
}
