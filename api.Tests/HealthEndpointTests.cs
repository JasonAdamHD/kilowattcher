using System.Net;
using System.Text.Json;
using Kilowattcher.Api.Tests.TestSupport;

namespace Kilowattcher.Api.Tests;

public class HealthEndpointTests(KilowattcherFactory factory) : IClassFixture<KilowattcherFactory>
{
    [Fact]
    public async Task Health_returns_ok_status_without_a_session()
    {
        var response = await factory.CreateVisitor().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.Single(body.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task Root_path_is_not_served_by_the_api()
    {
        // "/" belongs to the web service on Vercel; the API only answers under /api.
        var response = await factory.CreateVisitor().GetAsync("/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
