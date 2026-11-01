using System.Diagnostics;
using System.Text.Json;
using Kilowattcher.Api.Tools;

namespace Kilowattcher.Api.Agent;

/// <summary>A tool the server actually ran, recorded by the server (never taken from the model's claims).</summary>
public sealed record ToolCallRecord(string Name, JsonElement Arguments, JsonElement Result);

public sealed record AgentResult(
    string Answer,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    IReadOnlyList<string> Citations,
    string? SuggestedQuestion,
    bool RoundLimitReached);

/// <summary>
/// The agent loop: system prompt → model → run requested tools → feed results back → repeat,
/// with a hard limit of 5 tool rounds. Then the reply is validated as JSON, with one retry.
/// </summary>
public sealed class AgentRunner(IChatModel model, ToolRegistry tools, HomeClock clock, ILogger<AgentRunner> logger)
{
    public const int MaxToolRounds = 5;
    public const string RoundLimitNote = "I may have missed something; try a narrower question.";

    private static readonly JsonSerializerOptions ToolJson = new(JsonSerializerDefaults.Web);

    // Whole-request budget across every model call (each call is also capped at 30 seconds).
    public static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(60);

    public async Task<AgentResult> RunAsync(IReadOnlyList<AgentItem> conversation, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TotalBudget);

        var stopwatch = Stopwatch.StartNew();
        var items = conversation.ToList();
        var records = new List<ToolCallRecord>();
        var citableIds = new HashSet<string>(StringComparer.Ordinal);
        var definitions = tools.All.Select(t => new ToolDefinition(t.Name, t.Description, t.ParametersSchema)).ToList();
        var instructions = SystemPrompt.Build(clock);
        var usage = (Input: 0, Output: 0);
        var rounds = 0;
        var limitReached = false;

        try
        {
            ModelStep step;
            while (true)
            {
                step = await model.NextAsync(new ModelRequest(instructions, items, definitions, AllowTools: true), budget.Token);
                usage = (usage.Input + step.InputTokens, usage.Output + step.OutputTokens);

                if (step.ToolCalls.Count == 0)
                {
                    break;
                }

                if (rounds == MaxToolRounds)
                {
                    // The model wants a 6th round. Don't run it: ask for an answer from what it has, tools off.
                    limitReached = true;
                    step = await model.NextAsync(new ModelRequest(instructions, items, definitions, AllowTools: false), budget.Token);
                    usage = (usage.Input + step.InputTokens, usage.Output + step.OutputTokens);

                    if (step.ToolCalls.Count > 0)
                    {
                        throw new ChatModelException(ChatModelFailure.InvalidReply, "Model requested tools with tools switched off.");
                    }

                    break;
                }

                rounds++;
                foreach (var call in step.ToolCalls)
                {
                    var record = RunTool(call, citableIds);
                    records.Add(record);
                    items.Add(call);
                    items.Add(new ToolOutputItem(call.CallId, record.Result.GetRawText()));
                }
            }

            var reply = AgentReplyParser.TryParse(step.Text, out var problem);
            if (reply is null)
            {
                logger.LogWarning("Model reply failed validation ({Problem}); retrying once.", problem);
                items.Add(new AssistantItem(step.Text ?? string.Empty));
                items.Add(new DeveloperNoteItem(
                    "Your last reply did not match the required JSON schema. Reply again with JSON only: answer, citations, suggestedQuestion."));

                var retry = await model.NextAsync(new ModelRequest(instructions, items, definitions, AllowTools: false), budget.Token);
                usage = (usage.Input + retry.InputTokens, usage.Output + retry.OutputTokens);

                reply = retry.ToolCalls.Count == 0 ? AgentReplyParser.TryParse(retry.Text, out problem) : null;
                if (reply is null)
                {
                    throw new ChatModelException(ChatModelFailure.InvalidReply, $"Model reply failed validation twice ({problem ?? "tool call"}).");
                }
            }

            var citations = reply.Citations.Where(citableIds.Contains).ToList();
            var stripped = reply.Citations.Count - citations.Count;
            if (stripped > 0)
            {
                // IDs only, never message text: they come from our own document set.
                logger.LogWarning("Removed {Count} citation(s) that search_docs never returned: {Ids}.",
                    stripped, string.Join(", ", reply.Citations.Except(citations)));
            }

            var answer = limitReached ? $"{reply.Answer}\n\n{RoundLimitNote}" : reply.Answer;

            // Never log message text: only tool names, sizes, timing, and token counts.
            logger.LogInformation(
                "Agent answered in {ElapsedMs} ms after {Rounds} tool round(s) [{Tools}], {InputTokens} input and {OutputTokens} output tokens, limit reached: {LimitReached}.",
                stopwatch.ElapsedMilliseconds, rounds, string.Join(", ", records.Select(r => r.Name)), usage.Input, usage.Output, limitReached);

            return new AgentResult(answer, records, citations, reply.SuggestedQuestion, limitReached);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatModelException(ChatModelFailure.Timeout, "The agent ran out of time.", ex);
        }
    }

    private ToolCallRecord RunTool(ToolCallItem call, HashSet<string> citableIds)
    {
        var arguments = ParseArguments(call.ArgumentsJson);
        object result;

        if (!tools.TryGet(call.Name, out var tool))
        {
            result = new ToolError($"Unknown tool '{call.Name}'.");
        }
        else if (arguments is null)
        {
            result = new ToolError("Arguments were not valid JSON.");
        }
        else
        {
            try
            {
                result = tool.Execute(arguments.Value);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tool {Tool} failed.", call.Name);
                result = new ToolError("The tool failed. Answer without it.");
            }
        }

        if (result is DocSearchResult search)
        {
            citableIds.UnionWith(search.Results.Select(r => r.Id));
        }

        return new ToolCallRecord(
            call.Name,
            arguments ?? JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(result, result.GetType(), ToolJson));
    }

    private static JsonElement? ParseArguments(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
