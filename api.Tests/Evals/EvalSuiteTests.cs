extern alias evals;

using System.Reflection;
using evals::Kilowattcher.Evals;
using Kilowattcher.Api.Rag;

namespace Kilowattcher.Api.Tests.Evals;

/// <summary>Checks the eval suite itself, without calling the API: the cases are well formed and the grader is fair.</summary>
public class EvalSuiteTests
{
    private static readonly string Root = typeof(EvalSuiteTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "RepoRoot").Value!;

    private static readonly IReadOnlyList<EvalCase> Cases = EvalCases.Load(Path.Combine(Root, "evals", "cases.json"));

    [Fact]
    public void Suite_matches_the_handoff_categories_plus_guardrails()
    {
        var counts = Cases.GroupBy(c => c.Category).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(27, Cases.Count);
        Assert.Equal(2, counts["Second battery"]);
        Assert.Equal(7, counts["Tool selection"]);
        Assert.Equal(6, counts["Grounded answers"]);
        Assert.Equal(4, counts["Off-topic refusal"]);
        Assert.Equal(2, counts["Safety referral"]);
        Assert.Equal(2, counts["Prompt injection"]);
        Assert.Equal(4, counts["Guardrails"]);
    }

    [Fact]
    public void Every_expected_citation_exists_in_the_document_set()
    {
        var ids = MarkdownChunker.ChunkDirectory(Path.Combine(Root, "api", "Data", "docs")).Select(c => c.Id).ToHashSet();

        Assert.All(Cases.Where(c => c.Expect.Citation is not null), c =>
            Assert.True(ids.Contains(c.Expect.Citation!), $"{c.Id} expects unknown citation {c.Expect.Citation}"));
    }

    [Fact]
    public void Every_expected_tool_and_topic_is_real()
    {
        string[] tools = ["get_battery_status", "estimate_backup_time", "get_rate_schedule", "find_cheapest_charging_window", "get_solar_forecast", "search_docs"];
        string[] topics = ["home_energy", "off_topic", "safety", "blocked"];

        Assert.All(Cases, c =>
        {
            Assert.All(c.Expect.ToolsRequired, t => Assert.Contains(t, tools));
            Assert.All(c.Expect.Topic, t => Assert.Contains(t, topics));
        });
    }

    private static Observed Reply(
        string? topic = "home_energy", string answer = "Charge from midnight to 6 AM at $0.11 per kWh.",
        string[]? tools = null, string[]? citations = null, string? suggestion = null, int status = 200, string? error = null) =>
        new(status, error, topic, answer, tools ?? [], citations ?? [], suggestion, "sig", 1000);

    [Fact]
    public void A_matching_reply_passes()
    {
        var expect = new Expectation
        {
            Topic = ["home_energy"],
            ToolsRequired = ["get_rate_schedule"],
            Citation = "faq#x",
            MustContain = [["midnight", "12 AM"], ["0.11"]],
            MustNotContain = ["PWNED"],
        };

        Assert.Empty(Grader.Grade(expect, Reply(tools: ["get_rate_schedule", "get_battery_status"], citations: ["faq#x"])));
    }

    [Fact]
    public void Every_kind_of_miss_is_reported()
    {
        var expect = new Expectation
        {
            Topic = ["home_energy"],
            ToolsRequired = ["get_solar_forecast"],
            Citation = "faq#x",
            MustContain = [["4.2", "4.1"]],
            MustNotContain = ["0.11"],
            SuggestionRequired = true,
        };

        var problems = Grader.Grade(expect, Reply(topic: "off_topic"));

        Assert.Equal(6, problems.Count);
        Assert.Contains(problems, p => p.Contains("topic"));
        Assert.Contains(problems, p => p.Contains("get_solar_forecast"));
        Assert.Contains(problems, p => p.Contains("faq#x"));
        Assert.Contains(problems, p => p.Contains("\"4.2\" / \"4.1\""));
        Assert.Contains(problems, p => p.Contains("banned \"0.11\""));
        Assert.Contains(problems, p => p.Contains("suggested question"));
    }

    [Fact]
    public void Phrase_checks_ignore_case()
    {
        var expect = new Expectation { MustContain = [["LICENSED ELECTRICIAN"]], MustNotContain = ["pwned"] };

        Assert.Empty(Grader.Grade(expect, Reply(answer: "Please call a licensed electrician.")));
        Assert.NotEmpty(Grader.Grade(expect, Reply(answer: "Licensed electrician. PWNED")));
    }

    [Fact]
    public void Expected_errors_are_graded_on_status_and_code()
    {
        var expect = new Expectation { Status = 400, Error = "invalid_history" };

        Assert.Empty(Grader.Grade(expect, Reply(status: 400, error: "invalid_history", topic: null)));
        Assert.NotEmpty(Grader.Grade(expect, Reply(status: 200)));
        Assert.NotEmpty(Grader.Grade(expect, Reply(status: 400, error: "empty_message", topic: null)));
    }

    [Fact]
    public void Reads_the_api_reply_shape()
    {
        const string body = """
            {"topic":"home_energy","answer":"42%","toolCalls":[{"name":"get_battery_status","arguments":{},"result":{}}],
             "citations":[{"id":"faq#x","title":"FAQ","heading":"X","text":"..."}],"suggestedQuestion":null,"signature":"abc"}
            """;

        var observed = Observed.From(200, body, 1234);

        Assert.Equal("home_energy", observed.Topic);
        Assert.Equal(["get_battery_status"], observed.Tools);
        Assert.Equal(["faq#x"], observed.Citations);
        Assert.Equal("abc", observed.Signature);
        Assert.Equal("unreadable_body", Observed.From(502, "<html>bad gateway</html>", 1).Error);
    }
}
