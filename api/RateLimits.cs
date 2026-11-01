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

    public const int RedeemPerMinute = 5;
    public const int ChatPerMinute = 10;

    public static IServiceCollection AddKilowattcherRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new ApiError("rate_limited"), cancellationToken);
            };

            options.AddPolicy(RedeemPolicy, http => PerIpPerMinute(http, RedeemPerMinute));
            options.AddPolicy(ChatPolicy, http => PerIpPerMinute(http, ChatPerMinute));
        });

    private static RateLimitPartition<string> PerIpPerMinute(HttpContext http, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(ClientIp(http), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });

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
