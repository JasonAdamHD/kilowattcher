using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Kilowattcher.Api.Households;
using Kilowattcher.Api.Rag;
using Kilowattcher.Api.Tests.TestSupport;
using Kilowattcher.Api.Tools;

namespace Kilowattcher.Api.Tests.Households;

public class HouseholdResolverTests
{
    [Fact]
    public void Defaults_come_from_config()
    {
        var home = TestHouseholds.Default;

        Assert.Equal("VoltNest 13", home.Battery.Name);
        Assert.Equal(42, home.StateOfChargePercent);
        Assert.Equal("Phoenix, AZ", home.Location.City);
        Assert.Equal([0.11m, 0.24m, 0.48m, 0.24m], home.RatePeriods.Select(p => p.PricePerKwh));
        Assert.False(home.Customized);
    }

    [Fact]
    public void Settings_override_battery_charge_city_and_prices()
    {
        var home = TestHouseholds.With(new HouseholdSettings(
            Battery: "helio-powercell-10", StateOfChargePercent: 80, City: "seattle",
            SuperOffPeakPrice: 0.09m, OffPeakPrice: 0.20m, PeakPrice: 0.55m));

        Assert.Equal("Helio PowerCell 10", home.Battery.Name);
        Assert.Equal(80, home.StateOfChargePercent);
        Assert.Equal("America/Los_Angeles", home.Location.TimeZone);
        Assert.Equal([0.09m, 0.20m, 0.55m, 0.20m], home.RatePeriods.Select(p => p.PricePerKwh));
        Assert.True(home.Customized);
        Assert.Contains("changed some of these in settings", home.Summary);
    }

    [Fact]
    public void Status_reflects_the_chosen_model()
    {
        var status = TestHouseholds.Helio(charge: 60).Status;

        Assert.Equal("Helio PowerCell 10", status.Product);
        Assert.Equal(10.0, status.CapacityKwh);
        Assert.Equal(6.0, status.StoredKwh);
        Assert.Equal(15, status.BackupReservePercent);
        Assert.Equal(3.8, status.MaxChargeKw);
    }

