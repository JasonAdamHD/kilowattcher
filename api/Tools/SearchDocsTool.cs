using System.Text.Json;
using Kilowattcher.Api.Rag;

namespace Kilowattcher.Api.Tools;

public sealed record DocSearchHit(string Id, string Title, string Heading, string Text, double Score);

public sealed record DocSearchResult(IReadOnlyList<DocSearchHit> Results, string? Note);

/// <summary>
/// The RAG step: embeds the query with the embedding deployment and returns the top 3 chunks of
/// api/Data/docs by cosine similarity. Chunk vectors are precomputed in embeddings.json, so the
/// only Azure call is for the query itself. Citations are only accepted for IDs returned here.
/// </summary>
public sealed class SearchDocsTool(DocIndexProvider indexProvider, IEmbeddingClient embeddings, ILogger<SearchDocsTool> logger) : ITool
{
    public const int TopResults = 3;
    public const int MaxQueryLength = 500;

    public string Name => "search_docs";

    public string Description =>
        "Search the home's documents (VoltNest 13 battery manual, Sunrise Saver rate plan, outage backup guide, " +
        "solar basics, FAQ) and return the 3 most relevant passages with their IDs. Use it for what a setting " +
        "or term means, how the battery or solar works, outage advice, and plan rules.";

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

    public async Task<object> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var query = ToolArguments.GetString(arguments, "query")?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length > MaxQueryLength)
        {
            return new ToolError($"query must be 1 to {MaxQueryLength} characters.");
        }

        var index = indexProvider.Index;
        if (index.Count == 0)
        {
            return new DocSearchResult([], "The document set isn't available right now. Answer from tool data only and say the documents couldn't be checked.");
        }

        float[] vector;
        try
        {
            vector = (await embeddings.EmbedAsync([query], cancellationToken))[0];
        }
        catch (EmbeddingUnavailableException ex)
        {
            // Status only: never the query, which is the visitor's own words.
            logger.LogWarning("Query embedding failed (HTTP {Status}).", ex.Status);
            return new DocSearchResult([], "Document search is temporarily unavailable. Answer from tool data only and say the documents couldn't be checked.");
        }

        var hits = index.Search(vector, TopResults)
            .Select(m => new DocSearchHit(m.Chunk.Id, m.Chunk.Title, m.Chunk.Heading, m.Chunk.Text, Math.Round(m.Score, 3)))
            .ToList();

        return new DocSearchResult(hits, null);
    }
}
