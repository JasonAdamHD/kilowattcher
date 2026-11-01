using System.Text.Json;

namespace Kilowattcher.Api.Rag;

/// <summary>The on-disk format of api/Data/embeddings.json, written by tools/embed.</summary>
public sealed record EmbeddingFile(string Deployment, int Dimensions, IReadOnlyList<EmbeddedChunk> Chunks);

public sealed record EmbeddedChunk(string Id, string Title, string Heading, string Text, string ContentHash, float[] Vector);

public sealed record DocMatch(DocChunk Chunk, double Score);

/// <summary>
/// The document set in memory: every chunk with its normalized embedding. Search is a cosine
/// similarity scan; with a few dozen chunks that takes microseconds, so no database is needed.
/// </summary>
public sealed class DocIndex
{
    public const string FileName = "embeddings.json";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly (DocChunk Chunk, float[] Unit)[] _entries;

    public DocIndex(int dimensions, IEnumerable<(DocChunk Chunk, float[] Vector)> entries)
    {
        Dimensions = dimensions;
        _entries = entries.Select(e =>
        {
            if (e.Vector.Length != dimensions)
            {
                throw new InvalidDataException($"Chunk {e.Chunk.Id} has {e.Vector.Length} dimensions; expected {dimensions}.");
            }

            return (e.Chunk, Normalize(e.Vector));
        }).ToArray();
    }

    public int Dimensions { get; }

    public int Count => _entries.Length;

    public IEnumerable<DocChunk> Chunks => _entries.Select(e => e.Chunk);

    public static DocIndex Empty { get; } = new(0, []);

    public static DocIndex FromFile(EmbeddingFile file) =>
        new(file.Dimensions, file.Chunks.Select(c => (new DocChunk(c.Id, c.Title, c.Heading, c.Text), c.Vector)));

    public static EmbeddingFile ReadFile(string path) =>
        JsonSerializer.Deserialize<EmbeddingFile>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"{path} is empty.");

    public bool TryGet(string id, out DocChunk chunk)
    {
        foreach (var entry in _entries)
        {
            if (entry.Chunk.Id == id)
            {
                chunk = entry.Chunk;
                return true;
            }
        }

        chunk = null!;
        return false;
    }

    /// <summary>Top matches by cosine similarity, best first.</summary>
    public IReadOnlyList<DocMatch> Search(ReadOnlySpan<float> query, int top)
    {
        if (query.Length != Dimensions)
        {
            throw new ArgumentException($"Query has {query.Length} dimensions; the index has {Dimensions}.", nameof(query));
        }

        var unitQuery = Normalize(query.ToArray());
        return _entries
            .Select(e => new DocMatch(e.Chunk, Dot(unitQuery, e.Unit)))
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Chunk.Id, StringComparer.Ordinal)
            .Take(top)
            .ToList();
    }

    // With both vectors at length 1, cosine similarity is just the dot product.
    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static float[] Normalize(float[] vector)
    {
        double length = 0;
        foreach (var value in vector)
        {
            length += value * value;
        }

        length = Math.Sqrt(length);
        if (length == 0)
        {
            return vector;
        }

        return vector.Select(v => (float)(v / length)).ToArray();
    }
}

/// <summary>Loads api/Data/embeddings.json once, at first use. Missing or broken file → empty index, logged.</summary>
public sealed class DocIndexProvider
{
    private readonly Lazy<DocIndex> _index;

    public DocIndexProvider(ILogger<DocIndexProvider> logger) => _index = new Lazy<DocIndex>(() => Load(logger));

    /// <summary>For tests: serve a ready-made index instead of reading the file.</summary>
    internal DocIndexProvider(DocIndex index) => _index = new Lazy<DocIndex>(index);

    public DocIndex Index => _index.Value;

    private static DocIndex Load(ILogger logger)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", DocIndex.FileName);
        try
        {
            var index = DocIndex.FromFile(DocIndex.ReadFile(path));
            logger.LogInformation("Loaded {Count} document chunks ({Dimensions} dimensions).", index.Count, index.Dimensions);
            return index;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            logger.LogError("Document index unavailable ({Error}); search_docs will return no results.", ex.GetType().Name);
            return DocIndex.Empty;
        }
    }
}
