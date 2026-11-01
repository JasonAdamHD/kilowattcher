using System.Diagnostics;
using Kilowattcher.Api.Auth;

namespace Kilowattcher.Api.Agent;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat", async (ChatRequest? request, HttpContext http, SessionTokenService sessions,
                IChatModel model, ILoggerFactory loggers, CancellationToken cancellationToken) =>
        {
            if (!http.HasValidSession(sessions))
            {
                return ApiErrors.Unauthorized();
            }

            if (ChatRequestValidator.Validate(request) is { } error)
            {
                return Results.BadRequest(new ApiError(error));
            }

            var turns = (request!.History ?? [])
                .Select(h => new ChatTurn(h.Role!, h.Content!))
                .Append(new ChatTurn(ChatRole.User, request.Message!))
                .ToList();

            var logger = loggers.CreateLogger("Chat");
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var reply = await model.CompleteAsync(turns, cancellationToken);

                // Never log message text: only sizes, timing, and token counts.
                logger.LogInformation(
                    "Chat answered in {ElapsedMs} ms with {HistoryCount} history messages, {InputTokens} input and {OutputTokens} output tokens.",
                    stopwatch.ElapsedMilliseconds, turns.Count - 1, reply.InputTokens, reply.OutputTokens);

                return Results.Ok(new ChatResponse(reply.Text, [], []));
            }
            catch (ChatModelException ex)
            {
                logger.LogWarning("Chat failed after {ElapsedMs} ms: {Failure}.", stopwatch.ElapsedMilliseconds, ex.Failure);

                return ex.Failure switch
                {
                    ChatModelFailure.Busy => Results.Json(new ApiError("assistant_busy"), statusCode: StatusCodes.Status429TooManyRequests),
                    ChatModelFailure.Timeout => Results.Json(new ApiError("assistant_timeout"), statusCode: StatusCodes.Status504GatewayTimeout),
                    _ => Results.Json(new ApiError("assistant_unavailable"), statusCode: StatusCodes.Status502BadGateway),
                };
            }
        }).RequireChatRateLimit();
    }
}
