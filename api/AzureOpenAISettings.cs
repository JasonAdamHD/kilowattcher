using System.ClientModel;
using System.ClientModel.Primitives;

namespace Kilowattcher.Api;

/// <summary>
/// Reads the Azure OpenAI endpoint, key, and a deployment name from IConfiguration
/// (user-secrets locally, environment variables on Vercel). Shared by chat and embeddings.
/// </summary>
public sealed record AzureOpenAISettings(Uri Endpoint, ApiKeyCredential Credential, string Deployment)
{
    public const string ChatDeploymentKey = "AZURE_OPENAI_CHAT_DEPLOYMENT";
    public const string EmbeddingDeploymentKey = "AZURE_OPENAI_EMBEDDING_DEPLOYMENT";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Null when anything is missing or the endpoint isn't HTTPS (the key must never travel in clear text).</summary>
    public static AzureOpenAISettings? TryRead(IConfiguration configuration, string deploymentKey)
    {
        var endpoint = configuration["AZURE_OPENAI_ENDPOINT"];
        var apiKey = configuration["AZURE_OPENAI_API_KEY"];
        var deployment = configuration[deploymentKey];

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment)
            || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return new AzureOpenAISettings(ToV1Endpoint(baseUri), new ApiKeyCredential(apiKey.Trim()), deployment.Trim());
    }

    /// <summary>Applies the shared timeout and retry rules to any OpenAI client options.</summary>
    public T Configure<T>(T options) where T : ClientPipelineOptions
    {
        options.NetworkTimeout = Timeout;
        // One retry at most, so a slow Azure can't stretch a request far past the 30-second budget.
        options.RetryPolicy = new ClientRetryPolicy(maxRetries: 1);
        return options;
    }

    /// <summary>Turns "https://name.openai.azure.com/" into "https://name.openai.azure.com/openai/v1/".</summary>
    public static Uri ToV1Endpoint(Uri baseUri)
    {
        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(baseUri, path + "/");
        }

        return new Uri(baseUri, path + "/openai/v1/");
    }
}
