using Kilowattcher.Api.Households;
using System.Reflection;
using System.Text.Json;
using Kilowattcher.Api.Rag;
using Kilowattcher.Api.Tests.TestSupport;
using Kilowattcher.Api.Tools;

namespace Kilowattcher.Api.Tests.Tools;

public class ChargingWindowToolTests
{
    /// <summary>A Phoenix clock (UTC-7, no daylight saving) at a local November time.</summary>
    private static HomeClock At(int day, int hour, int minute = 0) =>
        HomeClock.For(new MutableTimeProvider(new DateTimeOffset(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(-7))));

    private static ChargingPlan Plan(HomeClock clock, double? target = null, DateOnly? nightOf = null, double? from = null) =>
        Assert.IsType<ChargingPlan>(ChargingWindowTool.Plan(TestHouseholds.Default, clock, new ChargingRequest(nightOf, from, target)));

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
        Assert.IsType<ToolError>(ChargingWindowTool.Plan(TestHouseholds.Default, At(1, 13), new ChargingRequest(TargetPercent: target)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Targets_outside_1_to_100_are_errors(double target)
    {
        Assert.IsType<ToolError>(ChargingWindowTool.Plan(TestHouseholds.Default, At(1, 13), new ChargingRequest(TargetPercent: target)));
    }

    [Fact]
    public void Tuesday_night_at_a_third_full_plans_after_midnight_on_wednesday()
    {
        // Asked on Sunday 2026-11-01: "On Tuesday, should I charge at night if it's only 1/3 full?"
        var plan = Plan(At(1, 13), nightOf: new DateOnly(2026, 11, 3), from: 33);

        Assert.Equal(33, plan.StartingPercent);
        Assert.Equal("as asked", plan.StartingPercentSource);
        Assert.Equal(9.04, plan.EnergyToAddKwh);           // (100 - 33)% of 13.5 kWh = 9.045, stored as 9.0449…
        Assert.Equal(109, plan.ChargeMinutes);             // 9.045 kWh ÷ 5 kW
        Assert.Equal("2026-11-04 00:00", plan.CheapestWindow.Start);
        Assert.Equal("Tuesday night (early Wednesday), midnight to 1:49 AM", plan.CheapestWindow.Description);
        Assert.Equal(1.11m, plan.EstimatedCost);           // 9.045 ÷ 0.9 × $0.11 = $1.106
        Assert.Equal("2026-11-03 18:00", plan.SearchedFrom);
        Assert.Equal("2026-11-04 10:00", plan.SearchedTo);
    }

    [Fact]
    public void Planning_for_tonight_by_date_matches_the_next_24_hours()
    {
        var byDate = Plan(At(1, 13), nightOf: new DateOnly(2026, 11, 1));
        var next24 = Plan(At(1, 13));

        Assert.Equal(next24.CheapestWindow, byDate.CheapestWindow);
    }

    [Theory]
    [InlineData("2026-10-31")] // yesterday
    [InlineData("2026-11-08")] // 7 days ahead
    [InlineData("someday")]
    [InlineData("3")]
    public void Nights_outside_the_week_or_badly_written_are_errors(string night)
    {
        var result = ChargingWindowTool.Execute(
            JsonSerializer.SerializeToElement(new { night_of = night, from_percent = (double?)null, target_percent = (double?)null }),
            TestHouseholds.Context(At(1, 13)));

        Assert.IsType<ToolError>(result);
    }

    [Theory]
    // Today is Sunday 2026-11-01.
    [InlineData("tonight", "2026-11-01")]
    [InlineData("Tonight", "2026-11-01")]
    [InlineData("tomorrow", "2026-11-02")]
    [InlineData("tomorrow night", "2026-11-02")]
    [InlineData("Tuesday", "2026-11-03")]
    [InlineData("tuesday night", "2026-11-03")]
    [InlineData("this Saturday", "2026-11-07")]
    [InlineData("Sunday", "2026-11-01")]              // today's weekday means today
    [InlineData("2026-11-05", "2026-11-05")]
    public void Night_words_resolve_to_dates_in_code(string words, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), ChargingWindowTool.ResolveNight(words, new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public async Task On_tuesday_at_a_third_full_with_a_sunny_wednesday_it_is_not_needed()
    {
        // The demo question. Replay: tomorrow (Mon) is cloudy; Wednesday is the recording's 3rd day, sunny.
        var clock = At(1, 13);
        var tool = new ChargingWindowTool(TestWeather.Recorded());

        var plan = Assert.IsType<ChargingPlan>(await tool.ExecuteAsync(JsonSerializer.SerializeToElement(
            new { night_of = "Tuesday", from_percent = 33.0, target_percent = (double?)null }), TestHouseholds.Context(clock), CancellationToken.None));

        Assert.Equal("Tuesday night (early Wednesday), midnight to 1:49 AM", plan.CheapestWindow.Description);
        Assert.Equal("Wednesday", plan.NextDay!.Weekday);
        Assert.Equal("sunny", plan.NextDay.Conditions);
        Assert.Equal("not_needed", plan.Recommendation);
        Assert.Contains("refill the battery for free", plan.RecommendationReason);
    }

    [Fact]
    public async Task Tonight_before_a_cloudy_day_it_is_worth_it()
    {
        var clock = At(1, 13);
        var tool = new ChargingWindowTool(TestWeather.Recorded());

        var plan = Assert.IsType<ChargingPlan>(await tool.ExecuteAsync(JsonSerializer.SerializeToElement(
            new { night_of = "tonight", from_percent = (double?)null, target_percent = (double?)null }), TestHouseholds.Context(clock), CancellationToken.None));

        Assert.Equal("Monday", plan.NextDay!.Weekday);
        Assert.Equal("cloudy", plan.NextDay.Conditions);
        Assert.Equal("worth_it", plan.Recommendation);
        Assert.Contains("$0.96 instead of $4.18", plan.RecommendationReason);
    }

    [Fact]
    public async Task Without_a_forecast_it_still_plans_but_makes_no_recommendation()
    {
        var clock = At(1, 13);
        var tool = new ChargingWindowTool(new NoWeather());

        var plan = Assert.IsType<ChargingPlan>(await tool.ExecuteAsync(JsonSerializer.SerializeToElement(
            new { night_of = (string?)null, from_percent = (double?)null, target_percent = (double?)null }), TestHouseholds.Context(clock), CancellationToken.None));

        Assert.Equal("unknown", plan.Recommendation);
        Assert.Null(plan.NextDay);
        Assert.Equal(0.11m, plan.CheapestWindow.PricePerKwh);
    }

    private sealed class NoWeather : Kilowattcher.Api.Weather.IWeatherSource
    {
        public string Description => "down";
        public Task<IReadOnlyList<Kilowattcher.Api.Weather.WeatherHour>> GetDayAsync(Kilowattcher.Api.Tools.HomeClock clock, DateOnly date, CancellationToken cancellationToken) =>
            throw new Kilowattcher.Api.Weather.WeatherUnavailableException("down");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Starting_levels_outside_0_to_100_are_errors(double from)
    {
        Assert.IsType<ToolError>(ChargingWindowTool.Plan(TestHouseholds.Default, At(1, 13), new ChargingRequest(FromPercent: from)));
    }

    [Fact]
    public void Daylight_saving_nights_still_start_at_local_midnight()
    {
        // New York on the night the clocks go back (2026-11-01, 2 AM → 1 AM). Asked at 6 PM on Oct 31.
        var clock = HomeClock.For(new MutableTimeProvider(new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero)), "America/New_York");

        var plan = Plan(clock);

        Assert.Equal("2026-11-01 00:00", plan.CheapestWindow.Start);
        Assert.StartsWith("tonight, midnight", plan.CheapestWindow.Description);
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
        Assert.Equal(expected, ChargingWindowTool.PeriodAt(At(1, hour, minute).Now, RateScheduleTool.DefaultPeriods).Name);
    }

    [Fact]
    public void Runs_through_the_tool_interface_with_a_null_target()
    {
        var clock = HomeClock.For(new MutableTimeProvider(new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero))); // 13:00 Phoenix
        var result = ChargingWindowTool.Execute(JsonSerializer.SerializeToElement(
            new { night_of = (string?)null, from_percent = (double?)null, target_percent = (double?)null }), TestHouseholds.Context(clock));

        Assert.Equal(100, Assert.IsType<ChargingPlan>(result).TargetPercent);
    }

    [Fact]
    public void Efficiency_matches_the_battery_manual()
    {
        var root = typeof(ChargingWindowToolTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!;
        var section = MarkdownChunker.ChunkDirectory(Path.Combine(root, "api", "Data", "docs"))
            .Single(c => c.Id == "battery-manual#charging-from-the-grid");

        Assert.Contains($"{BatteryCatalog.VoltNest13.RoundTripEfficiency:P0}".Replace(" ", ""), section.Text.Replace(" ", ""));
    }
}
