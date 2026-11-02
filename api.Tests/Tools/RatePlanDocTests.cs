using System.Globalization;
using System.Reflection;
using Kilowattcher.Api.Rag;
using Kilowattcher.Api.Tools;

namespace Kilowattcher.Api.Tests.Tools;

/// <summary>
/// The rate plan lives in two places: RateScheduleTool.Periods (what the tools compute with) and
/// time-of-use-plan.md (what the agent cites). If either is edited alone, the agent would contradict
/// itself, so these tests fail the build until both say the same thing.
/// </summary>
public class RatePlanDocTests
{
    private static readonly string RateSection = Section("time-of-use-plan#rate-periods-and-prices");

    public static TheoryData<string, string, string, decimal> Periods()
    {
        var data = new TheoryData<string, string, string, decimal>();
        foreach (var p in RateScheduleTool.Periods)
        {
            data.Add(p.Name, p.Start, p.End, p.PricePerKwh);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Periods))]
    public void Every_period_in_code_appears_in_the_doc_with_the_same_hours_and_price(string name, string start, string end, decimal price)
    {
        var expected = $"from {Spoken(start)} to {Spoken(end)} at ${price.ToString("0.00", CultureInfo.InvariantCulture)} per kWh";

        Assert.True(RateSection.Contains(expected, StringComparison.Ordinal),
            $"time-of-use-plan.md \"Rate periods and prices\" should say \"{name} … {expected}\" to match RateScheduleTool.Periods.");
        Assert.Contains(name, RateSection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_doc_lists_no_prices_the_code_does_not_have()
    {
        var codePrices = RateScheduleTool.Periods.Select(p => p.PricePerKwh.ToString("0.00", CultureInfo.InvariantCulture)).ToHashSet();
        var docPrices = System.Text.RegularExpressions.Regex.Matches(RateSection, @"\$(\d+\.\d{2}) per kWh").Select(m => m.Groups[1].Value);

        Assert.All(docPrices, price => Assert.Contains(price, codePrices));
    }

    [Fact]
    public void Cheapest_and_most_expensive_prices_match_the_faq()
    {
        var faq = Section("faq#when-is-electricity-cheapest");
        var cheapest = RateScheduleTool.Periods.MinBy(p => p.PricePerKwh)!;
        var priciest = RateScheduleTool.Periods.MaxBy(p => p.PricePerKwh)!;

        Assert.Contains($"{Spoken(cheapest.Start)} and {Spoken(cheapest.End)}, at ${cheapest.PricePerKwh:0.00} per kWh", faq);
        Assert.Contains($"{Spoken(priciest.Start)} to {Spoken(priciest.End)}, at ${priciest.PricePerKwh:0.00} per kWh", faq);
    }

    /// <summary>"00:00" → "12 AM", "06:00" → "6 AM", "16:00" → "4 PM", "24:00" → "12 AM", as the docs write times.</summary>
    internal static string Spoken(string clock)
    {
        var hour = int.Parse(clock[..2], CultureInfo.InvariantCulture) % 24;
        var twelve = hour % 12 == 0 ? 12 : hour % 12;
        return $"{twelve} {(hour < 12 ? "AM" : "PM")}";
    }

    [Theory]
    [InlineData("00:00", "12 AM")]
    [InlineData("06:00", "6 AM")]
    [InlineData("12:00", "12 PM")]
    [InlineData("16:00", "4 PM")]
    [InlineData("21:00", "9 PM")]
    [InlineData("24:00", "12 AM")]
    public void Clock_times_are_spoken_the_way_the_docs_write_them(string clock, string spoken)
    {
        Assert.Equal(spoken, Spoken(clock));
    }

    private static string Section(string id)
    {
        var root = typeof(RatePlanDocTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!;
        return MarkdownChunker.ChunkDirectory(Path.Combine(root, "api", "Data", "docs")).Single(c => c.Id == id).Text;
    }
}
