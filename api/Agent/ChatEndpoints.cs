using System.Diagnostics;
using Kilowattcher.Api.Auth;
using Kilowattcher.Api.Safety;

namespace Kilowattcher.Api.Agent;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat", async (ChatRequest? request, HttpContext http, SessionTokenService sessions,
                AgentRunner agent, IPromptShield shield, HistorySigner signer, ILoggerFactory loggers, CancellationToken cancellationToken) =>
        {
            if (!http.HasValidSession(sessions))
            {
                return ApiErrors.Unauthorized();
            }

            if (ChatRequestValidator.Validate(request) is { } error)
            {
                return Results.BadRequest(new ApiError(error));
            }

            // Only answers this API actually gave may appear as assistant turns.
            if ((request!.History ?? []).Any(h => h.Role == ChatRole.Assistant && !signer.Verify(h.Content, h.Signature)))
            {
                loggers.CreateLogger("Chat").LogWarning("Rejected chat history with an unsigned or altered assistant turn.");
                return Results.BadRequest(new ApiError("invalid_history"));
            }

            var conversation = (request!.History ?? [])
                .Select(h => h.Role == ChatRole.Assistant ? (AgentItem)new AssistantItem(h.Content!) : new UserItem(h.Content!))
                .Append(new UserItem(request.Message!))
                .ToList();

            var stopwatch = Stopwatch.StartNew();
            try
            {
                // Layer 5: Prompt Shields screens the visitor's message before any model sees it.
                if (await shield.IsAttackAsync(request.Message!, cancellationToken))
                {
                    loggers.CreateLogger("Chat").LogWarning("Prompt Shields flagged a message; the agent did not run.");
                    return Blocked(signer);
                }

                var result = await agent.RunAsync(conversation, cancellationToken);
                return Results.Ok(new ChatResponse(
                    result.Topic, result.Answer, result.ToolCalls, result.Citations, result.SuggestedQuestion, signer.Sign(result.Answer)));
            }
            catch (ChatModelException ex) when (ex.Failure == ChatModelFailure.Blocked)
            {
                loggers.CreateLogger("Chat").LogWarning("Azure's guardrail blocked a chat request.");
                return Blocked(signer);
            }
            catch (ChatModelException ex)
            {
                loggers.CreateLogger("Chat").LogWarning("Chat failed after {ElapsedMs} ms: {Failure}.", stopwatch.ElapsedMilliseconds, ex.Failure);

                return ex.Failure switch
                {
                    ChatModelFailure.Busy => Results.Json(new ApiError("assistant_busy"), statusCode: StatusCodes.Status429TooManyRequests),
                    ChatModelFailure.Timeout => Results.Json(new ApiError("assistant_timeout"), statusCode: StatusCodes.Status504GatewayTimeout),
                    ChatModelFailure.InvalidReply => Results.Json(new ApiError("invalid_answer"), statusCode: StatusCodes.Status502BadGateway),
                    _ => Results.Json(new ApiError("assistant_unavailable"), statusCode: StatusCodes.Status502BadGateway),
                };
            }
        }).RequireChatRateLimit();
    }

    /// <summary>A normal, signed reply, so the visitor sees a polite decline and can keep chatting.</summary>
    private static IResult Blocked(HistorySigner signer) =>
        Results.Ok(new ChatResponse(
            AgentRunner.BlockedTopic, AgentRunner.BlockedAnswer, [], [], AgentRunner.DefaultSuggestion, signer.Sign(AgentRunner.BlockedAnswer)));
}
