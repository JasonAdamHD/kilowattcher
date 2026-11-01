using System.ClientModel;
using OpenAI;
using OpenAI.Embeddings;

namespace Kilowattcher.Api.Rag;

public static class EmbeddingSettings
{
    // text-embedding-3-small can shorten its vectors; 512 keeps embeddings.json small with little
    // loss for a few dozen short passages. Queries must use the same size as the index.
    public const int Dimensions = 512;
}

/// <summary>Turns text into vectors. Tests use a fake; only the Azure version makes network calls.</summary>
public interface IEmbeddingClient
{
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken);
}

public sealed class EmbeddingUnavailableException(string message, int? status = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? Status { get; } = status;
}

public sealed class AzureEmbeddingClient(AzureOpenAISettings? settings) : IEmbeddingClient
{
    private readonly EmbeddingClient? _client = settings is null
        ? null
        : new EmbeddingClient(settings.Deployment, settings.Credential, settings.Configure(new OpenAIClientOptions { Endpoint = settings.Endpoint }));

    public AzureEmbeddingClient(IConfiguration configuration)
        : this(AzureOpenAISettings.TryRead(configuration, AzureOpenAISettings.EmbeddingDeploymentKey))
    {
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new EmbeddingUnavailableException("Azure OpenAI embedding settings are missing or invalid.");
        }

        try
        {
            OpenAIEmbeddingCollection result = await _client.GenerateEmbeddingsAsync(
                inputs, new EmbeddingGenerationOptions { Dimensions = EmbeddingSettings.Dimensions }, cancellationToken);

            return result.OrderBy(e => e.Index).Select(e => e.ToFloats().ToArray()).ToList();
        }
        catch (ClientResultException ex)
        {
            throw new EmbeddingUnavailableException($"Azure OpenAI returned HTTP {ex.Status}.", ex.Status, ex);
        }
    }
}
