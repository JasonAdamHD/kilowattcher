using System.Text.Json;
using Kilowattcher.Api.Agent;

namespace Kilowattcher.Api.Tests.Agent;

public class AgentReplyParserTests
{
    [Fact]
    public void Parses_a_valid_reply()
    {
        var reply = AgentReplyParser.TryParse("""{"answer":" Charge tonight. ","citations":["doc-1","doc-1",""],"suggestedQuestion":null}""", out var problem);

        Assert.NotNull(reply);
        Assert.Null(problem);
        Assert.Equal("Charge tonight.", reply.Answer);
        Assert.Equal(["doc-1"], reply.Citations);
        Assert.Null(reply.SuggestedQuestion);
    }

    [Fact]
    public void Keeps_a_suggested_question()
    {
        var reply = AgentReplyParser.TryParse("""{"answer":"I can only help with home energy.","citations":[],"suggestedQuestion":"When is power cheapest tomorrow?"}""", out _);

        Assert.Equal("When is power cheapest tomorrow?", reply?.SuggestedQuestion);
    }

    [Theory]
    [InlineData(null, "empty reply")]
    [InlineData("", "empty reply")]
    [InlineData("Sure! Charge tonight.", "not valid JSON")]
    [InlineData("""["answer"]""", "not a JSON object")]
    [InlineData("""{"citations":[],"suggestedQuestion":null}""", "missing answer")]
    [InlineData("""{"answer":"  ","citations":[],"suggestedQuestion":null}""", "missing answer")]
    [InlineData("""{"answer":"ok","citations":"doc-1","suggestedQuestion":null}""", "invalid citations")]
    [InlineData("""{"answer":"ok","citations":[1],"suggestedQuestion":null}""", "invalid citations")]
    [InlineData("""{"answer":"ok","citations":[]}""", "missing suggestedQuestion")]
    [InlineData("""{"answer":"ok","citations":[],"suggestedQuestion":5}""", "invalid suggested question")]
    [InlineData("""{"answer":"ok","citations":[],"suggestedQuestion":null,"toolCalls":[]}""", "unexpected property")]
    public void Rejects_replies_that_break_the_schema(string? text, string expectedProblem)
    {
        Assert.Null(AgentReplyParser.TryParse(text, out var problem));
        Assert.Equal(expectedProblem, problem);
    }

    [Fact]
    public void Rejects_an_answer_too_long_to_send_back_as_history()
    {
        var text = JsonSerializer.Serialize(new { answer = new string('a', AgentReplyFormat.MaxAnswerLength + 1), citations = Array.Empty<string>(), suggestedQuestion = (string?)null });

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
