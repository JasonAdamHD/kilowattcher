namespace Kilowattcher.Api.Agent;

/// <param name="Signature">Required on assistant turns: the signature the API returned with that answer.</param>
public sealed record ChatHistoryMessage(string? Role, string? Content, string? Signature = null);

/// <param name="Settings">The visitor's household settings from the settings panel; null keeps every default.</param>
public sealed record ChatRequest(string? Message, IReadOnlyList<ChatHistoryMessage>? History, Kilowattcher.Api.Households.HouseholdSettings? Settings = null);

/// <summary>
/// toolCalls lists the tools the server ran, with arguments and raw results, so the UI can show them.
/// suggestedQuestion is set only when the agent declines an off-topic request.
/// topic is home_energy, off_topic, or safety. signature must be sent back with this answer in history.
/// </summary>
public sealed record ChatResponse(
    string Topic,
    string Answer,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    IReadOnlyList<Citation> Citations,
    string? SuggestedQuestion,
    string Signature);

public static class ChatLimits
{
    public const int MaxMessageLength = 1_000;
    public const int MaxHistoryMessages = 10;

    // Assistant replies are capped at 600 output tokens, which can run past 1,000 characters.
    public const int MaxAssistantHistoryLength = 4_000;

    public const int MaxOutputTokens = 600;
}

public static class ChatRequestValidator
{
    /// <summary>Returns an error code, or null when the request is valid.</summary>
    public static string? Validate(ChatRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Message))
        {
            return "empty_message";
        }

        if (request.Message.Length > ChatLimits.MaxMessageLength)
        {
            return "message_too_long";
        }

        var history = request.History ?? [];
        if (history.Count > ChatLimits.MaxHistoryMessages)
        {
            return "history_too_long";
        }

        foreach (var item in history)
        {
            // Only user and assistant turns: a browser must never be able to send system instructions.
            var limit = item.Role switch
            {
                ChatRole.User => ChatLimits.MaxMessageLength,
                ChatRole.Assistant => ChatLimits.MaxAssistantHistoryLength,
                _ => -1,
            };

            if (limit < 0)
            {
                return "invalid_history_role";
            }

            if (string.IsNullOrWhiteSpace(item.Content) || item.Content.Length > limit)
            {
                return "invalid_history_content";
            }
        }

        return null;
    }
}

public static class ChatRole
{
    public const string User = "user";
    public const string Assistant = "assistant";
}
