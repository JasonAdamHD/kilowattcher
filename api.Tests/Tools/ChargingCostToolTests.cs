using System.Text.Json;
using Kilowattcher.Api.Tests.TestSupport;
using Kilowattcher.Api.Tools;

namespace Kilowattcher.Api.Tests.Tools;

public class ChargingCostToolTests
{
    /// <summary>A Phoenix clock (UTC-7, no daylight saving) at a local November time.</summary>
    private static HomeClock At(int day, int hour, int minute = 0) =>
        HomeClock.For(new MutableTimeProvider(new DateTimeOffset(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(-7))));

    private static ChargingCostEstimate Cost(HomeClock clock, string start, double? from = 0, double? target = null) =>
        Assert.IsType<ChargingCostEstimate>(ChargingCostTool.Execute(
            JsonSerializer.SerializeToElement(new { start, from_percent = from, target_percent = target }),
            TestHouseholds.Context(clock)));

    [Fact]
    public void Right_now_in_the_evening_runs_out_peak_then_off_peak_and_waiting_saves_money()
    {
        // The question the model couldn't answer: empty VoltNest at 8:40 PM. 13.5 kWh ÷ 0.9 = 15 kWh from the
        // grid, 13.5 ÷ 5 kW = 162 minutes: 20 minutes of peak ($0.48, until 9 PM), then 142 of off-peak ($0.24).
        // 15 × (20 × 0.48 + 142 × 0.24) ÷ 162 = $4.04. Waiting for midnight at $0.11 would be $1.65.
        var cost = Cost(At(1, 20, 40), "now");

        Assert.Equal("2026-11-01 20:40", cost.Start);
        Assert.Equal("2026-11-01 23:22", cost.End);
        Assert.Equal("this evening, 8:40 PM to 11:22 PM", cost.Description);
        Assert.Equal(15.0, cost.GridEnergyKwh);
        Assert.Equal(162, cost.ChargeMinutes);
        Assert.Equal(4.04m, cost.TotalCost);
        Assert.Equal(["Peak", "Off-peak"], cost.Breakdown.Select(p => p.RatePeriod));
        Assert.Equal([0.48m, 0.24m], cost.Breakdown.Select(p => p.PricePerKwh));
        Assert.Equal("2026-11-02 00:00", cost.CheapestWindow!.Start);
        Assert.Equal(1.65m, cost.CheapestWindowCost);
        Assert.Equal(2.39m, cost.SavingsIfYouWait);
    }

    [Fact]
    public void A_charge_that_crosses_midnight_pays_each_periods_price_for_its_share()
    {
        // 10 PM start: 120 minutes at $0.24, then 42 at $0.11. 15 kWh × (120 × 0.24 + 42 × 0.11) ÷ 162 = $3.09.
        var cost = Cost(At(1, 13), "22:00");

        Assert.Equal("2026-11-01 22:00", cost.Start);
        Assert.Equal(3.09m, cost.TotalCost);
        Assert.Equal(["Off-peak", "Super off-peak"], cost.Breakdown.Select(p => p.RatePeriod));
        Assert.Equal(["2026-11-01 22:00", "2026-11-02 00:00"], cost.Breakdown.Select(p => p.From));
        Assert.Equal(11.11, cost.Breakdown[0].GridEnergyKwh);
        Assert.Equal(3.89, cost.Breakdown[1].GridEnergyKwh);
    }

    [Fact]
    public void Starting_before_peak_runs_into_peak_prices()
    {
        // 3 PM start: 60 minutes at $0.24, then 102 at $0.48. 15 × (14.4 + 48.96) ÷ 162 = $5.87.
        var cost = Cost(At(1, 13), "15:00");

        Assert.Equal(5.87m, cost.TotalCost);
        Assert.Equal(["Off-peak", "Peak"], cost.Breakdown.Select(p => p.RatePeriod));
    }

    [Fact]
    public void A_time_that_has_passed_today_means_tomorrow()
    {
        var cost = Cost(At(1, 23), "22:00");

        Assert.Equal("2026-11-02 22:00", cost.Start);
        Assert.StartsWith("tomorrow", cost.Description);
    }

    [Fact]
    public void Without_from_percent_it_uses_the_live_battery_level()
    {
        // 42% → 100%: 8.7 kWh from the grid, 94 minutes from 8:40 PM: 20 at $0.48, 74 at $0.24.
        // 8.7 × (9.6 + 17.76) ÷ 94 = $2.53.
        var cost = Cost(At(1, 20, 40), "now", from: null);

        Assert.Equal(42, cost.StartingPercent);
        Assert.Equal("live battery level", cost.StartingPercentSource);
        Assert.Equal(8.7, cost.GridEnergyKwh);
        Assert.Equal(2.53m, cost.TotalCost);
        Assert.Equal(0.96m, cost.CheapestWindowCost); // the same number find_cheapest_charging_window gives
    }

    [Fact]
    public void The_second_battery_charges_slower_and_with_more_losses()
    {
        // Helio from 0: 10 kWh ÷ 0.88 = 11.36 kWh from the grid; 10 ÷ 3.8 kW = 158 minutes.
        var cost = Assert.IsType<ChargingCostEstimate>(ChargingCostTool.Estimate(
            TestHouseholds.Helio(), At(1, 20, 40), new DateTimeOffset(2026, 11, 1, 20, 40, 0, TimeSpan.FromHours(-7)), 0, null));

        Assert.Equal(11.36, cost.GridEnergyKwh);
        Assert.Equal(158, cost.ChargeMinutes);
        Assert.Contains("Helio PowerCell 10", cost.Note);
    }

    [Theory]
    [InlineData("10pm")]
    [InlineData("25:00")]
    [InlineData("tomorrow")]
    public void An_unclear_start_is_an_error(string start)
    {
        var result = ChargingCostTool.Execute(
            JsonSerializer.SerializeToElement(new { start, from_percent = (double?)null, target_percent = (double?)null }),
            TestHouseholds.Context(At(1, 13)));

        Assert.IsType<ToolError>(result);
    }

    [Fact]
    public void A_full_battery_needs_no_charging()
    {
        var result = ChargingCostTool.Execute(
            JsonSerializer.SerializeToElement(new { start = "now", from_percent = 100, target_percent = (double?)null }),
            TestHouseholds.Context(At(1, 13)));

        Assert.Contains("No grid charging needed", Assert.IsType<ToolError>(result).Error);
    }
}
