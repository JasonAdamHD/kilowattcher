using System.Reflection;
using Kilowattcher.Api.Rag;

namespace Kilowattcher.Api.Tests.Rag;

/// <summary>Checks the real docs in api/Data/docs and the committed embeddings.json.</summary>
public class RepoDocsTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly IReadOnlyList<DocChunk> Chunks = MarkdownChunker.ChunkDirectory(Path.Combine(Root, "api", "Data", "docs"));

    [Fact]
    public void Docs_produce_uniquely_identified_chunks()
    {
        Assert.InRange(Chunks.Count, 20, 80);
        Assert.Equal(Chunks.Count, Chunks.Select(c => c.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("battery-manual#reserve-percentage")]
    [InlineData("outage-backup#how-long-backup-lasts")]
    [InlineData("faq#should-i-charge-my-battery-from-the-grid-tonight")]
    [InlineData("time-of-use-plan#rate-periods-and-prices")]
    public void Demo_passages_exist(string id)
    {
        Assert.Contains(Chunks, c => c.Id == id);
    }

    [Fact]
    public void Every_section_is_a_self_contained_passage()
    {
        Assert.All(Chunks, c =>
        {
            var words = c.Text.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
            Assert.True(words is >= 30 and <= 200, $"{c.Id} has {words} words; keep sections between 30 and 200.");
        });
    }

    [Fact]
    public void Embeddings_file_matches_the_current_docs()
    {
        var path = Path.Combine(Root, "api", "Data", DocIndex.FileName);
        Assert.True(File.Exists(path), "api/Data/embeddings.json is missing. Run: dotnet run --project tools/embed");

        var file = DocIndex.ReadFile(path);
        Assert.Equal(EmbeddingSettings.Dimensions, file.Dimensions);

        var stored = file.Chunks.ToDictionary(c => c.Id, c => c.ContentHash);
        var current = Chunks.ToDictionary(c => c.Id, c => c.ContentHash);
        Assert.True(
            stored.Count == current.Count && current.All(c => stored.TryGetValue(c.Key, out var hash) && hash == c.Value),
            "Docs changed since embeddings.json was built. Run: dotnet run --project tools/embed");

        // Loads without errors, every vector the right size.
        Assert.Equal(Chunks.Count, DocIndex.FromFile(file).Count);
    }

    private static string FindRepoRoot() =>
        typeof(RepoDocsTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "RepoRoot").Value!;
}
