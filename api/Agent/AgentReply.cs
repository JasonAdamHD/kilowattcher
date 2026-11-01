using System.Text.Json;

namespace Kilowattcher.Api.Agent;

/// <summary>The JSON the model must return as its final answer.</summary>
public sealed record AgentReply(string Topic, string Answer, IReadOnlyList<string> Citations, string? SuggestedQuestion);

/// <summary>What the model says the reply is about. The server acts on it; see AgentRunner.</summary>
public static class ReplyTopic
{
    public const string HomeEnergy = "home_energy";
    public const string OffTopic = "off_topic";
    public const string Safety = "safety";

    public static readonly string[] All = [HomeEnergy, OffTopic, Safety];
}

public static class AgentReplyFormat
{
    public const string SchemaName = "kilowattcher_reply";

    // Strict structured output: every property required, no extras. Azure enforces it;
    // AgentReplyParser checks it again, because the API must never trust model output blindly.
    public const string Schema = """
        {
          "type": "object",
          "properties": {
            "topic": {
              "type": "string",
              "enum": ["home_energy", "off_topic", "safety"],
              "description": "home_energy for normal answers, off_topic for declines, safety for electrical-work referrals."
            },
            "answer": {
              "type": "string",
              "description": "The reply shown to the visitor: short, plain, friendly."
            },
            "citations": {
              "type": "array",
              "items": { "type": "string" },
              "description": "IDs of search_docs passages the answer relies on. Empty when none were used."
            },
            "suggestedQuestion": {
              "type": ["string", "null"],
              "description": "When declining an off-topic request, one short home-energy question the visitor could ask instead. Otherwise null."
            }
          },
          "required": ["topic", "answer", "citations", "suggestedQuestion"],
          "additionalProperties": false
        }
        """;

    // Answers are sent back as history later, which caps assistant messages at 4,000 characters;
    // this leaves room for the round-limit note.
    public const int MaxAnswerLength = 3_800;
    public const int MaxSuggestedQuestionLength = 200;
    public const int MaxCitations = 10;
}

public static class AgentReplyParser
{
    /// <summary>Returns the reply, or null with a short reason (for logs, never shown to visitors).</summary>
    public static AgentReply? TryParse(string? text, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "empty reply";
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            problem = "not valid JSON";
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = "not a JSON object";
                return null;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is not ("topic" or "answer" or "citations" or "suggestedQuestion"))
                {
                    problem = "unexpected property";
                    return null;
                }
            }

            if (!root.TryGetProperty("topic", out var topic) || topic.ValueKind != JsonValueKind.String
                || !ReplyTopic.All.Contains(topic.GetString()))
            {
                problem = "invalid topic";
                return null;
            }

            if (!root.TryGetProperty("answer", out var answer) || answer.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(answer.GetString()))
            {
                problem = "missing answer";
                return null;
            }

            var answerText = answer.GetString()!.Trim();
            if (answerText.Length > AgentReplyFormat.MaxAnswerLength)
            {
                problem = "answer too long";
                return null;
            }

            if (!root.TryGetProperty("citations", out var citations) || citations.ValueKind != JsonValueKind.Array
                || citations.GetArrayLength() > AgentReplyFormat.MaxCitations
                || citations.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String))
            {
                problem = "invalid citations";
                return null;
            }

            string? suggested = null;
            if (root.TryGetProperty("suggestedQuestion", out var suggestion))
            {
                if (suggestion.ValueKind == JsonValueKind.String)
                {
                    suggested = suggestion.GetString()!.Trim();
                    if (suggested.Length > AgentReplyFormat.MaxSuggestedQuestionLength)
                    {
                        problem = "suggested question too long";
                        return null;
                    }

                    suggested = suggested.Length == 0 ? null : suggested;
                }
                else if (suggestion.ValueKind != JsonValueKind.Null)
                {
                    problem = "invalid suggested question";
                    return null;
                }
            }
            else
            {
                problem = "missing suggestedQuestion";
                return null;
            }

            var ids = citations.EnumerateArray().Select(c => c.GetString()!.Trim()).Where(c => c.Length > 0).Distinct().ToList();
            return new AgentReply(topic.GetString()!, answerText, ids, suggested);
        }
    }
}
