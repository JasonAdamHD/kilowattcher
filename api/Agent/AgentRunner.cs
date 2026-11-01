using System.Diagnostics;
using System.Text.Json;
using Kilowattcher.Api.Safety;
using Kilowattcher.Api.Tools;

namespace Kilowattcher.Api.Agent;

/// <summary>A tool the server actually ran, recorded by the server (never taken from the model's claims).</summary>
public sealed record ToolCallRecord(string Name, JsonElement Arguments, JsonElement Result);

/// <summary>A passage the answer relies on, with the text the citation overlay shows.</summary>
public sealed record Citation(string Id, string Title, string Heading, string Text);

public sealed record AgentResult(
    string Topic,
    string Answer,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    IReadOnlyList<Citation> Citations,
    string? SuggestedQuestion,
    bool RoundLimitReached);

/// <summary>
/// The agent loop: system prompt → model → run requested tools → feed results back → repeat,
/// with a hard limit of 5 tool rounds. Then the reply is validated as JSON, with one retry.
/// </summary>
public sealed class AgentRunner(IChatModel model, ToolRegistry tools, HomeClock clock, IAnswerJudge judge, ILogger<AgentRunner> logger)
{
    public const int MaxToolRounds = 5;
    public const string RoundLimitNote = "I may have missed something; try a narrower question.";

    // Fixed wording the server uses instead of the model's text, so an off-topic reply can't leak content.
    public const string OffTopicAnswer =
        "I can only help with home energy: your battery, solar, and electricity rates. Want to try one of those?";
    public const string DefaultSuggestion = "When is electricity cheapest tomorrow?";
    public const string ElectricianNote = "For any wiring or panel work, please contact a licensed electrician.";

    // Used when a safety layer (the answer judge or Prompt Shields) withholds a reply.
    public const string BlockedTopic = "blocked";
    public const string BlockedAnswer =
        "I can't help with that request. I can answer questions about your battery, solar, and electricity rates.";

    private static readonly JsonSerializerOptions ToolJson = new(JsonSerializerDefaults.Web);

    // Whole-request budget across every model call (each call is also capped at 30 seconds).
    public static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(60);

    public async Task<AgentResult> RunAsync(IReadOnlyList<AgentItem> conversation, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TotalBudget);

        var stopwatch = Stopwatch.StartNew();
        // The reminder sits right after the visitor's latest message, so it's the last thing the model reads.
        var items = conversation.Append(new DeveloperNoteItem(SystemPrompt.Reminder)).ToList();
        var records = new List<ToolCallRecord>();
        var citable = new Dictionary<string, Citation>(StringComparer.Ordinal);
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
                    var record = await RunToolAsync(call, citable, budget.Token);
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

            reply = EnforceTopic(reply);

            // Layer 4: an independent reviewer checks the answer itself. Fixed declines skip it.
            // If the judge can't be reached, its exception propagates: nothing unchecked is shown.
            if (reply.Topic != ReplyTopic.OffTopic)
            {
                var question = conversation.OfType<UserItem>().LastOrDefault()?.Text ?? string.Empty;
                var verdict = await judge.ReviewAsync(question, reply.Answer, budget.Token);
                if (!verdict.Allowed)
                {
                    logger.LogWarning("Answer judge withheld a {Topic} reply ({Category}).", reply.Topic, verdict.Category);
                    reply = new AgentReply(BlockedTopic, BlockedAnswer, [], DefaultSuggestion);
                }
            }

            var citations = reply.Citations.Where(citable.ContainsKey).Select(id => citable[id]).ToList();
            var unknown = reply.Citations.Where(id => !citable.ContainsKey(id)).ToList();
            if (unknown.Count > 0)
            {
                // Logged for the owner. Model-written IDs are trimmed so a log line can't grow without bound.
                logger.LogWarning("Removed {Count} citation(s) that search_docs never returned: {Ids}.",
                    unknown.Count, string.Join(", ", unknown.Select(id => id.Length > 80 ? id[..80] : id)));
            }

            var answer = limitReached ? $"{reply.Answer}\n\n{RoundLimitNote}" : reply.Answer;

            // Never log message text: only tool names, sizes, timing, and token counts.
            logger.LogInformation(
                "Agent answered {Topic} in {ElapsedMs} ms after {Rounds} tool round(s) [{Tools}], {InputTokens} input and {OutputTokens} output tokens, limit reached: {LimitReached}.",
                reply.Topic, stopwatch.ElapsedMilliseconds, rounds, string.Join(", ", records.Select(r => r.Name)), usage.Input, usage.Output, limitReached);

            return new AgentResult(reply.Topic, answer, records, citations, reply.SuggestedQuestion, limitReached);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatModelException(ChatModelFailure.Timeout, "The agent ran out of time.", ex);
        }
    }

    /// <summary>
    /// The model labels its own reply; the server decides what that label allows. Off-topic replies are
    /// replaced with fixed wording (whatever the model wrote is dropped), and safety replies always carry
    /// the electrician referral.
    /// </summary>
    internal static AgentReply EnforceTopic(AgentReply reply) => reply.Topic switch
    {
        ReplyTopic.OffTopic => new AgentReply(
            ReplyTopic.OffTopic,
            OffTopicAnswer,
            [],
            string.IsNullOrWhiteSpace(reply.SuggestedQuestion) ? DefaultSuggestion : reply.SuggestedQuestion),
        ReplyTopic.Safety when !reply.Answer.Contains("licensed electrician", StringComparison.OrdinalIgnoreCase) =>
            reply with { Answer = $"{reply.Answer}\n\n{ElectricianNote}", SuggestedQuestion = null },
        ReplyTopic.Safety => reply with { SuggestedQuestion = null },
        _ => reply with { SuggestedQuestion = null },
    };

    private async Task<ToolCallRecord> RunToolAsync(ToolCallItem call, Dictionary<string, Citation> citable, CancellationToken cancellationToken)
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
                result = await tool.ExecuteAsync(arguments.Value, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Tool {Tool} failed.", call.Name);
                result = new ToolError("The tool failed. Answer without it.");
            }
        }

        if (result is DocSearchResult search)
        {
            foreach (var hit in search.Results)
            {
                citable[hit.Id] = new Citation(hit.Id, hit.Title, hit.Heading, hit.Text);
            }
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
