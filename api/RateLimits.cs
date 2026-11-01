using System.Threading.RateLimiting;

namespace Kilowattcher.Api;

/// <summary>
/// Best-effort, in-memory, per-IP limits. Counters reset when the container scales to zero;
/// the Azure tokens-per-minute quota is the hard ceiling.
/// </summary>
public static class RateLimits
{
    public const string RedeemPolicy = "redeem";
    public const string ChatPolicy = "chat";

    // Redeem: fixed window. A burst of wrong codes locks the visitor out until the minute ends,
    // matching the "Too many tries. Wait a minute" screen.
    public const int RedeemPerMinute = 5;

    // Chat: token bucket. A visitor can fire 10 quick questions, then gets one more every
    // 6 seconds (10 per minute sustained) instead of waiting for a whole window to reset.
    public const int ChatBurst = 10;
    public static readonly TimeSpan ChatRefillEvery = TimeSpan.FromSeconds(6);

    private static readonly TimeSpan FallbackRetryAfter = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddKilowattcherRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                // Tell the client exactly when a retry will succeed, as reported by the limiter itself.
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) && wait > TimeSpan.Zero
                    ? wait
                    : FallbackRetryAfter;

                context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds(retryAfter).ToString();
                await context.HttpContext.Response.WriteAsJsonAsync(new ApiError("rate_limited"), cancellationToken);
            };

            options.AddPolicy(RedeemPolicy, http =>
                RateLimitPartition.GetFixedWindowLimiter(ClientIp(http), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RedeemPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

            options.AddPolicy(ChatPolicy, http =>
                RateLimitPartition.GetTokenBucketLimiter(ClientIp(http), _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = ChatBurst,
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = ChatRefillEvery,
                    AutoReplenishment = true,
                    QueueLimit = 0,
                }));
        });

    /// <summary>Whole seconds, rounded up, so a client that waits this long is never early.</summary>
    internal static int RetryAfterSeconds(TimeSpan wait) => Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));

    /// <summary>
    /// The container is only reachable through Vercel, which sets x-real-ip / x-forwarded-for
    /// to the visitor's address. The IP lives in memory only, never in logs.
    /// </summary>
    public static string ClientIp(HttpContext http)
    {
        var realIp = http.Request.Headers["x-real-ip"].ToString();
        if (!string.IsNullOrWhiteSpace(realIp))
        {
            return realIp.Trim();
        }

        var forwardedFor = http.Request.Headers["x-forwarded-for"].ToString();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            return forwardedFor.Split(',')[0].Trim();
        }

        return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
