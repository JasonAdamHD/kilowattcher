using Kilowattcher.Api.Tests.TestSupport;

namespace Kilowattcher.Api.Tests;

public class ChatRateLimiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Refill = TimeSpan.FromSeconds(6);

    private readonly MutableTimeProvider _time = new(Start);
    private readonly ChatRateLimiter _limiter;

    public ChatRateLimiterTests() => _limiter = new ChatRateLimiter(10, Refill, _time);

    private void Advance(double seconds) => _time.Now += TimeSpan.FromSeconds(seconds);

    private void Drain(string key = "visitor")
    {
        for (var i = 0; i < 10; i++)
        {
            Assert.True(_limiter.TryTake(key).Allowed);
        }
    }

    [Fact]
    public void A_fresh_visitor_gets_a_full_bucket()
    {
        var first = _limiter.TryTake("visitor");

        Assert.True(first.Allowed);
        Assert.Equal(10, first.Limit);
        Assert.Equal(9, first.Remaining);
        Assert.Equal(Refill, first.NextTokenIn);
    }

    [Fact]
    public void The_eleventh_quick_request_is_rejected_with_a_full_refill_wait()
    {
        Drain();

        var blocked = _limiter.TryTake("visitor");

        Assert.False(blocked.Allowed);
        Assert.Equal(0, blocked.Remaining);
        Assert.Equal(Refill, blocked.NextTokenIn);
    }

    [Fact]
    public void Wait_time_counts_down_exactly()
    {
        Drain();

        Advance(2);
        Assert.Equal(TimeSpan.FromSeconds(4), _limiter.TryTake("visitor").NextTokenIn);

        Advance(3.5);
        Assert.Equal(TimeSpan.FromSeconds(0.5), _limiter.TryTake("visitor").NextTokenIn);
    }

    [Fact]
    public void One_token_comes_back_after_one_refill_period()
    {
        Drain();

        Advance(6);
        var allowed = _limiter.TryTake("visitor");
        var blocked = _limiter.TryTake("visitor");

        Assert.True(allowed.Allowed);
        Assert.Equal(0, allowed.Remaining);
        Assert.False(blocked.Allowed);
        Assert.Equal(Refill, blocked.NextTokenIn);
    }

    [Fact]
    public void Refills_keep_a_steady_beat_and_carry_partial_periods()
    {
        Drain();

        Advance(13); // two tokens earned, 1 s into the third period
        Assert.True(_limiter.TryTake("visitor").Allowed);
        var second = _limiter.TryTake("visitor");

        Assert.True(second.Allowed);
        Assert.Equal(0, second.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(5), second.NextTokenIn);
    }

    [Fact]
    public void The_bucket_never_holds_more_than_its_capacity()
    {
        _limiter.TryTake("visitor");

        Advance(600);
        var decision = _limiter.TryTake("visitor");

        Assert.Equal(9, decision.Remaining);
    }

    [Fact]
    public void Each_visitor_has_their_own_bucket()
    {
        Drain("noisy");

        var other = _limiter.TryTake("quiet");

        Assert.True(other.Allowed);
        Assert.Equal(9, other.Remaining);
    }

    [Fact]
    public void Visitors_whose_bucket_has_refilled_are_forgotten()
    {
        _limiter.TryTake("left-a-while-ago");
        Drain("still-active");
        Assert.Equal(2, _limiter.TrackedVisitors);

        // After a minute both buckets are full again, so the sweep forgets both;
        // only the visitor who comes back is tracked again.
        Advance(61);
        _limiter.TryTake("still-active");

        Assert.Equal(1, _limiter.TrackedVisitors);
    }
}
