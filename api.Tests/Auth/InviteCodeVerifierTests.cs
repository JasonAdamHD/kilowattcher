using Kilowattcher.Api.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kilowattcher.Api.Tests.Auth;

public class InviteCodeVerifierTests
{
    private static InviteCodeVerifier Create(string? hash) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["INVITE_CODE_HASH"] = hash }).Build(),
        NullLogger<InviteCodeVerifier>.Instance);

    [Fact]
    public void HashCode_is_uppercase_hex_sha256()
    {
        // SHA-256("abc"), a published test vector.
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", InviteCodeVerifier.HashCode("abc"));
    }

    [Fact]
    public void Accepts_the_matching_code()
    {
        Assert.True(Create(InviteCodeVerifier.HashCode("open-sesame")).Verify("open-sesame"));
    }

    [Fact]
    public void Accepts_a_lowercase_stored_hash_and_trims_pasted_whitespace()
    {
        var verifier = Create(InviteCodeVerifier.HashCode("open-sesame").ToLowerInvariant());

        Assert.True(verifier.Verify("  open-sesame\n"));
    }

    [Theory]
    [InlineData("wrong-code")]
    [InlineData("OPEN-SESAME")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_anything_else(string? code)
    {
        Assert.False(Create(InviteCodeVerifier.HashCode("open-sesame")).Verify(code));
    }

    [Fact]
    public void Rejects_codes_over_the_length_cap()
    {
        var longCode = new string('a', InviteCodeVerifier.MaxCodeLength + 1);

        Assert.False(Create(InviteCodeVerifier.HashCode(longCode)).Verify(longCode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("ABCD")]
    public void Fails_closed_when_the_hash_is_missing_or_malformed(string? hash)
    {
        var verifier = Create(hash);

        Assert.False(verifier.IsConfigured);
        Assert.False(verifier.Verify("anything"));
    }
}
