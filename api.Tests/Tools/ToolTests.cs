using System.Text.Json;
using Kilowattcher.Api.Tests.TestSupport;
using Kilowattcher.Api.Tools;

namespace Kilowattcher.Api.Tests.Tools;

public class ToolTests
{
    // 2026-11-01 20:00 UTC is 13:00 on Sunday 2026-11-01 in Phoenix.
    private static readonly HomeClock Clock = new(new MutableTimeProvider(new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero)));

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void Home_clock_is_phoenix_time_all_year()
    {
        Assert.Equal(new DateOnly(2026, 11, 1), Clock.Today);
        Assert.Equal(13, Clock.Now.Hour);

        // 03:00 UTC on Nov 2 is still the evening of Nov 1 in Phoenix; in July it's the same offset.
        Assert.Equal(new DateOnly(2026, 11, 1), new HomeClock(new MutableTimeProvider(new DateTimeOffset(2026, 11, 2, 3, 0, 0, TimeSpan.Zero))).Today);
        Assert.Equal(TimeSpan.FromHours(-7), new HomeClock(new MutableTimeProvider(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero))).Now.Offset);
    }

    [Fact]
    public void Battery_returns_the_approved_mock_values()
    {
        var status = Assert.IsType<BatteryStatus>(new BatteryStatusTool().Execute(Args(new { })));

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
        var schedule = Assert.IsType<RateSchedule>(new RateScheduleTool(Clock).Execute(Args(new { date = "2026-11-02" })));

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
        Assert.IsType<ToolError>(new RateScheduleTool(Clock).Execute(Args(new { date })));
    }

    [Fact]
    public void Solar_is_sunny_today_and_cloudy_tomorrow()
    {
        var tool = new SolarForecastTool(Clock);

        var today = Assert.IsType<SolarForecast>(tool.Execute(Args(new { date = "2026-11-01" })));
        var tomorrow = Assert.IsType<SolarForecast>(tool.Execute(Args(new { date = "2026-11-02" })));
        var dayAfter = Assert.IsType<SolarForecast>(tool.Execute(Args(new { date = "2026-11-03" })));

        Assert.Equal("sunny", today.Conditions);
        Assert.Equal(34.8, today.TotalKwh);
        Assert.Equal("cloudy", tomorrow.Conditions);
        Assert.InRange(tomorrow.TotalKwh, 9.5, 11.0);
        Assert.Equal(1.5, tomorrow.Hourly.Single(h => h.Hour == "12:00").Kwh);
        Assert.Equal("sunny", dayAfter.Conditions);
        Assert.Equal(13, today.Hourly.Count);
    }

    [Theory]
    [InlineData("2026-10-31")] // yesterday
    [InlineData("2026-11-08")] // 7 days ahead
    [InlineData("not-a-date")]
    public void Solar_rejects_dates_outside_the_forecast_window(string date)
    {
        Assert.IsType<ToolError>(new SolarForecastTool(Clock).Execute(Args(new { date })));
    }

    [Fact]
    public void Search_docs_stub_returns_no_results()
    {
        var result = Assert.IsType<DocSearchResult>(new SearchDocsTool().Execute(Args(new { query = "reserve percentage" })));

        Assert.Empty(result.Results);
    }

    [Fact]
    public void Every_tool_schema_is_valid_strict_json_schema()
    {
        ITool[] tools = [new BatteryStatusTool(), new RateScheduleTool(Clock), new SolarForecastTool(Clock), new SearchDocsTool()];

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
