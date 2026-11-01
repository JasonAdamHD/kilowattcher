using System.Collections.Concurrent;

namespace Kilowattcher.Api;

/// <param name="Allowed">Whether this request may proceed.</param>
/// <param name="Remaining">Tokens left after this request.</param>
/// <param name="NextTokenIn">Time until one more token arrives; zero when the bucket is full.</param>
public readonly record struct RateLimitDecision(bool Allowed, int Limit, int Remaining, TimeSpan NextTokenIn);

/// <summary>
/// Per-IP token bucket with exact timing: each visitor holds up to <c>capacity</c> tokens and
/// earns one back every <c>refillEvery</c>. Unlike the built-in .NET limiter, it reports the real
/// time until the next token, so clients can be told exactly when to retry.
/// In memory only: buckets reset when the container scales to zero.
/// </summary>
public sealed class ChatRateLimiter(int capacity, TimeSpan refillEvery, TimeProvider time)
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private DateTimeOffset _lastSweep = time.GetUtcNow();

    internal int TrackedVisitors => _buckets.Count;

    public RateLimitDecision TryTake(string key)
    {
        var now = time.GetUtcNow();
        SweepIdleBuckets(now);

        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(capacity, now));
        lock (bucket)
        {
            Refill(bucket, now);

            var allowed = bucket.Tokens > 0;
            if (allowed)
            {
                if (bucket.Tokens == capacity)
                {
                    // Leaving a full bucket starts the refill clock now.
                    bucket.LastRefill = now;
                }

                bucket.Tokens--;
            }

            var nextTokenIn = bucket.Tokens == capacity ? TimeSpan.Zero : bucket.LastRefill + refillEvery - now;
            return new RateLimitDecision(allowed, capacity, bucket.Tokens, nextTokenIn);
        }
    }

    private void Refill(Bucket bucket, DateTimeOffset now)
    {
        if (bucket.Tokens == capacity)
        {
            return;
        }

        var earned = (int)((now - bucket.LastRefill).Ticks / refillEvery.Ticks);
        if (earned <= 0)
        {
            return;
        }

        bucket.Tokens = Math.Min(capacity, bucket.Tokens + earned);
        // Keep the leftover partial period, so refills stay on a steady 6-second beat.
        bucket.LastRefill += refillEvery * earned;
    }

    /// <summary>Drops visitors whose bucket has refilled to full, so memory stays bounded.</summary>
    private void SweepIdleBuckets(DateTimeOffset now)
    {
        if (now - _lastSweep < SweepEvery)
        {
            return;
        }

        _lastSweep = now;
        foreach (var (key, bucket) in _buckets)
        {
            lock (bucket)
            {
                Refill(bucket, now);
                if (bucket.Tokens == capacity)
                {
                    _buckets.TryRemove(key, out _);
                }
            }
        }
    }

    private sealed class Bucket(int tokens, DateTimeOffset lastRefill)
    {
        public int Tokens { get; set; } = tokens;
        public DateTimeOffset LastRefill { get; set; } = lastRefill;
    }
}

public static class ChatRateLimitFilter
{
    /// <summary>
    /// Runs before the chat handler, so every attempt counts (including ones that fail auth).
    /// Every response carries RateLimit-Limit / RateLimit-Remaining / RateLimit-Reset
    /// (seconds until the next token, 0 when full); a 429 adds an exact Retry-After.
    /// </summary>
    public static RouteHandlerBuilder RequireChatRateLimit(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var limiter = http.RequestServices.GetRequiredService<ChatRateLimiter>();
            var decision = limiter.TryTake(RateLimits.ClientIp(http));

            var resetSeconds = decision.NextTokenIn > TimeSpan.Zero ? RateLimits.RetryAfterSeconds(decision.NextTokenIn) : 0;
            http.Response.Headers["RateLimit-Limit"] = decision.Limit.ToString();
            http.Response.Headers["RateLimit-Remaining"] = decision.Remaining.ToString();
            http.Response.Headers["RateLimit-Reset"] = resetSeconds.ToString();

            if (!decision.Allowed)
            {
                http.Response.Headers.RetryAfter = resetSeconds.ToString();
                return Results.Json(new ApiError("rate_limited"), statusCode: StatusCodes.Status429TooManyRequests);
            }

            return await next(context);
        });
}
