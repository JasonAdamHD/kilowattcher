using Kilowattcher.Api.Auth;
using Kilowattcher.Api.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kilowattcher.Api.Tests.Auth;

public class SessionTokenServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _time = new(Start);

    private SessionTokenService Create(string? key = null) => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SESSION_SIGNING_KEY"] = key ?? KilowattcherFactory.SigningKey })
            .Build(),
        _time,
        NullLogger<SessionTokenService>.Instance);

    [Fact]
    public void Issued_token_validates_and_expires_after_24_hours()
    {
        var sessions = Create();

        var (token, expiresAt) = sessions.Issue();

        Assert.Equal(Start.AddHours(24), expiresAt);
        Assert.Equal(expiresAt, sessions.Validate(token));
    }

    [Fact]
    public void Token_is_still_valid_just_before_expiry()
    {
        var sessions = Create();
        var (token, _) = sessions.Issue();

        _time.Now = Start.AddHours(24).AddSeconds(-1);

        Assert.NotNull(sessions.Validate(token));
    }

    [Fact]
    public void Token_is_rejected_once_expired()
    {
        var sessions = Create();
        var (token, _) = sessions.Issue();

        _time.Now = Start.AddHours(24);

        Assert.Null(sessions.Validate(token));
    }

    [Fact]
    public void Tampered_expiry_is_rejected()
    {
        var sessions = Create();
        var (token, _) = sessions.Issue();
        var parts = token.Split('.');

        var extended = $"{parts[0]}.{long.Parse(parts[1]) + 3600}.{parts[2]}";

        Assert.Null(sessions.Validate(extended));
    }

    [Fact]
    public void Token_signed_with_a_different_key_is_rejected()
    {
        var otherKey = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
        var (token, _) = Create(otherKey).Issue();

        Assert.Null(Create().Validate(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1.2")]
    [InlineData("a.b.c")]
    [InlineData("1.2.3.4")]
    public void Malformed_tokens_are_rejected(string? token)
    {
        Assert.Null(Create().Validate(token));
    }

    [Fact]
    public void Rotating_the_signing_key_voids_existing_tokens()
    {
        var (token, _) = Create().Issue();
        var rotated = Create(Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));

        Assert.Null(rotated.Validate(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")] // 3 bytes: too short
    public void Fails_closed_with_a_missing_or_weak_key(string key)
    {
        var sessions = Create(key);

        Assert.False(sessions.IsConfigured);
        Assert.Null(sessions.Validate("1.2.3"));
        Assert.Throws<InvalidOperationException>(() => sessions.Issue());
    }
}
