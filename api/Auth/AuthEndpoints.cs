namespace Kilowattcher.Api.Auth;

public sealed record RedeemRequest(string? Code);

public sealed record SessionResponse(bool Authenticated, DateTimeOffset ExpiresAt);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        // Called on page load: 200 shows Chat, 401 shows the Invite screen.
        auth.MapGet("/session", (HttpContext http, SessionTokenService sessions) =>
        {
            var expiresAt = sessions.Validate(http.Request.Cookies[SessionTokenService.CookieName]);
            return expiresAt is { } expiry
                ? Results.Ok(new SessionResponse(true, expiry))
                : ApiErrors.Unauthorized();
        });

        auth.MapPost("/redeem", (RedeemRequest? request, HttpContext http,
                InviteCodeVerifier verifier, SessionTokenService sessions, ILoggerFactory loggers) =>
        {
            if (!verifier.IsConfigured || !sessions.IsConfigured)
            {
                loggers.CreateLogger("Auth").LogError("Redeem refused: invite code hash or signing key is not configured.");
                return ApiErrors.NotConfigured();
            }

            if (string.IsNullOrWhiteSpace(request?.Code))
            {
                return Results.BadRequest(new ApiError("missing_code"));
            }

            if (!verifier.Verify(request.Code))
            {
                return Results.Json(new ApiError("invalid_code"), statusCode: StatusCodes.Status401Unauthorized);
            }

            var (token, expiresAt) = sessions.Issue();
            http.Response.Cookies.Append(SessionTokenService.CookieName, token, SessionCookie.Options(expiresAt));

            return Results.Ok(new SessionResponse(true, expiresAt));
        }).RequireRateLimiting(RateLimits.RedeemPolicy);
    }
}

public static class SessionCookie
{
    public static CookieOptions Options(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/api",
        Expires = expiresAt,
        IsEssential = true,
    };

    public static bool HasValidSession(this HttpContext http, SessionTokenService sessions) =>
        sessions.Validate(http.Request.Cookies[SessionTokenService.CookieName]) is not null;
}
