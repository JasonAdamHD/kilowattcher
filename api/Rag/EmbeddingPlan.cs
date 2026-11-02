namespace Kilowattcher.Api.Rag;

/// <summary>
/// What an embed run has to do, worked out by comparing the docs today with embeddings.json:
/// <list type="bullet">
/// <item><b>Unchanged</b>: same ID, same content hash → keep the stored vector (no Azure call).</item>
/// <item><b>Changed</b>: same ID, different hash (text, title, heading, or product tag edited) → re-embed.</item>
/// <item><b>Added</b>: an ID that isn't stored yet (new doc or new heading) → embed.</item>
/// <item><b>Removed</b>: a stored ID no doc has any more (deleted doc, renamed heading) → drop.</item>
/// </list>
/// Renaming a heading changes its ID, so it shows up as one removed plus one added.
/// </summary>
public sealed record EmbeddingPlan(
    IReadOnlyList<DocChunk> Unchanged,
    IReadOnlyList<DocChunk> Changed,
    IReadOnlyList<DocChunk> Added,
    IReadOnlyList<string> Removed,
    bool FullRebuild)
{
    public IReadOnlyList<DocChunk> ToEmbed => [.. Changed, .. Added];

    public bool IsUpToDate => Changed.Count == 0 && Added.Count == 0 && Removed.Count == 0;

    /// <param name="stored">The current embeddings.json, or null when there isn't one yet.</param>
    /// <param name="full">Re-embed everything, for example after changing the embedding model or dimensions.</param>
    public static EmbeddingPlan Create(IReadOnlyList<DocChunk> current, EmbeddingFile? stored, int dimensions, bool full = false)
    {
        // A different vector size means the old vectors can't be mixed with new ones.
        if (full || stored is null || stored.Dimensions != dimensions)
        {
            return new EmbeddingPlan([], [], current, stored?.Chunks.Select(c => c.Id).Where(id => current.All(c => c.Id != id)).ToList() ?? [], FullRebuild: true);
        }

        var storedById = stored.Chunks.ToDictionary(c => c.Id);
        var unchanged = current.Where(c => storedById.TryGetValue(c.Id, out var s) && s.ContentHash == c.ContentHash).ToList();
        var changed = current.Where(c => storedById.TryGetValue(c.Id, out var s) && s.ContentHash != c.ContentHash).ToList();
        var added = current.Where(c => !storedById.ContainsKey(c.Id)).ToList();
        var removed = stored.Chunks.Select(c => c.Id).Where(id => current.All(c => c.Id != id)).ToList();

        return new EmbeddingPlan(unchanged, changed, added, removed, FullRebuild: false);
    }

    /// <summary>The new file: docs in their current order, stored vectors where unchanged, fresh ones elsewhere.</summary>
    public EmbeddingFile Apply(EmbeddingFile? stored, IReadOnlyList<DocChunk> current, IReadOnlyList<float[]> newVectors, string deployment, int dimensions)
    {
        if (newVectors.Count != ToEmbed.Count)
        {
            throw new ArgumentException($"Expected {ToEmbed.Count} new vectors, got {newVectors.Count}.", nameof(newVectors));
        }

        var fresh = ToEmbed.Select((c, i) => (c.Id, Vector: newVectors[i])).ToDictionary(x => x.Id, x => x.Vector);
        var kept = stored?.Chunks.ToDictionary(c => c.Id, c => c.Vector) ?? [];

        return new EmbeddingFile(
            deployment,
            dimensions,
            current.Select(c => new EmbeddedChunk(
                c.Id, c.Title, c.Heading, c.Text, c.ContentHash,
                fresh.TryGetValue(c.Id, out var v) ? v : kept[c.Id],
                c.Product)).ToList());
    }
}
