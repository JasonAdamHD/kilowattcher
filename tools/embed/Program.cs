// Builds api/Data/embeddings.json from api/Data/docs/*.md.
//
//   dotnet run --project tools/embed            embed every chunk with the embedding deployment
//   dotnet run --project tools/embed -- --check verify embeddings.json matches the docs (no Azure call)
//
// Reads Azure settings from the API's user-secrets (or environment variables). Prints counts only,
// never keys, endpoints, or document text.

using System.Text.Json;
using Kilowattcher.Api;
using Kilowattcher.Api.Rag;
using Microsoft.Extensions.Configuration;

var root = FindRepoRoot();
var docsDir = Path.Combine(root, "api", "Data", "docs");
var outputPath = Path.Combine(root, "api", "Data", DocIndex.FileName);

var chunks = MarkdownChunker.ChunkDirectory(docsDir);
var docCount = chunks.Select(c => c.Id.Split('#')[0]).Distinct().Count();
Console.WriteLine($"Found {chunks.Count} chunks in {docCount} docs.");

if (args.Contains("--check"))
{
    return Check(chunks, outputPath);
}

var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Marker>(optional: true)
    .AddEnvironmentVariables()
    .Build();

var settings = AzureOpenAISettings.TryRead(configuration, AzureOpenAISettings.EmbeddingDeploymentKey);
if (settings is null)
{
    Console.Error.WriteLine("Azure settings missing: set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY and AZURE_OPENAI_EMBEDDING_DEPLOYMENT in the API's user-secrets.");
    return 1;
}

IEmbeddingClient client = new AzureEmbeddingClient(settings);
float[][] vectors;
try
{
    vectors = (await client.EmbedAsync(chunks.Select(c => c.EmbeddingInput).ToList(), CancellationToken.None)).ToArray();
}
catch (EmbeddingUnavailableException ex)
{
    Console.Error.WriteLine($"Embedding failed: {ex.Message}");
    return 1;
}

var file = new EmbeddingFile(
    settings.Deployment,
    EmbeddingSettings.Dimensions,
    chunks.Select((c, i) => new EmbeddedChunk(c.Id, c.Title, c.Heading, c.Text, c.ContentHash, vectors[i])).ToList());

await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(file, DocIndex.Json) + "\n");
Console.WriteLine($"Wrote {file.Chunks.Count} vectors ({file.Dimensions} dimensions) to api/Data/{DocIndex.FileName}.");
return 0;

static int Check(IReadOnlyList<DocChunk> chunks, string path)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"api/Data/{DocIndex.FileName} is missing. Run: dotnet run --project tools/embed");
        return 1;
    }

    var stored = DocIndex.ReadFile(path).Chunks.ToDictionary(c => c.Id, c => c.ContentHash);
    var current = chunks.ToDictionary(c => c.Id, c => c.ContentHash);

    var added = current.Keys.Except(stored.Keys).ToList();
    var removed = stored.Keys.Except(current.Keys).ToList();
    var changed = current.Keys.Intersect(stored.Keys).Where(id => current[id] != stored[id]).ToList();

    if (added.Count + removed.Count + changed.Count == 0)
    {
        Console.WriteLine($"api/Data/{DocIndex.FileName} is up to date.");
        return 0;
    }

    foreach (var (label, ids) in new[] { ("new", added), ("removed", removed), ("changed", changed) })
    {
        foreach (var id in ids)
        {
            Console.Error.WriteLine($"  {label}: {id}");
        }
    }

    Console.Error.WriteLine("Docs changed since the last embed. Run: dotnet run --project tools/embed");
    return 1;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Kilowattcher.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new DirectoryNotFoundException("Run this from inside the kilowattcher repo.");
}

/// <summary>Anchors AddUserSecrets to this assembly's UserSecretsId (shared with the API).</summary>
internal sealed class Marker;
