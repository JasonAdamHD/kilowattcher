using System.Threading.RateLimiting;

namespace Kilowattcher.Api;

/// <summary>
/// Best-effort, in-memory, per-IP limits. Counters reset when the container scales to zero;
/// the Azure tokens-per-minute quota is the hard ceiling.
/// </summary>
public static class RateLimits
{
    public const string RedeemPolicy = "redeem";

    // Redeem: built-in fixed window. A burst of wrong codes locks the visitor out for the rest
    // of the minute, matching the "Too many tries. Wait a minute" screen.
    public const int RedeemPerMinute = 5;
    private static readonly TimeSpan RedeemWindow = TimeSpan.FromMinutes(1);

    // Chat: our own token bucket (ChatRateLimiter). A visitor can fire 10 quick questions,
    // then earns one more every 6 seconds (10 per minute sustained).
    public const int ChatBurst = 10;
    public static readonly TimeSpan ChatRefillEvery = TimeSpan.FromSeconds(6);

    public static IServiceCollection AddKilowattcherRateLimits(this IServiceCollection services)
    {
        services.AddSingleton(sp => new ChatRateLimiter(ChatBurst, ChatRefillEvery, sp.GetRequiredService<TimeProvider>()));

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                // The built-in limiter reports the full window, not the time left in it, so this
                // is an upper bound: a client that waits this long is guaranteed to get through.
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) && wait > TimeSpan.Zero
                    ? wait
                    : RedeemWindow;

                context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds(retryAfter).ToString();
                await context.HttpContext.Response.WriteAsJsonAsync(new ApiError("rate_limited"), cancellationToken);
            };

            options.AddPolicy(RedeemPolicy, http =>
                RateLimitPartition.GetFixedWindowLimiter(ClientIp(http), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = RedeemPerMinute,
                    Window = RedeemWindow,
                    QueueLimit = 0,
                }));
        });
    }

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
