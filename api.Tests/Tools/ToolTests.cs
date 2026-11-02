using Kilowattcher.Api.Households;
using System.Text.Json;
using Kilowattcher.Api.Rag;
using Kilowattcher.Api.Tests.TestSupport;
using Kilowattcher.Api.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kilowattcher.Api.Tests.Tools;

public class ToolTests
{
    // 2026-11-01 20:00 UTC is 13:00 on Sunday 2026-11-01 in Phoenix.
    private static readonly HomeClock Clock = HomeClock.For(new MutableTimeProvider(new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero)));

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Home_clock_is_phoenix_time_all_year()
    {
        Assert.Equal(new DateOnly(2026, 11, 1), Clock.Today);
        Assert.Equal(13, Clock.Now.Hour);

        // 03:00 UTC on Nov 2 is still the evening of Nov 1 in Phoenix; in July it's the same offset.
        Assert.Equal(new DateOnly(2026, 11, 1), HomeClock.For(new MutableTimeProvider(new DateTimeOffset(2026, 11, 2, 3, 0, 0, TimeSpan.Zero))).Today);
        Assert.Equal(TimeSpan.FromHours(-7), HomeClock.For(new MutableTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero))).Now.Offset);
    }

    [Fact]
    public void Home_clock_follows_daylight_saving_in_zones_that_use_it()
    {
        var summer = HomeClock.For(new MutableTimeProvider(new DateTimeOffset(2026, 7, 1, 16, 0, 0, TimeSpan.Zero)), "America/New_York");
        var winter = HomeClock.For(new MutableTimeProvider(new DateTimeOffset(2026, 12, 1, 17, 0, 0, TimeSpan.Zero)), "America/New_York");

        Assert.Equal(TimeSpan.FromHours(-4), summer.Now.Offset);
        Assert.Equal(TimeSpan.FromHours(-5), winter.Now.Offset);
        Assert.Equal(12, summer.Now.Hour);
        Assert.Contains("UTC-4", summer.Label);
    }

    [Fact]
    public void Battery_returns_the_approved_mock_values()
    {
        var status = Assert.IsType<BatteryStatus>(TestHouseholds.Default.Status);

        Assert.Equal("VoltNest 13", status.Product);
        Assert.Equal(13.5, status.CapacityKwh);
        Assert.Equal(42, status.StateOfChargePercent);
        Assert.Equal(20, status.BackupReservePercent);
        Assert.Equal(5.0, status.MaxChargeKw);
        Assert.Equal("discharging", status.Mode);
        Assert.Equal(1.2, status.PowerKw);
        Assert.Equal(Math.Round(status.CapacityKwh * status.StateOfChargePercent / 100, 2), status.StoredKwh);
    }

    [Fact]
    public void Rate_periods_cover_the_whole_day_without_gaps()
    {
        var schedule = Assert.IsType<RateSchedule>(RateScheduleTool.Execute(Args(new { date = "2026-11-02" }), TestHouseholds.Context(Clock)));

        Assert.Equal("2026-11-02", schedule.Date);
        Assert.Equal("00:00", schedule.Periods[0].Start);
        Assert.Equal("24:00", schedule.Periods[^1].End);
        for (var i = 1; i < schedule.Periods.Count; i++)
        {
            Assert.Equal(schedule.Periods[i - 1].End, schedule.Periods[i].Start);
        }

        Assert.Equal(0.11m, schedule.Periods.Min(p => p.PricePerKwh));
        Assert.Equal(0.48m, schedule.Periods.Single(p => p.Name == "Peak").PricePerKwh);
    }

    [Theory]
    [InlineData("tomorrow")]
    [InlineData("11/02/2026")]
    [InlineData("2026-13-01")]
    public void Rates_reject_badly_formatted_dates(string date)
    {
        Assert.IsType<ToolError>(RateScheduleTool.Execute(Args(new { date }), TestHouseholds.Context(Clock)));
    }

    private static SolarForecastTool Solar() => new(TestWeather.Recorded());

    [Fact]
    public async Task Solar_replays_the_recorded_forecast_with_a_cloudy_tomorrow()
    {
        // Clock: Sunday 2026-11-01 in Phoenix. The recording's first day (95% cloud) is served as tomorrow.
        var tomorrow = Assert.IsType<SolarForecast>(await Solar().ExecuteAsync(Args(new { date = "2026-11-02" }), TestHouseholds.Context(Clock), CancellationToken.None));
        var dayAfter = Assert.IsType<SolarForecast>(await Solar().ExecuteAsync(Args(new { date = "2026-11-03" }), TestHouseholds.Context(Clock), CancellationToken.None));

        Assert.Equal("cloudy", tomorrow.Conditions);
        Assert.InRange(tomorrow.TotalKwh, 7.0, 9.0);   // 1.40 kWh/m² × 7.2 kW × 0.8 ≈ 8.0
        Assert.Equal("sunny", dayAfter.Conditions);
        Assert.InRange(dayAfter.TotalKwh, 33.0, 36.0); // a clear November day, ≈ 34.4
        Assert.Contains("Open-Meteo.com", tomorrow.Source);
        Assert.All(tomorrow.Hourly, h => Assert.True(h.Kwh > 0));
    }

    [Fact]
    public async Task Solar_for_today_is_not_in_the_replay()
    {
        // The replay starts tomorrow, so today has no recorded hours; live Open-Meteo covers today.
        Assert.IsType<ToolError>(await Solar().ExecuteAsync(Args(new { date = "2026-11-01" }), TestHouseholds.Context(Clock), CancellationToken.None));
    }

    [Fact]
    public void Solar_math_is_system_size_times_irradiance_times_losses()
    {
        var day = new DateOnly(2026, 11, 2);
        IReadOnlyList<Kilowattcher.Api.Weather.WeatherHour> hours =
        [
            new(day.ToDateTime(new TimeOnly(11, 0)), 1000, 10, 25), // full sun: 7.2 × 1.0 × 0.8 = 5.76 kWh
            new(day.ToDateTime(new TimeOnly(12, 0)), 500, 10, 25),  // half sun: 2.88 kWh
            new(day.ToDateTime(new TimeOnly(22, 0)), 0, 10, 15),    // night: dropped from the hourly list
        ];

        var forecast = SolarForecastTool.Build(day, hours, "test");

        Assert.Equal([5.8, 2.9], forecast.Hourly.Select(h => h.Kwh));
        Assert.Equal(8.6, forecast.TotalKwh);
        Assert.Equal("sunny", forecast.Conditions);
    }

    [Theory]
    [InlineData(10, "sunny")]
    [InlineData(50, "partly cloudy")]
    [InlineData(90, "cloudy")]
    public void Conditions_follow_daytime_cloud_cover(double cloud, string expected)
    {
        var day = new DateOnly(2026, 11, 2);
        var hours = Enumerable.Range(8, 9).Select(h => new Kilowattcher.Api.Weather.WeatherHour(day.ToDateTime(new TimeOnly(h, 0)), 400, cloud, 20)).ToList();

        Assert.Equal(expected, SolarForecastTool.Build(day, hours, "test").Conditions);
    }

    [Theory]
    [InlineData("2026-10-31")] // yesterday
    [InlineData("2026-11-08")] // 7 days ahead
    [InlineData("not-a-date")]
    public async Task Solar_rejects_dates_outside_the_forecast_window(string date)
    {
        Assert.IsType<ToolError>(await Solar().ExecuteAsync(Args(new { date }), TestHouseholds.Context(Clock), CancellationToken.None));
    }

    [Fact]
    public async Task Weather_outages_become_a_note_not_a_crash()
    {
        var tool = new SolarForecastTool(new FailingWeather());

        var error = Assert.IsType<ToolError>(await tool.ExecuteAsync(Args(new { date = "2026-11-02" }), TestHouseholds.Context(Clock), CancellationToken.None));
        Assert.Contains("unavailable", error.Error);
    }

    private sealed class FailingWeather : Kilowattcher.Api.Weather.IWeatherSource
    {
        public string Description => "down";
        public Task<IReadOnlyList<Kilowattcher.Api.Weather.WeatherHour>> GetDayAsync(Kilowattcher.Api.Tools.HomeClock clock, DateOnly date, CancellationToken cancellationToken) =>
            throw new Kilowattcher.Api.Weather.WeatherUnavailableException("down");
    }

    [Fact]
    public void Every_tool_schema_is_valid_strict_json_schema()
    {
        ITool[] tools = [new BatteryStatusTool(), new RateScheduleTool(), Solar(), new ChargingWindowTool(TestWeather.Recorded()), new BackupEstimateTool(),
            new SearchDocsTool(TestIndex.Provider(DocIndex.Empty), new FakeEmbeddingClient(), NullLogger<SearchDocsTool>.Instance)];

        foreach (var tool in tools)
        {
            using var schema = JsonDocument.Parse(tool.ParametersSchema);
            var root = schema.RootElement;
            Assert.Equal("object", root.GetProperty("type").GetString());
            Assert.False(root.GetProperty("additionalProperties").GetBoolean());

            // Strict mode: every declared property must be required.
            var declared = root.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order();
            var required = root.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Order();
            Assert.Equal(declared, required);
        }
    }
}
