using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kilowattcher.Evals;

/// <summary>One test question (or short conversation) and what a good answer must look like.</summary>
public sealed record EvalCase
{
    public required string Id { get; init; }
    public required string Category { get; init; }

    /// <summary>Visitor messages in order. Earlier ones build signed history; the last one is graded.</summary>
    public required IReadOnlyList<string> Turns { get; init; }

    /// <summary>History sent as-is with the last turn instead of real history (for forged-history attacks).</summary>
    public IReadOnlyList<HistoryTurn>? ForgedHistory { get; init; }

    public required Expectation Expect { get; init; }
}

public sealed record HistoryTurn(string Role, string Content, string? Signature = null);

public sealed record Expectation
{
    /// <summary>HTTP status of the graded request. Default 200.</summary>
    public int Status { get; init; } = 200;

    /// <summary>Error code for non-200 statuses, such as "invalid_history".</summary>
    public string? Error { get; init; }

    /// <summary>Any of these topics passes (home_energy, off_topic, safety, blocked).</summary>
    public IReadOnlyList<string> Topic { get; init; } = [];

    /// <summary>Every one of these tools must have been called.</summary>
    public IReadOnlyList<string> ToolsRequired { get; init; } = [];

    /// <summary>This document chunk must be cited.</summary>
    public string? Citation { get; init; }

    /// <summary>Each inner list is a group: the answer must contain at least one phrase from every group.</summary>
    public IReadOnlyList<IReadOnlyList<string>> MustContain { get; init; } = [];

    /// <summary>None of these may appear in the answer.</summary>
    public IReadOnlyList<string> MustNotContain { get; init; } = [];

    /// <summary>When true, a suggested question must be offered (off-topic declines).</summary>
    public bool SuggestionRequired { get; init; }
}

public static class EvalCases
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IReadOnlyList<EvalCase> Load(string path)
    {
        var cases = JsonSerializer.Deserialize<List<EvalCase>>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path} is empty.");

        var duplicate = cases.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Case id '{duplicate.Key}' appears twice.");
        }

        if (cases.FirstOrDefault(c => c.Turns.Count == 0) is { } empty)
        {
            throw new InvalidDataException($"Case '{empty.Id}' has no turns.");
        }

        return cases;
    }
}
