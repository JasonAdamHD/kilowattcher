namespace Kilowattcher.Api.Agent;

/// <summary>One entry in the conversation sent to the model, oldest first.</summary>
public abstract record AgentItem;

public sealed record UserItem(string Text) : AgentItem;

public sealed record AssistantItem(string Text) : AgentItem;

/// <summary>A tool call the model asked for, replayed so it can see what it requested.</summary>
public sealed record ToolCallItem(string CallId, string Name, string ArgumentsJson) : AgentItem;

public sealed record ToolOutputItem(string CallId, string OutputJson) : AgentItem;

/// <summary>A server note to the model, such as "your last reply wasn't valid JSON".</summary>
public sealed record DeveloperNoteItem(string Text) : AgentItem;

public sealed record ToolDefinition(string Name, string Description, string ParametersSchema);

public sealed record ModelRequest(
    string Instructions,
    IReadOnlyList<AgentItem> Items,
    IReadOnlyList<ToolDefinition> Tools,
    bool AllowTools);

/// <summary>Either tool calls to run, or final reply text (expected to be the JSON reply).</summary>
public sealed record ModelStep(IReadOnlyList<ToolCallItem> ToolCalls, string? Text, int InputTokens, int OutputTokens);

/// <summary>
/// One model round trip. The agent loop lives in C# (AgentRunner) so it can be unit-tested
/// with a scripted fake; only this interface ever talks to Azure.
/// </summary>
public interface IChatModel
{
    Task<ModelStep> NextAsync(ModelRequest request, CancellationToken cancellationToken);
}

public enum ChatModelFailure
{
    /// <summary>Azure returned 429 (quota or rate limit).</summary>
    Busy,

    /// <summary>No answer in time.</summary>
    Timeout,

    /// <summary>Azure 5xx, auth failure, missing configuration, or an empty reply.</summary>
    Unavailable,

    /// <summary>The model's reply failed JSON validation twice.</summary>
    InvalidReply,
}

public sealed class ChatModelException(ChatModelFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ChatModelFailure Failure { get; } = failure;
}
