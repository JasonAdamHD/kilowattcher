using Kilowattcher.Api.Households;
using System.Text.Json;
using Kilowattcher.Api.Rag;
using Kilowattcher.Api.Tests.TestSupport;
using Kilowattcher.Api.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kilowattcher.Api.Tests.Rag;

public class SearchDocsToolTests
{
    private static readonly ToolContext Context = TestHouseholds.Context(HomeClock.For(TimeProvider.System));

    private static JsonElement Query(string query) => JsonSerializer.SerializeToElement(new { query });

    private static SearchDocsTool Create(DocIndex index, FakeEmbeddingClient embeddings) =>
        new(TestIndex.Provider(index), embeddings, NullLogger<SearchDocsTool>.Instance);

    [Fact]
    public async Task Returns_the_top_three_passages_for_the_query()
    {
        var embeddings = new FakeEmbeddingClient(_ => [0.9f, 0.1f, 0.2f]);

        var result = Assert.IsType<DocSearchResult>(
            await Create(TestIndex.ThreeAxes(), embeddings).ExecuteAsync(Query("reserve percentage"), Context, CancellationToken.None));

        Assert.Equal(SearchDocsTool.TopResults, result.Results.Count);
        Assert.Equal("battery-manual#reserve-percentage", result.Results[0].Id);
        Assert.Equal("Text of battery-manual#reserve-percentage.", result.Results[0].Text);
        Assert.Null(result.Note);
        Assert.Equal(["reserve percentage"], embeddings.Inputs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_an_empty_query_without_calling_azure(string query)
    {
        var embeddings = new FakeEmbeddingClient();

        var result = await Create(TestIndex.ThreeAxes(), embeddings).ExecuteAsync(Query(query), Context, CancellationToken.None);

        Assert.IsType<ToolError>(result);
        Assert.Empty(embeddings.Inputs);
    }

    [Fact]
    public async Task Rejects_an_overlong_query()
    {
        var result = await Create(TestIndex.ThreeAxes(), new FakeEmbeddingClient())
            .ExecuteAsync(Query(new string('a', SearchDocsTool.MaxQueryLength + 1)), Context, CancellationToken.None);

        Assert.IsType<ToolError>(result);
    }

    [Fact]
    public async Task Missing_index_returns_no_results_with_a_note()
    {
        var embeddings = new FakeEmbeddingClient();

        var result = Assert.IsType<DocSearchResult>(
            await Create(DocIndex.Empty, embeddings).ExecuteAsync(Query("reserve"), Context, CancellationToken.None));

        Assert.Empty(result.Results);
        Assert.NotNull(result.Note);
        Assert.Empty(embeddings.Inputs);
    }

    [Fact]
    public async Task Embedding_outage_returns_no_results_instead_of_failing_the_chat()
    {
        var embeddings = new FakeEmbeddingClient { Failure = new EmbeddingUnavailableException("busy", 429) };

        var result = Assert.IsType<DocSearchResult>(
            await Create(TestIndex.ThreeAxes(), embeddings).ExecuteAsync(Query("reserve"), Context, CancellationToken.None));

        Assert.Empty(result.Results);
        Assert.Contains("temporarily unavailable", result.Note);
    }
}
