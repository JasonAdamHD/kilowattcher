using Kilowattcher.Api.Rag;
using Kilowattcher.Api.Tests.TestSupport;

namespace Kilowattcher.Api.Tests.Rag;

public class DocIndexTests
{
    [Fact]
    public void Ranks_chunks_by_cosine_similarity_best_first()
    {
        var index = TestIndex.ThreeAxes();

        var matches = index.Search([0.1f, 0.9f, 0.3f], top: 3);

        Assert.Equal(
            ["time-of-use-plan#rate-periods-and-prices", "outage-backup#how-long-backup-lasts", "battery-manual#reserve-percentage"],
            matches.Select(m => m.Chunk.Id));
        Assert.True(matches[0].Score > matches[1].Score && matches[1].Score > matches[2].Score);
    }

    [Fact]
    public void Vector_length_does_not_affect_the_score()
    {
        var index = TestIndex.ThreeAxes();

        var small = index.Search([0.2f, 0.1f, 0f], top: 1)[0];
        var large = index.Search([20f, 10f, 0f], top: 1)[0];

        Assert.Equal(small.Chunk.Id, large.Chunk.Id);
        Assert.Equal(small.Score, large.Score, precision: 6);
    }

    [Fact]
    public void Identical_direction_scores_one_and_opposite_scores_minus_one()
    {
        var index = new DocIndex(2, [(TestIndex.Chunk("a#same"), [3f, 4f]), (TestIndex.Chunk("b#opposite"), [-3f, -4f])]);

        var matches = index.Search([0.6f, 0.8f], top: 2);

        Assert.Equal(1.0, matches[0].Score, precision: 5);
        Assert.Equal(-1.0, matches[1].Score, precision: 5);
    }

    [Fact]
    public void Returns_at_most_the_requested_number()
    {
        Assert.Single(TestIndex.ThreeAxes().Search([1f, 1f, 1f], top: 1));
    }

    [Fact]
    public void Rejects_vectors_of_the_wrong_size()
    {
        Assert.Throws<InvalidDataException>(() => new DocIndex(3, [(TestIndex.Chunk("a#b"), [1f, 0f])]));
        Assert.Throws<ArgumentException>(() => TestIndex.ThreeAxes().Search([1f, 0f], top: 3));
    }
}
