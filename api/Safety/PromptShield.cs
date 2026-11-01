using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Kilowattcher.Api.Agent;

namespace Kilowattcher.Api.Safety;

/// <summary>
/// Layer 5: Azure AI Content Safety Prompt Shields checks each visitor message for jailbreak attempts
/// (role-play overrides, "ignore your rules", DAN-style prompts) before the agent runs. The same check
/// also runs inside the Azure deployment's guardrail as a backstop; calling it here makes it visible
/// in our logs and testable.
/// </summary>
public interface IPromptShield
{
    /// <returns>True when Prompt Shields detects an attack in the message.</returns>
    Task<bool> IsAttackAsync(string message, CancellationToken cancellationToken);
}

public sealed class AzurePromptShield(HttpClient http, IConfiguration configuration, ILogger<AzurePromptShield> logger) : IPromptShield
{
    public const string EndpointKey = "AZURE_CONTENT_SAFETY_ENDPOINT";
    private const string Path = "contentsafety/text:shieldPrompt?api-version=2024-09-01";

    public async Task<bool> IsAttackAsync(string message, CancellationToken cancellationToken)
    {
        var endpoint = configuration[EndpointKey];
        var apiKey = configuration["AZURE_OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey)
            || !Uri.TryCreate(endpoint.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogError("Prompt Shields settings are missing ({Key} or key); messages can't be checked.", EndpointKey);
            throw new ChatModelException(ChatModelFailure.Unavailable, "Prompt Shields is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, Path))
        {
            Content = JsonContent.Create(new ShieldRequest(message, [])),
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", apiKey.Trim());

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Prompt Shields returned HTTP {Status}.", (int)response.StatusCode);
                throw new ChatModelException(
                    (int)response.StatusCode == StatusCodes.Status429TooManyRequests ? ChatModelFailure.Busy : ChatModelFailure.Unavailable,
                    $"Prompt Shields returned HTTP {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<ShieldResponse>(cancellationToken)
                ?? throw new ChatModelException(ChatModelFailure.Unavailable, "Prompt Shields returned an empty body.");

            return result.UserPromptAnalysis?.AttackDetected
                ?? throw new ChatModelException(ChatModelFailure.Unavailable, "Prompt Shields returned no user prompt analysis.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Prompt Shields request failed ({Error}).", ex.GetType().Name);
            throw new ChatModelException(ChatModelFailure.Unavailable, "Prompt Shields is unreachable.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatModelException(ChatModelFailure.Timeout, "Prompt Shields timed out.", ex);
        }
    }

    private sealed record ShieldRequest(
        [property: JsonPropertyName("userPrompt")] string UserPrompt,
        [property: JsonPropertyName("documents")] string[] Documents);

    private sealed record ShieldResponse([property: JsonPropertyName("userPromptAnalysis")] PromptAnalysis? UserPromptAnalysis);

    private sealed record PromptAnalysis([property: JsonPropertyName("attackDetected")] bool AttackDetected);
}
