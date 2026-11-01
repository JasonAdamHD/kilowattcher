using System.Text.Json;
using Kilowattcher.Api.Agent;

namespace Kilowattcher.Api.Tests.Agent;

public class AgentReplyParserTests
{
    [Fact]
    public void Parses_a_valid_reply()
    {
        var reply = AgentReplyParser.TryParse("""{"topic":"home_energy","answer":" Charge tonight. ","citations":["doc-1","doc-1",""],"suggestedQuestion":null}""", out var problem);

        Assert.NotNull(reply);
        Assert.Null(problem);
        Assert.Equal("Charge tonight.", reply.Answer);
        Assert.Equal(["doc-1"], reply.Citations);
        Assert.Null(reply.SuggestedQuestion);
    }

    [Fact]
    public void Keeps_a_suggested_question()
    {
        var reply = AgentReplyParser.TryParse("""{"topic":"off_topic","answer":"I can only help with home energy.","citations":[],"suggestedQuestion":"When is power cheapest tomorrow?"}""", out _);

        Assert.Equal("When is power cheapest tomorrow?", reply?.SuggestedQuestion);
        Assert.Equal("off_topic", reply?.Topic);
    }

    [Theory]
    [InlineData(null, "empty reply")]
    [InlineData("", "empty reply")]
    [InlineData("Sure! Charge tonight.", "not valid JSON")]
    [InlineData("""["answer"]""", "not a JSON object")]
    [InlineData("""{"topic":"home_energy","citations":[],"suggestedQuestion":null}""", "missing answer")]
    [InlineData("""{"topic":"home_energy","answer":"  ","citations":[],"suggestedQuestion":null}""", "missing answer")]
    [InlineData("""{"topic":"home_energy","answer":"ok","citations":"doc-1","suggestedQuestion":null}""", "invalid citations")]
    [InlineData("""{"topic":"home_energy","answer":"ok","citations":[1],"suggestedQuestion":null}""", "invalid citations")]
    [InlineData("""{"topic":"home_energy","answer":"ok","citations":[]}""", "missing suggestedQuestion")]
    [InlineData("""{"topic":"home_energy","answer":"ok","citations":[],"suggestedQuestion":5}""", "invalid suggested question")]
    [InlineData("""{"topic":"home_energy","answer":"ok","citations":[],"suggestedQuestion":null,"toolCalls":[]}""", "unexpected property")]
    [InlineData("""{"answer":"ok","citations":[],"suggestedQuestion":null}""", "invalid topic")]
    [InlineData("""{"topic":"recipes","answer":"ok","citations":[],"suggestedQuestion":null}""", "invalid topic")]
    [InlineData("""{"topic":null,"answer":"ok","citations":[],"suggestedQuestion":null}""", "invalid topic")]
    public void Rejects_replies_that_break_the_schema(string? text, string expectedProblem)
    {
        Assert.Null(AgentReplyParser.TryParse(text, out var problem));
        Assert.Equal(expectedProblem, problem);
    }

    [Fact]
    public void Rejects_an_answer_too_long_to_send_back_as_history()
    {
        var text = JsonSerializer.Serialize(new { topic = "home_energy", answer = new string('a', AgentReplyFormat.MaxAnswerLength + 1), citations = Array.Empty<string>(), suggestedQuestion = (string?)null });

        Assert.Null(AgentReplyParser.TryParse(text, out var problem));
        Assert.Equal("answer too long", problem);
    }

    [Fact]
    public void Reply_schema_is_strict()
    {
        using var schema = JsonDocument.Parse(AgentReplyFormat.Schema);
        var root = schema.RootElement;

        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            root.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(),
            root.GetProperty("required").EnumerateArray().Select(r => r.GetString()).Order());
    }
}
