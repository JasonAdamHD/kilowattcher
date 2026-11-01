namespace Kilowattcher.Api.Agent;

public sealed record ChatTurn(string Role, string Content);

public sealed record ChatModelReply(string Text, int InputTokens, int OutputTokens);

/// <summary>
/// The only way the API talks to a model. Unit tests swap in a fake, so no test calls Azure.
/// </summary>
public interface IChatModel
{
    /// <param name="turns">Prior history followed by the new user message, oldest first.</param>
    Task<ChatModelReply> CompleteAsync(IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken);
}

public enum ChatModelFailure
{
    /// <summary>Azure returned 429 (quota or rate limit).</summary>
    Busy,

    /// <summary>No answer within 30 seconds.</summary>
    Timeout,

    /// <summary>Azure 5xx, auth failure, missing configuration, or an unreadable reply.</summary>
    Unavailable,
}

public sealed class ChatModelException(ChatModelFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ChatModelFailure Failure { get; } = failure;
}
