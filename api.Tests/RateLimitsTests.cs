namespace Kilowattcher.Api.Tests;

public class RateLimitsTests
{
    [Theory]
    [InlineData(0.0, 1)]    // never tell a client to retry "now"
    [InlineData(0.2, 1)]
    [InlineData(5.0, 5)]
    [InlineData(5.01, 6)]   // round up so a client that waits is never early
    [InlineData(59.5, 60)]
    public void Retry_after_is_whole_seconds_rounded_up(double seconds, int expected)
    {
        Assert.Equal(expected, RateLimits.RetryAfterSeconds(TimeSpan.FromSeconds(seconds)));
    }
}
