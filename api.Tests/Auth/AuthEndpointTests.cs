using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kilowattcher.Api.Auth;
using Kilowattcher.Api.Tests.TestSupport;

namespace Kilowattcher.Api.Tests.Auth;

public class AuthEndpointTests(KilowattcherFactory factory) : IClassFixture<KilowattcherFactory>
{
    [Fact]
    public async Task Session_is_401_without_a_cookie()
    {
        var response = await factory.CreateVisitor().GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Redeeming_the_right_code_sets_a_locked_down_session_cookie()
    {
        var response = await factory.CreateVisitor().PostAsJsonAsync("/api/auth/redeem", new { code = KilowattcherFactory.InviteCode });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        var attributes = cookie.ToLowerInvariant();
        Assert.StartsWith($"{SessionTokenService.CookieName}=", cookie);
        Assert.Contains("httponly", attributes);
        Assert.Contains("secure", attributes);
        Assert.Contains("samesite=strict", attributes);
        Assert.Contains("path=/api", attributes);
        Assert.Contains("expires=", attributes);
    }

    [Fact]
    public async Task Session_is_200_after_redeeming()
    {
        var visitor = await factory.CreateSignedInVisitor();

        var response = await visitor.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.True(body.RootElement.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddHours(23));
    }

    [Fact]
    public async Task Wrong_code_is_401_and_sets_no_cookie()
    {
        var response = await factory.CreateVisitor().PostAsJsonAsync("/api/auth/redeem", new { code = "not-the-code" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("invalid_code", await ErrorCode(response));
    }

    [Fact]
    public async Task Missing_code_is_400()
    {
        var response = await factory.CreateVisitor().PostAsJsonAsync("/api/auth/redeem", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("missing_code", await ErrorCode(response));
    }

    [Fact]
    public async Task Sixth_redeem_attempt_in_a_minute_is_rate_limited()
    {
        var visitor = factory.CreateVisitor();

        for (var attempt = 1; attempt <= RateLimits.RedeemPerMinute; attempt++)
        {
            var allowed = await visitor.PostAsJsonAsync("/api/auth/redeem", new { code = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        var blocked = await visitor.PostAsJsonAsync("/api/auth/redeem", new { code = KilowattcherFactory.InviteCode });

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal("rate_limited", await ErrorCode(blocked));
        Assert.True(blocked.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Rate_limit_is_per_visitor_ip()
    {
        var noisy = factory.CreateVisitor();
        for (var attempt = 0; attempt <= RateLimits.RedeemPerMinute; attempt++)
        {
            await noisy.PostAsJsonAsync("/api/auth/redeem", new { code = "wrong" });
        }

        var response = await factory.CreateVisitor().PostAsJsonAsync("/api/auth/redeem", new { code = KilowattcherFactory.InviteCode });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Forged_cookie_is_rejected()
    {
        var visitor = factory.CreateVisitor();
        visitor.DefaultRequestHeaders.Add("Cookie", $"{SessionTokenService.CookieName}=1.99999999999.forged");

        var response = await visitor.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("error").GetString();
    }
}
