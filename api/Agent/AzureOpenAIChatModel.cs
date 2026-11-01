// The Responses API is marked experimental (OPENAI001) in OpenAI 2.14.0. The suppression is
// kept to this one adapter so an SDK change can only break this file.
#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI.Responses;

namespace Kilowattcher.Api.Agent;

/// <summary>
/// Calls the Azure OpenAI chat deployment through the stable OpenAI .NET SDK and the Azure v1 endpoint.
/// </summary>
public sealed class AzureOpenAIChatModel : IChatModel
{
    // Stage 3 replaces this with the full home-energy scope, refusal, and safety prompt.
    internal const string SystemPrompt =
        "You are Kilowattcher, a friendly assistant for questions about home batteries, " +
        "rooftop solar, and electricity rates. Answer in a few short, plain sentences.";

    private readonly Lazy<(ResponsesClient Client, string Deployment)?> _client;
    private readonly ILogger<AzureOpenAIChatModel> _logger;

    public AzureOpenAIChatModel(IConfiguration configuration, ILogger<AzureOpenAIChatModel> logger)
    {
        _logger = logger;
        _client = new Lazy<(ResponsesClient, string)?>(() => CreateClient(configuration));
    }

    public async Task<ChatModelReply> CompleteAsync(IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken)
    {
        if (_client.Value is not { } configured)
        {
            throw new ChatModelException(ChatModelFailure.Unavailable, "Azure OpenAI is not configured.");
        }

        var (client, deployment) = configured;

        var options = new CreateResponseOptions
        {
            Model = deployment,
            Instructions = SystemPrompt,
            MaxOutputTokenCount = ChatLimits.MaxOutputTokens,
            StoredOutputEnabled = false, // Don't keep visitor conversations on the Azure side.
        };

        foreach (var turn in turns)
        {
            options.InputItems.Add(turn.Role == ChatRole.Assistant
                ? ResponseItem.CreateAssistantMessageItem(turn.Content, [])
                : ResponseItem.CreateUserMessageItem(turn.Content));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ChatLimits.AzureTimeout);

        try
        {
            ResponseResult result = await client.CreateResponseAsync(options, timeout.Token);
            var text = result.GetOutputText();

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ChatModelException(ChatModelFailure.Unavailable, "The model returned no text.");
            }

            return new ChatModelReply(text.Trim(), result.Usage?.InputTokenCount ?? 0, result.Usage?.OutputTokenCount ?? 0);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatModelException(ChatModelFailure.Timeout, "Azure OpenAI did not answer within 30 seconds.", ex);
        }
        catch (ClientResultException ex)
        {
            // Log the status only; Azure error bodies can echo request content.
            _logger.LogWarning("Azure OpenAI returned HTTP {Status}.", ex.Status);
            var failure = ex.Status == StatusCodes.Status429TooManyRequests ? ChatModelFailure.Busy : ChatModelFailure.Unavailable;
            throw new ChatModelException(failure, $"Azure OpenAI returned HTTP {ex.Status}.", ex);
        }
    }

    private (ResponsesClient, string)? CreateClient(IConfiguration configuration)
    {
        var endpoint = configuration["AZURE_OPENAI_ENDPOINT"];
        var apiKey = configuration["AZURE_OPENAI_API_KEY"];
        var deployment = configuration["AZURE_OPENAI_CHAT_DEPLOYMENT"];

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment)
            || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogError("Azure OpenAI settings are missing or invalid (endpoint, key, or chat deployment).");
            return null;
        }

        var options = new ResponsesClientOptions
        {
            Endpoint = ToV1Endpoint(baseUri),
            NetworkTimeout = ChatLimits.AzureTimeout,
            // One retry at most, so a slow Azure can't stretch a request far past the 30-second budget.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 1),
        };

        return (new ResponsesClient(new ApiKeyCredential(apiKey.Trim()), options), deployment.Trim());
    }

    /// <summary>Turns "https://name.openai.azure.com/" into "https://name.openai.azure.com/openai/v1/".</summary>
    internal static Uri ToV1Endpoint(Uri baseUri)
    {
        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(baseUri, path + "/");
        }

        return new Uri(baseUri, path + "/openai/v1/");
    }
}
