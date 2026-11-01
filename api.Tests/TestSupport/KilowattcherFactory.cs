using System.Net.Http.Json;
using Kilowattcher.Api.Agent;
using Kilowattcher.Api.Auth;
using Kilowattcher.Api.Rag;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kilowattcher.Api.Tests.TestSupport;

/// <summary>
/// Boots the real API in memory with fake secrets and a fake model.
/// The "Testing" environment keeps user-secrets (real keys) out of every test.
/// </summary>
public sealed class KilowattcherFactory : WebApplicationFactory<Program>
{
    public const string InviteCode = "test-invite-code";

    // Fixed, obviously fake 32-byte key for tests only.
    public static readonly string SigningKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    public FakeChatModel ChatModel { get; } = new();

    public FakeEmbeddingClient Embeddings { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["INVITE_CODE_HASH"] = InviteCodeVerifier.HashCode(InviteCode),
            ["SESSION_SIGNING_KEY"] = SigningKey,
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IChatModel>(ChatModel);
            services.AddSingleton<IEmbeddingClient>(Embeddings);
            services.AddSingleton(TestIndex.Provider(TestIndex.ThreeAxes()));
        });
    }

    /// <summary>
    /// HTTPS base address so the Secure cookie is stored and sent; a unique client IP so
    /// per-IP rate limits never leak between tests.
    /// </summary>
    public HttpClient CreateVisitor()
    {
        var client = CreateHttpsClient();
        client.DefaultRequestHeaders.Add("x-real-ip", Guid.NewGuid().ToString());
        return client;
    }

    public async Task<HttpClient> CreateSignedInVisitor()
    {
        var client = CreateVisitor();
        var response = await client.PostAsJsonAsync("/api/auth/redeem", new { code = InviteCode });
        response.EnsureSuccessStatusCode();
        return client;
    }

    private HttpClient CreateHttpsClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
}
