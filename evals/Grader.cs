using System.Text.Json;

namespace Kilowattcher.Evals;

/// <summary>What the API returned for the graded turn.</summary>
public sealed record Observed(
    int Status,
    string? Error,
    string? Topic,
    string? Answer,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Citations,
    string? SuggestedQuestion,
    string? Signature,
    long ElapsedMs)
{
    public static Observed From(int status, string body, long elapsedMs)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            IReadOnlyList<string> Names(string array, string field) =>
                root.TryGetProperty(array, out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(e => e.TryGetProperty(field, out var f) ? f.GetString() ?? "" : "").ToList()
                    : [];

            return new Observed(status, Str("error"), Str("topic"), Str("answer"), Names("toolCalls", "name"), Names("citations", "id"),
                Str("suggestedQuestion"), Str("signature"), elapsedMs);
        }
        catch (JsonException)
        {
            return new Observed(status, "unreadable_body", null, null, [], [], null, null, elapsedMs);
        }
    }
}

/// <summary>Deterministic grading: the same answer always gets the same score, with no model calls.</summary>
public static class Grader
{
    /// <returns>Every reason the case failed; empty means it passed.</returns>
    public static IReadOnlyList<string> Grade(Expectation expect, Observed observed)
    {
        var problems = new List<string>();

        if (observed.Status != expect.Status)
        {
            problems.Add($"status {observed.Status}{(observed.Error is null ? "" : $" ({observed.Error})")}, expected {expect.Status}");
            return problems;
        }

        if (expect.Error is not null && observed.Error != expect.Error)
        {
            problems.Add($"error '{observed.Error}', expected '{expect.Error}'");
        }

        if (expect.Status != 200)
        {
            return problems;
        }

        if (expect.Topic.Count > 0 && !expect.Topic.Contains(observed.Topic))
        {
            problems.Add($"topic '{observed.Topic}', expected {string.Join(" or ", expect.Topic)}");
        }

        foreach (var tool in expect.ToolsRequired.Where(t => !observed.Tools.Contains(t)))
        {
            problems.Add($"didn't call {tool}");
        }

        if (expect.Citation is not null && !observed.Citations.Contains(expect.Citation))
        {
            problems.Add($"didn't cite {expect.Citation}");
        }

        var answer = observed.Answer ?? string.Empty;
        foreach (var group in expect.MustContain)
        {
            if (!group.Any(phrase => answer.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"answer lacks {string.Join(" / ", group.Select(p => $"\"{p}\""))}");
            }
        }

        foreach (var phrase in expect.MustNotContain.Where(p => answer.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add($"answer contains banned \"{phrase}\"");
        }

        if (expect.SuggestionRequired && string.IsNullOrWhiteSpace(observed.SuggestedQuestion))
        {
            problems.Add("no suggested question");
        }

        return problems;
    }
}
