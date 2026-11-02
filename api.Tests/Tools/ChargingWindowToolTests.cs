using System.Reflection;
using System.Text.Json;
using Kilowattcher.Api.Rag;
using Kilowattcher.Api.Tests.TestSupport;
using Kilowattcher.Api.Tools;

namespace Kilowattcher.Api.Tests.Tools;

public class ChargingWindowToolTests
{
    private static readonly TimeSpan Phoenix = TimeSpan.FromHours(-7);

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, Phoenix);

    private static ChargingPlan Plan(DateTimeOffset now, double? target = null) =>
        Assert.IsType<ChargingPlan>(ChargingWindowTool.Plan(BatteryStatusTool.Status, now, target));

    [Fact]
    public void Asked_in_the_afternoon_the_cheapest_window_is_tonight_after_midnight()
    {
        // The exact case the model got wrong: midnight to 6 AM belongs to tomorrow's date.
        var plan = Plan(At(1, 13));

        Assert.Equal("2026-11-02 00:00", plan.CheapestWindow.Start);
        Assert.Equal("Super off-peak", plan.CheapestWindow.RatePeriod);
        Assert.Equal(0.11m, plan.CheapestWindow.PricePerKwh);
        Assert.Equal("tonight, midnight to 1:34 AM", plan.CheapestWindow.Description);
    }

    [Fact]
    public void Energy_time_and_cost_follow_the_battery_and_the_rates()
    {
        // 42% → 100% of 13.5 kWh = 7.83 kWh; ÷ 0.9 efficiency = 8.7 kWh from the grid; ÷ 5 kW = 94 minutes.
        var plan = Plan(At(1, 13));

        Assert.Equal(7.83, plan.EnergyToAddKwh);
        Assert.Equal(8.7, plan.GridEnergyKwh);
        Assert.Equal(94, plan.ChargeMinutes); // matches the window: midnight to 1:34 AM
        Assert.Equal(0.96m, plan.EstimatedCost);        // 8.7 × $0.11
        Assert.Equal(4.18m, plan.CostIfChargedAtPeak);  // 8.7 × $0.48
    }

    [Fact]
    public void Asked_late_in_the_evening_it_still_waits_for_midnight()
    {
        var plan = Plan(At(1, 22, 10));

        Assert.Equal("2026-11-02 00:00", plan.CheapestWindow.Start);
        Assert.StartsWith("tonight", plan.CheapestWindow.Description);
    }

    [Fact]
    public void Asked_during_the_cheap_hours_it_starts_at_the_next_quarter_hour()
    {
        var plan = Plan(At(2, 1, 5));

        Assert.Equal("2026-11-02 01:15", plan.CheapestWindow.Start);
        Assert.Equal(0.11m, plan.CheapestWindow.PricePerKwh);
    }

    [Fact]
    public void Too_little_cheap_time_left_tonight_moves_to_the_next_night()
    {
        // At 5:30 AM only 30 minutes of $0.11 remain; a 1.6 h charge can't fit, so it waits a day.
        var plan = Plan(At(2, 5, 30));

        Assert.Equal("2026-11-03 00:00", plan.CheapestWindow.Start);
        Assert.Equal(0.11m, plan.CheapestWindow.PricePerKwh);
    }

    [Fact]
    public void A_lower_target_needs_less_energy_and_time()
    {
        var plan = Plan(At(1, 13), target: 80);

        Assert.Equal(80, plan.TargetPercent);
        Assert.Equal(5.13, plan.EnergyToAddKwh); // (80 - 42)% of 13.5
        Assert.True(plan.ChargeMinutes < 94);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(30)]
    public void Nothing_to_do_at_or_above_the_target(double target)
    {
        Assert.IsType<ToolError>(ChargingWindowTool.Plan(BatteryStatusTool.Status, At(1, 13), target));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Targets_outside_1_to_100_are_errors(double target)
    {
        Assert.IsType<ToolError>(ChargingWindowTool.Plan(BatteryStatusTool.Status, At(1, 13), target));
    }

    [Theory]
    [InlineData(0, 0, "Super off-peak")]
    [InlineData(5, 59, "Super off-peak")]
    [InlineData(6, 0, "Off-peak")]
    [InlineData(16, 0, "Peak")]
    [InlineData(20, 59, "Peak")]
    [InlineData(21, 0, "Off-peak")]
    [InlineData(23, 59, "Off-peak")]
    public void Every_minute_of_the_day_maps_to_one_rate_period(int hour, int minute, string expected)
    {
        Assert.Equal(expected, ChargingWindowTool.PeriodAt(At(1, hour, minute)).Name);
    }

    [Fact]
    public void Runs_through_the_tool_interface_with_a_null_target()
    {
        var clock = new HomeClock(new MutableTimeProvider(new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero))); // 13:00 Phoenix
        var result = new ChargingWindowTool(clock).Execute(JsonSerializer.SerializeToElement(new { target_percent = (double?)null }));

        Assert.Equal(100, Assert.IsType<ChargingPlan>(result).TargetPercent);
    }

    [Fact]
    public void Efficiency_matches_the_battery_manual()
    {
        var root = typeof(ChargingWindowToolTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!;
        var section = MarkdownChunker.ChunkDirectory(Path.Combine(root, "api", "Data", "docs"))
            .Single(c => c.Id == "battery-manual#charging-from-the-grid");

        Assert.Contains($"{ChargingWindowTool.RoundTripEfficiency:P0}".Replace(" ", ""), section.Text.Replace(" ", ""));
    }
}
