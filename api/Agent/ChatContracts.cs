namespace Kilowattcher.Api.Agent;

public sealed record ChatHistoryMessage(string? Role, string? Content);

public sealed record ChatRequest(string? Message, IReadOnlyList<ChatHistoryMessage>? History);

/// <summary>Stage 2 always returns empty toolCalls and citations; Stages 3 and 4 fill them.</summary>
public sealed record ChatResponse(string Answer, IReadOnlyList<string> ToolCalls, IReadOnlyList<string> Citations);

public static class ChatLimits
{
    public const int MaxMessageLength = 1_000;
    public const int MaxHistoryMessages = 10;

    // Assistant replies are capped at 600 output tokens, which can run past 1,000 characters.
    public const int MaxAssistantHistoryLength = 4_000;

    public const int MaxOutputTokens = 600;
    public static readonly TimeSpan AzureTimeout = TimeSpan.FromSeconds(30);
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
