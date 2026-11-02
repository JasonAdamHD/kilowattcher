// Keeps api/Data/embeddings.json in step with api/Data/docs/*.md, incrementally.
//
//   dotnet run --project tools/embed                embed only new and changed chunks, drop removed ones
//   dotnet run --project tools/embed -- --plan      show what would change, without calling Azure
//   dotnet run --project tools/embed -- --check     fail if anything would change (CI), without calling Azure
//   dotnet run --project tools/embed -- --full      re-embed every chunk (e.g. after changing model or dimensions)
//
// Reads Azure settings from the API's user-secrets (or environment variables). Prints chunk IDs and
// counts only, never keys, endpoints, or document text.

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

var stored = File.Exists(outputPath) ? DocIndex.ReadFile(outputPath) : null;
var plan = EmbeddingPlan.Create(chunks, stored, EmbeddingSettings.Dimensions, full: args.Contains("--full"));
Print(plan, stored is null);

if (args.Contains("--check"))
{
    if (plan.IsUpToDate)
    {
        return 0;
    }

    Console.Error.WriteLine("Docs changed since the last embed. Run: dotnet run --project tools/embed");
    return 1;
}

if (args.Contains("--plan"))
{
    return 0;
}

if (plan.IsUpToDate && !plan.FullRebuild)
{
    return 0;
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

IReadOnlyList<float[]> vectors = [];
if (plan.ToEmbed.Count > 0)
{
    try
    {
        IEmbeddingClient client = new AzureEmbeddingClient(settings);
        vectors = await client.EmbedAsync(plan.ToEmbed.Select(c => c.EmbeddingInput).ToList(), CancellationToken.None);
    }
    catch (EmbeddingUnavailableException ex)
    {
        Console.Error.WriteLine($"Embedding failed: {ex.Message}");
        return 1;
    }
}

var file = plan.Apply(stored, chunks, vectors, settings.Deployment, EmbeddingSettings.Dimensions);
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(file, DocIndex.Json) + "\n");
Console.WriteLine($"Embedded {plan.ToEmbed.Count} chunk(s) in one request, kept {plan.Unchanged.Count}, dropped {plan.Removed.Count}. " +
    $"Wrote {file.Chunks.Count} vectors to api/Data/{DocIndex.FileName}.");
return 0;

static void Print(EmbeddingPlan plan, bool noFile)
{
    if (noFile)
    {
        Console.WriteLine($"No api/Data/{DocIndex.FileName} yet: all chunks are new.");
    }
    else if (plan.FullRebuild)
    {
        Console.WriteLine("Full rebuild: every chunk will be embedded again.");
    }

    Console.WriteLine($"  unchanged: {plan.Unchanged.Count} (stored vectors reused)");
    foreach (var (label, ids) in new[]
    {
        ("changed", plan.Changed.Select(c => c.Id)),
        ("added", plan.Added.Select(c => c.Id)),
        ("removed", plan.Removed),
    })
    {
        var list = ids.ToList();
        Console.WriteLine($"  {label}: {list.Count}");
        foreach (var id in list)
        {
            Console.WriteLine($"    {id}");
        }
    }

    if (plan.IsUpToDate && !plan.FullRebuild)
    {
        Console.WriteLine($"api/Data/{DocIndex.FileName} is up to date.");
    }
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
