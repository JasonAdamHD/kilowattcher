using System.Reflection;
using System.Text.Json;
using Kilowattcher.Api.Rag;
using Kilowattcher.Api.Tools;

namespace Kilowattcher.Api.Tests.Tools;

public class BackupEstimateToolTests
{
    private static JsonElement Load(double? kw) => JsonSerializer.SerializeToElement(new { load_kw = kw });

    [Fact]
    public void Current_load_gives_the_documented_answer()
    {
        // (42% - 5%) x 13.5 kWh / 1.2 kW = 4.995 / 1.2 = 4.16 → 4.2 hours, the worked example in outage-backup.md.
        var estimate = Assert.IsType<BackupEstimate>(new BackupEstimateTool().Execute(Load(null)));

        Assert.Equal(42, estimate.StateOfChargePercent);
        Assert.Equal(5, estimate.BackupFloorPercent);
        Assert.Equal(5.0, estimate.UsableKwh, precision: 2);
        Assert.Equal(1.2, estimate.LoadKw);
        Assert.Equal(4.2, estimate.EstimatedHours);
        Assert.Equal("(42% - 5%) x 13.5 kWh / 1.2 kW", estimate.Formula);
    }

    [Theory]
    [InlineData(0.5, 10.0)]
    [InlineData(2.5, 2.0)]
    [InlineData(5.0, 1.0)]
    public void A_requested_load_changes_only_the_division(double loadKw, double hours)
    {
        var estimate = Assert.IsType<BackupEstimate>(new BackupEstimateTool().Execute(Load(loadKw)));

        Assert.Equal(hours, estimate.EstimatedHours);
        Assert.Equal("requested", estimate.LoadSource);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)] // over the 5 kW the battery can supply
    public void Impossible_loads_are_errors(double loadKw)
    {
        Assert.IsType<ToolError>(new BackupEstimateTool().Execute(Load(loadKw)));
    }

    [Fact]
    public void Without_a_current_load_the_model_must_supply_one()
    {
        var idle = BatteryStatusTool.Status with { Mode = "idle", PowerKw = 0 };

        Assert.IsType<ToolError>(BackupEstimateTool.Estimate(idle, null));
        Assert.IsType<BackupEstimate>(BackupEstimateTool.Estimate(idle, 1.0));
    }

    [Fact]
    public void A_battery_at_or_below_the_floor_has_no_backup()
    {
        var empty = BatteryStatusTool.Status with { StateOfChargePercent = 4 };

        var estimate = Assert.IsType<BackupEstimate>(BackupEstimateTool.Estimate(empty, 1.0));

        Assert.Equal(0, estimate.EstimatedHours);
    }

    [Fact]
    public void Code_and_documents_agree_on_the_floor_and_the_worked_example()
    {
        var root = typeof(BackupEstimateToolTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!;
        var section = MarkdownChunker.ChunkDirectory(Path.Combine(root, "api", "Data", "docs"))
            .Single(c => c.Id == "outage-backup#how-long-backup-lasts");

        Assert.Contains($"{BackupEstimateTool.BackupFloorPercent}%", section.Text);
        Assert.Contains("4.2 hours", section.Text);
    }

    [Fact]
    public void Schema_is_strict()
    {
        using var schema = JsonDocument.Parse(new BackupEstimateTool().ParametersSchema);

        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("load_kw", Assert.Single(schema.RootElement.GetProperty("required").EnumerateArray()).GetString());
    }
}
