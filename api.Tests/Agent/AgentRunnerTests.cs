using System.Text.Json;
using Kilowattcher.Api.Agent;
using Kilowattcher.Api.Rag;
using Kilowattcher.Api.Tests.TestSupport;
using Kilowattcher.Api.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kilowattcher.Api.Tests.Agent;

public class AgentRunnerTests
{
    private static readonly HomeClock Clock = new(new MutableTimeProvider(new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero)));

    private readonly FakeChatModel _model = new();

    /// <summary>The real tools, with any extra tool added or replacing the one with the same name.</summary>
    private AgentRunner CreateRunner(params ITool[] extraTools)
    {
        ITool[] defaults = [new BatteryStatusTool(), new RateScheduleTool(Clock), new SolarForecastTool(Clock), Search(TestIndex.ThreeAxes())];
        var tools = defaults.Where(d => extraTools.All(e => e.Name != d.Name)).Concat(extraTools);
        return new AgentRunner(_model, new ToolRegistry(tools), Clock, NullLogger<AgentRunner>.Instance);
    }

    private static SearchDocsTool Search(DocIndex index) =>
        new(TestIndex.Provider(index), new FakeEmbeddingClient(), NullLogger<SearchDocsTool>.Instance);

    private Task<AgentResult> Ask(string question, AgentRunner? runner = null) =>
        (runner ?? CreateRunner()).RunAsync([new UserItem(question)], CancellationToken.None);

    [Fact]
    public async Task Answers_without_tools_when_none_are_requested()
    {
        _model.Then(Steps.Final("Hi! Ask me about your battery."));

        var result = await Ask("hello");

        Assert.Equal("Hi! Ask me about your battery.", result.Answer);
        Assert.Empty(result.ToolCalls);
        Assert.False(result.RoundLimitReached);
        Assert.Single(_model.Requests);
    }

    [Fact]
    public async Task Runs_requested_tools_and_feeds_results_back()
    {
        _model
            .Then(Steps.Calls(
                ("get_battery_status", "{}", "c1"),
                ("get_rate_schedule", """{"date":"2026-11-01"}""", "c2"),
                ("get_solar_forecast", """{"date":"2026-11-02"}""", "c3")))
            .Then(Steps.Final("Yes, charge between midnight and 6 AM."));

        var result = await Ask("Should I charge my battery from the grid tonight?");

        Assert.Equal(["get_battery_status", "get_rate_schedule", "get_solar_forecast"], result.ToolCalls.Select(t => t.Name));
        Assert.Equal(42, result.ToolCalls[0].Result.GetProperty("stateOfChargePercent").GetInt32());
        Assert.Equal("2026-11-01", result.ToolCalls[1].Arguments.GetProperty("date").GetString());
        Assert.Equal("cloudy", result.ToolCalls[2].Result.GetProperty("conditions").GetString());

        // The second model call sees each call followed by its output, matched by call ID.
        var followUp = _model.Requests[1].Items;
        Assert.IsType<UserItem>(followUp[0]);
        var outputs = followUp.OfType<ToolOutputItem>().ToList();
        Assert.Equal(["c1", "c2", "c3"], outputs.Select(o => o.CallId));
        Assert.Contains("\"conditions\":\"cloudy\"", outputs[2].OutputJson);
    }

    [Fact]
    public async Task Every_model_call_gets_the_system_prompt_tools_and_todays_date()
    {
        await Ask("hello");

        var request = Assert.Single(_model.Requests);
        Assert.Contains("Today is 2026-11-01; tomorrow is 2026-11-02.", request.Instructions);
        Assert.Contains("licensed electrician", request.Instructions);
        Assert.Equal(["get_battery_status", "get_rate_schedule", "get_solar_forecast", "search_docs"], request.Tools.Select(t => t.Name).Order());
        Assert.True(request.AllowTools);
    }

    [Fact]
    public async Task Unknown_tools_and_bad_arguments_become_errors_the_model_can_read()
    {
        _model
            .Then(Steps.Calls(("delete_everything", "{}", "c1"), ("get_rate_schedule", "{not json", "c2")))
            .Then(Steps.Final("I couldn't check the rates."));

        var result = await Ask("rates?");

        Assert.Equal("Unknown tool 'delete_everything'.", result.ToolCalls[0].Result.GetProperty("error").GetString());
        Assert.Equal("Arguments were not valid JSON.", result.ToolCalls[1].Result.GetProperty("error").GetString());
        Assert.Equal("I couldn't check the rates.", result.Answer);
    }

    [Fact]
    public async Task A_tool_that_throws_does_not_crash_the_request()
    {
        _model.Then(Steps.Call("explode", "{}")).Then(Steps.Final("Done."));

        var result = await Ask("hi", CreateRunner(new ThrowingTool()));

        Assert.Equal("The tool failed. Answer without it.", result.ToolCalls[0].Result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Stops_after_five_tool_rounds_and_answers_with_tools_off()
    {
        for (var i = 0; i < 6; i++)
        {
            _model.Then(Steps.Call("get_battery_status", "{}", $"round{i}"));
        }

        _model.Then(Steps.Final("Your battery is at 42%."));

        var result = await Ask("tell me everything");

        Assert.Equal(AgentRunner.MaxToolRounds, result.ToolCalls.Count); // the 6th request was never run
        Assert.True(result.RoundLimitReached);
        Assert.Equal($"Your battery is at 42%.\n\n{AgentRunner.RoundLimitNote}", result.Answer);

        Assert.Equal(7, _model.Requests.Count);
        Assert.All(_model.Requests.Take(6), r => Assert.True(r.AllowTools));
        Assert.False(_model.Requests[6].AllowTools);
        Assert.DoesNotContain(_model.Requests[6].Items, i => i is ToolCallItem { CallId: "round5" });
    }

    [Fact]
    public async Task Using_exactly_five_rounds_is_not_reported_as_hitting_the_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            _model.Then(Steps.Call("get_battery_status"));
        }

        _model.Then(Steps.Final("Done."));

        var result = await Ask("check five times");

        Assert.False(result.RoundLimitReached);
        Assert.Equal("Done.", result.Answer);
    }

    [Fact]
    public async Task Fails_if_the_model_insists_on_tools_with_tools_off()
    {
        for (var i = 0; i < 7; i++)
        {
            _model.Then(Steps.Call("get_battery_status"));
        }

        var ex = await Assert.ThrowsAsync<ChatModelException>(() => Ask("loop forever"));

        Assert.Equal(ChatModelFailure.InvalidReply, ex.Failure);
    }

    [Fact]
    public async Task Retries_once_when_the_reply_is_not_valid_json()
    {
        _model.Then(Steps.Text("Sure, charge tonight!")).Then(Steps.Final("Charge tonight."));

        var result = await Ask("charge?");

        Assert.Equal("Charge tonight.", result.Answer);
        Assert.Equal(2, _model.Requests.Count);
        var retry = _model.Requests[1];
        Assert.False(retry.AllowTools);
        Assert.IsType<AssistantItem>(retry.Items[^2]);
        Assert.IsType<DeveloperNoteItem>(retry.Items[^1]);
    }

    [Fact]
    public async Task Gives_up_after_two_invalid_replies()
    {
        _model.Then(Steps.Text("nope")).Then(Steps.Text("still nope"));

        var ex = await Assert.ThrowsAsync<ChatModelException>(() => Ask("charge?"));

        Assert.Equal(ChatModelFailure.InvalidReply, ex.Failure);
        Assert.Equal(2, _model.Requests.Count);
    }

    [Fact]
    public async Task Citations_not_returned_by_search_docs_are_removed()
    {
        _model.Then(Steps.Final("Reserve is the charge kept for outages.", citations: ["battery-manual#reserve"]));

        var result = await Ask("What does reserve percentage mean?");

        Assert.Empty(result.Citations);
    }

    [Fact]
    public async Task Citations_returned_by_search_docs_are_kept()
    {
        _model
            .Then(Steps.Call("search_docs", """{"query":"reserve"}"""))
            .Then(Steps.Final("Reserve is the charge kept for outages.", citations: ["manual#reserve", "made-up#id"]));

        var result = await Ask("What does reserve percentage mean?", CreateRunner(new FixedSearchTool("manual#reserve")));

        var citation = Assert.Single(result.Citations);
        Assert.Equal("manual#reserve", citation.Id);
        Assert.Equal("Battery manual", citation.Title);
        Assert.Equal("Reserve keeps charge for outages.", citation.Text);
    }

    [Fact]
    public async Task Real_search_results_become_citable_passages()
    {
        // FakeEmbeddingClient points every query along the first axis: the reserve-percentage chunk.
        _model
            .Then(Steps.Call("search_docs", """{"query":"reserve percentage"}"""))
            .Then(Steps.Final("It's the charge kept for outages.", citations: ["battery-manual#reserve-percentage"]));

        var result = await Ask("What does reserve percentage mean?");

        Assert.Equal("battery-manual#reserve-percentage", Assert.Single(result.Citations).Id);
        var hits = result.ToolCalls[0].Result.GetProperty("results");
        Assert.Equal(3, hits.GetArrayLength());
        Assert.Equal("battery-manual#reserve-percentage", hits[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Model_failures_propagate_unchanged()
    {
        _model.Then(_ => Steps.Fail(ChatModelFailure.Busy));

        var ex = await Assert.ThrowsAsync<ChatModelException>(() => Ask("hi"));

        Assert.Equal(ChatModelFailure.Busy, ex.Failure);
    }

    private sealed class ThrowingTool : ITool
    {
        public string Name => "explode";
        public string Description => "Always throws.";
        public string ParametersSchema => ToolArguments.NoArgumentsSchema;
        public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    /// <summary>Overrides the stub so citation keeping can be tested before Stage 4.</summary>
    private sealed class FixedSearchTool(string id) : ITool
    {
        public string Name => "search_docs";
        public string Description => "Test search.";
        public string ParametersSchema => ToolArguments.NoArgumentsSchema;
        public Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new DocSearchResult([new DocSearchHit(id, "Battery manual", "Reserve", "Reserve keeps charge for outages.", 0.9)], null));
    }
}
