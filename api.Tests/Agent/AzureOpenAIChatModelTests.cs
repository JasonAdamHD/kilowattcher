using Kilowattcher.Api.Agent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kilowattcher.Api.Tests.Agent;

public class AzureOpenAIChatModelTests
{
    [Theory]
    [InlineData("https://kw.openai.azure.com/", "https://kw.openai.azure.com/openai/v1/")]
    [InlineData("https://kw.openai.azure.com", "https://kw.openai.azure.com/openai/v1/")]
    [InlineData("https://kw.openai.azure.com/openai/v1", "https://kw.openai.azure.com/openai/v1/")]
    [InlineData("https://kw.openai.azure.com/openai/v1/", "https://kw.openai.azure.com/openai/v1/")]
    public void Endpoint_is_normalised_to_the_azure_v1_path(string configured, string expected)
    {
        Assert.Equal(expected, AzureOpenAIChatModel.ToV1Endpoint(new Uri(configured)).ToString());
    }

    [Theory]
    [InlineData(null, "fake-key", "chat")]
    [InlineData("https://kw.openai.azure.com/", null, "chat")]
    [InlineData("https://kw.openai.azure.com/", "fake-key", null)]
    [InlineData("http://kw.openai.azure.com/", "fake-key", "chat")] // plain HTTP would send the key unencrypted
    [InlineData("not a url", "fake-key", "chat")]
    public async Task Missing_or_unsafe_settings_fail_as_unavailable_without_any_network_call(
        string? endpoint, string? key, string? deployment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AZURE_OPENAI_ENDPOINT"] = endpoint,
            ["AZURE_OPENAI_API_KEY"] = key,
            ["AZURE_OPENAI_CHAT_DEPLOYMENT"] = deployment,
        }).Build();
        var model = new AzureOpenAIChatModel(configuration, NullLogger<AzureOpenAIChatModel>.Instance);

        var ex = await Assert.ThrowsAsync<ChatModelException>(
            () => model.NextAsync(new ModelRequest("test", [new UserItem("hi")], [], AllowTools: true), CancellationToken.None));

        Assert.Equal(ChatModelFailure.Unavailable, ex.Failure);
    }
}
