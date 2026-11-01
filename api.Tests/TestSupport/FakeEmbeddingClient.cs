using Kilowattcher.Api.Rag;

namespace Kilowattcher.Api.Tests.TestSupport;

/// <summary>Returns a vector chosen by the test; never calls Azure.</summary>
public sealed class FakeEmbeddingClient(Func<string, float[]>? embed = null) : IEmbeddingClient
{
    public List<string> Inputs { get; } = [];

    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Inputs.AddRange(inputs);
        IReadOnlyList<float[]> vectors = inputs.Select(i => embed?.Invoke(i) ?? [1f, 0f, 0f]).ToList();
        return Task.FromResult(vectors);
    }
}

public static class TestIndex
{
    public static DocChunk Chunk(string id) => new(id, "Test doc", id.Split('#').Last(), $"Text of {id}.");

    /// <summary>Three chunks pointing along the three axes, so the best match is easy to predict.</summary>
    public static DocIndex ThreeAxes() => new(3,
    [
        (Chunk("battery-manual#reserve-percentage"), [1f, 0f, 0f]),
        (Chunk("time-of-use-plan#rate-periods-and-prices"), [0f, 1f, 0f]),
        (Chunk("outage-backup#how-long-backup-lasts"), [0f, 0f, 1f]),
    ]);

    public static DocIndexProvider Provider(DocIndex index) => new(index);
}