    [Theory]
    [InlineData("tesla-powerwall", null, null, null, "unknown_battery")]
    [InlineData(null, 101, null, null, "invalid_charge")]
    [InlineData(null, -1, null, null, "invalid_charge")]
    [InlineData(null, null, "atlantis", null, "unknown_city")]
    [InlineData(null, null, null, 0.0, "invalid_price")]
    [InlineData(null, null, null, 2.5, "invalid_price")]
    public void Out_of_range_settings_are_rejected(string? battery, int? charge, string? city, double? peak, string expected)
    {
        var home = TestHouseholds.Resolver.Resolve(new HouseholdSettings(battery, charge, city, PeakPrice: (decimal?)peak), out var error);

        Assert.Null(home);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void Load_sets_the_drain_and_reaches_the_tools()
    {
        var home = TestHouseholds.With(new HouseholdSettings(LoadKw: 2.5));

        Assert.Equal(2.5, home.CurrentLoadKw);
        Assert.Equal("discharging", home.Status.Mode);
        Assert.Contains("powering the home at 2.5 kW", home.Summary);
        // (42% - 5%) x 13.5 kWh / 2.5 kW = 2.0 hours
        Assert.Equal(2.0, Assert.IsType<BackupEstimate>(BackupEstimateTool.Estimate(home, null)).EstimatedHours);
    }

    [Fact]
    public void Zero_load_means_idle()
    {
        var home = TestHouseholds.With(new HouseholdSettings(LoadKw: 0));

        Assert.Equal("idle", home.Status.Mode);
        Assert.Contains("idle (not powering the home)", home.Summary);
        Assert.IsType<ToolError>(BackupEstimateTool.Estimate(home, null));
    }

    [Theory]
    [InlineData(null, 5.0, true)]                  // VoltNest 13 maxes out at 5 kW
    [InlineData(null, 5.1, false)]
    [InlineData("helio-powercell-10", 3.8, true)]  // Helio maxes out at 3.8 kW
    [InlineData("helio-powercell-10", 4.0, false)]
    [InlineData(null, -0.1, false)]
    public void Load_is_capped_at_the_batterys_max_discharge(string? battery, double load, bool ok)
    {
        var home = TestHouseholds.Resolver.Resolve(new HouseholdSettings(Battery: battery, LoadKw: load), out var error);

        Assert.Equal(ok, home is not null);
        Assert.Equal(ok ? null : "invalid_load", error);
    }
}

/// <summary>The second battery changes what the tools compute, not just the name on the label.</summary>
public class SecondBatteryToolTests
{
    private static readonly HomeClock Clock = HomeClock.For(new MutableTimeProvider(new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void Backup_time_uses_the_models_capacity_and_floor()
    {
        // Helio PowerCell 10 at 80%: (80% - 10% floor) x 10 kWh / 1.2 kW = 5.83 → 5.8 hours.
        var estimate = Assert.IsType<BackupEstimate>(BackupEstimateTool.Estimate(TestHouseholds.Helio(80), null));

        Assert.Equal(10, estimate.BackupFloorPercent);
        Assert.Equal(7.0, estimate.UsableKwh);
        Assert.Equal(5.8, estimate.EstimatedHours);
    }

    [Fact]
    public void Charging_uses_the_models_rate_and_efficiency()
    {
        // Helio at 42% → 100%: 5.8 kWh at 3.8 kW = 92 minutes; ÷ 0.88 = 6.59 kWh from the grid; × $0.11 ≈ $0.725 → $0.72.
        var plan = Assert.IsType<ChargingPlan>(ChargingWindowTool.Plan(TestHouseholds.Helio(), Clock, new ChargingRequest()));

        Assert.Equal(5.8, plan.EnergyToAddKwh);
        Assert.Equal(92, plan.ChargeMinutes);
        Assert.Equal(0.88, plan.RoundTripEfficiency);
        Assert.Equal(0.72m, plan.EstimatedCost);
        Assert.Contains("Helio PowerCell 10", plan.Note);
    }

    [Fact]
    public void Custom_prices_change_the_window_cost()
    {
        var cheap = TestHouseholds.With(new HouseholdSettings(SuperOffPeakPrice: 0.05m));

        var plan = Assert.IsType<ChargingPlan>(ChargingWindowTool.Plan(cheap, Clock, new ChargingRequest()));

        Assert.Equal(0.05m, plan.CheapestWindow.PricePerKwh);
        Assert.Equal(0.44m, plan.EstimatedCost); // 8.7 kWh × $0.05
    }

    [Fact]
    public void Rate_schedule_flags_prices_changed_in_settings()
    {
        var context = TestHouseholds.Context(Clock, TestHouseholds.With(new HouseholdSettings(PeakPrice: 0.60m)));
        var args = JsonSerializer.SerializeToElement(new { date = "2026-11-02" });

        var schedule = Assert.IsType<RateSchedule>(RateScheduleTool.Execute(args, context));

        Assert.True(schedule.PricesChangedInSettings);
        Assert.Equal(0.60m, schedule.Periods.Single(p => p.Name == "Peak").PricePerKwh);
    }
}

/// <summary>Metadata filtering: each household only ever sees its own battery's manual.</summary>
public class ProductFilterTests
{
    private static DocIndex Index() => new(2,
    [
        (new DocChunk("battery-manual#operating-modes", "VoltNest 13 Owner's Guide", "Operating modes", "Self-Powered, Time-Based Control, Backup Only.", "voltnest-13"), [1f, 0f]),
        (new DocChunk("helio-powercell-manual#operating-modes", "Helio PowerCell 10 User Manual", "Operating modes", "Solar First, Cost Saver, Storm Guard.", "helio-powercell-10"), [1f, 0.01f]),
        (new DocChunk("faq#what-does-state-of-charge-mean", "FAQ", "State of charge", "How full the battery is."), [0.5f, 0.5f]),
    ]);

    [Theory]
    [InlineData("voltnest-13", "battery-manual#operating-modes")]
    [InlineData("helio-powercell-10", "helio-powercell-manual#operating-modes")]
    public void Search_only_returns_this_households_manual_plus_shared_docs(string product, string expectedManual)
    {
        var ids = Index().Search([1f, 0f], top: 3, product).Select(m => m.Chunk.Id).ToList();

        Assert.Equal([expectedManual, "faq#what-does-state-of-charge-mean"], ids);
    }

    [Fact]
    public void Front_matter_tags_every_chunk_in_a_doc()
    {
        var chunks = MarkdownChunker.Chunk("m", "---\nproduct: helio-powercell-10\n---\n\n# Manual\n\n## A\n\nText A.\n\n## B\n\nText B.");

        Assert.All(chunks, c => Assert.Equal("helio-powercell-10", c.Product));
        Assert.Equal("Manual", chunks[0].Title);
    }

    [Fact]
    public void Tagging_a_doc_changes_its_hashes_so_it_gets_re_embedded()
    {
        var plain = MarkdownChunker.Chunk("m", "# Manual\n\n## A\n\nText A.")[0];
        var tagged = MarkdownChunker.Chunk("m", "---\nproduct: voltnest-13\n---\n# Manual\n\n## A\n\nText A.")[0];

        Assert.Equal(plain.Id, tagged.Id);
        Assert.NotEqual(plain.ContentHash, tagged.ContentHash);
    }

    [Fact]
    public void Unclosed_front_matter_is_an_error()
    {
        Assert.Throws<InvalidOperationException>(() => MarkdownChunker.Chunk("m", "---\nproduct: x\n# Manual\n\n## A\n\nText."));
    }

    [Fact]
    public void Every_battery_model_has_a_tagged_manual_with_its_specs()
    {
        var root = typeof(ProductFilterTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!;
        var chunks = MarkdownChunker.ChunkDirectory(Path.Combine(root, "api", "Data", "docs"));

        foreach (var model in BatteryCatalog.All)
        {
            var manual = string.Join("\n", chunks.Where(c => c.Product == model.Product).Select(c => c.Text));
            Assert.False(string.IsNullOrEmpty(manual), $"No manual tagged product: {model.Product}");
            Assert.Contains($"{model.CapacityKwh:0.#} kWh", manual);
            Assert.Contains($"{model.MaxChargeKw:0.0} kW", manual);
            Assert.Contains($"{model.RoundTripEfficiency:P0}".Replace(" ", ""), manual.Replace(" ", ""));
            Assert.Contains($"{model.DefaultReservePercent}%", manual);
        }

        // Shared docs carry no product tag and name both models' backup floors.
        var outage = chunks.Single(c => c.Id == "outage-backup#how-long-backup-lasts");
        Assert.Null(outage.Product);
        Assert.All(BatteryCatalog.All, m => Assert.Contains($"{m.BackupFloorPercent}% for the {m.Name}", outage.Text));
    }
}

/// <summary>The incremental embed plan: reuse what's unchanged, embed what's new or changed, drop what's gone.</summary>
public class EmbeddingPlanTests
{
    private static DocChunk Chunk(string id, string text = "Text.") => new(id, "T", id.Split('#')[1], text);

    private static EmbeddingFile Stored(params (DocChunk Chunk, float Value)[] chunks) => new("embed", 2,
        chunks.Select(c => new EmbeddedChunk(c.Chunk.Id, c.Chunk.Title, c.Chunk.Heading, c.Chunk.Text, c.Chunk.ContentHash, [c.Value, 0f], c.Chunk.Product)).ToList());

    [Fact]
    public void Sorts_chunks_into_unchanged_changed_added_and_removed()
    {
        var stored = Stored((Chunk("d#keep"), 1), (Chunk("d#edit", "Old."), 2), (Chunk("d#gone"), 3));
        IReadOnlyList<DocChunk> current = [Chunk("d#keep"), Chunk("d#edit", "New."), Chunk("d#new")];

        var plan = EmbeddingPlan.Create(current, stored, dimensions: 2);

        Assert.Equal(["d#keep"], plan.Unchanged.Select(c => c.Id));
        Assert.Equal(["d#edit"], plan.Changed.Select(c => c.Id));
        Assert.Equal(["d#new"], plan.Added.Select(c => c.Id));
        Assert.Equal(["d#gone"], plan.Removed);
        Assert.Equal(["d#edit", "d#new"], plan.ToEmbed.Select(c => c.Id));
        Assert.False(plan.IsUpToDate);
    }

    [Fact]
    public void Applying_keeps_old_vectors_uses_new_ones_and_drops_removed_chunks()
    {
        var stored = Stored((Chunk("d#keep"), 1), (Chunk("d#edit", "Old."), 2), (Chunk("d#gone"), 3));
        IReadOnlyList<DocChunk> current = [Chunk("d#keep"), Chunk("d#edit", "New."), Chunk("d#new")];
        var plan = EmbeddingPlan.Create(current, stored, dimensions: 2);

        var file = plan.Apply(stored, current, [[20f, 0f], [30f, 0f]], "embed", 2);

        Assert.Equal(["d#keep", "d#edit", "d#new"], file.Chunks.Select(c => c.Id));
        Assert.Equal([1f, 20f, 30f], file.Chunks.Select(c => c.Vector[0]));
        Assert.True(EmbeddingPlan.Create(current, file, 2).IsUpToDate);
    }

    [Fact]
    public void Renaming_a_heading_is_one_removal_plus_one_addition()
    {
        var stored = Stored((Chunk("d#old-name"), 1));

        var plan = EmbeddingPlan.Create([Chunk("d#new-name")], stored, dimensions: 2);

        Assert.Equal(["d#old-name"], plan.Removed);
        Assert.Equal(["d#new-name"], plan.Added.Select(c => c.Id));
    }

    [Fact]
    public void A_different_vector_size_forces_a_full_rebuild()
    {
        var stored = Stored((Chunk("d#a"), 1));

        var plan = EmbeddingPlan.Create([Chunk("d#a")], stored, dimensions: 512);

        Assert.True(plan.FullRebuild);
        Assert.Equal(["d#a"], plan.ToEmbed.Select(c => c.Id));
    }

    [Fact]
    public void Nothing_to_do_when_docs_match()
    {
        var stored = Stored((Chunk("d#a"), 1));

        Assert.True(EmbeddingPlan.Create([Chunk("d#a")], stored, dimensions: 2).IsUpToDate);
    }
}

/// <summary>Settings through the real endpoints.</summary>
public class SettingsEndpointTests : IDisposable
{
    private readonly KilowattcherFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Options_list_models_cities_and_defaults()
    {
        var visitor = await _factory.CreateSignedInVisitor();

        using var body = JsonDocument.Parse(await visitor.GetStringAsync("/api/settings/options"));

        Assert.Equal(["voltnest-13", "helio-powercell-10"], body.RootElement.GetProperty("batteries").EnumerateArray().Select(b => b.GetProperty("id").GetString()));
        Assert.Equal(4, body.RootElement.GetProperty("cities").GetArrayLength());
        Assert.Equal("voltnest-13", body.RootElement.GetProperty("defaults").GetProperty("battery").GetString());
        Assert.Equal("phoenix", body.RootElement.GetProperty("defaults").GetProperty("city").GetString());
    }

    [Fact]
    public async Task Options_require_a_session()
    {
        var response = await _factory.CreateVisitor().GetAsync("/api/settings/options");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chat_settings_reach_the_tools()
    {
        _factory.ChatModel.Then(Steps.Call("get_battery_status")).Then(Steps.Final("It's a Helio at 75%."));
        var visitor = await _factory.CreateSignedInVisitor();

        var response = await visitor.PostAsJsonAsync("/api/chat", new
        {
            message = "What's my battery at?",
            settings = new { battery = "helio-powercell-10", stateOfChargePercent = 75 },
        });

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var status = body.RootElement.GetProperty("toolCalls")[0].GetProperty("result");
        Assert.Equal("Helio PowerCell 10", status.GetProperty("product").GetString());
        Assert.Equal(75, status.GetProperty("stateOfChargePercent").GetInt32());
        Assert.Contains("Helio PowerCell 10", _factory.ChatModel.Requests[0].Instructions);
    }

    [Fact]
    public async Task Invalid_settings_are_rejected_before_any_model_call()
    {
        var visitor = await _factory.CreateSignedInVisitor();

        var response = await visitor.PostAsJsonAsync("/api/chat", new { message = "hi", settings = new { battery = "tesla" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown_battery", body.RootElement.GetProperty("error").GetString());
        Assert.Empty(_factory.ChatModel.Requests);
    }
}
