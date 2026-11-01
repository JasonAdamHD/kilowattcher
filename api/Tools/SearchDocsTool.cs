using System.Text.Json;

namespace Kilowattcher.Api.Tools;

public sealed record DocChunk(string Id, string Title, string Heading, string Text);

public sealed record DocSearchResult(IReadOnlyList<DocChunk> Results, string? Note);

/// <summary>
/// Stage 3 stub: always returns no documents. Stage 4 replaces it with embedding search over
/// api/Data/docs. Citations are only accepted for chunk IDs this tool returned.
/// </summary>
public sealed class SearchDocsTool : ITool
{
    public string Name => "search_docs";

    public string Description =>
        "Search the home's document set (battery manual, rate plan explainer, FAQ) and return the most " +
        "relevant passages with their IDs. Use it for questions about what a setting or term means, " +
        "how the product works, or plan rules.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "What to look for, in a few words." }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """;

    public object Execute(JsonElement arguments) =>
        new DocSearchResult([], "Document search isn't available yet. Answer from tool data only, and say so if the documents would be needed.");
}
