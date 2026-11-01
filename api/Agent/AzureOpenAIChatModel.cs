// The Responses API is marked experimental (OPENAI001) in OpenAI 2.14.0. The suppression is
// kept to this one adapter so an SDK change can only break this file.
#pragma warning disable OPENAI001

using System.ClientModel;
using OpenAI.Responses;

namespace Kilowattcher.Api.Agent;

/// <summary>
/// One round trip to the Azure OpenAI chat deployment through the OpenAI .NET SDK and the Azure v1
/// endpoint. Maps our AgentItems to Responses API items and back; the loop lives in AgentRunner.
/// </summary>
public sealed class AzureOpenAIChatModel : IChatModel
{
    private static readonly ResponseTextOptions JsonReply = new()
    {
        TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
            AgentReplyFormat.SchemaName,
            BinaryData.FromString(AgentReplyFormat.Schema),
            jsonSchemaFormatDescription: null,
            jsonSchemaIsStrict: true),
    };

    private readonly Lazy<(ResponsesClient Client, string Deployment)?> _client;
    private readonly ILogger<AzureOpenAIChatModel> _logger;

    public AzureOpenAIChatModel(IConfiguration configuration, ILogger<AzureOpenAIChatModel> logger)
    {
        _logger = logger;
        _client = new Lazy<(ResponsesClient, string)?>(() => CreateClient(configuration));
    }

    public async Task<ModelStep> NextAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        if (_client.Value is not { } configured)
        {
            throw new ChatModelException(ChatModelFailure.Unavailable, "Azure OpenAI is not configured.");
        }

        var (client, deployment) = configured;
        var options = new CreateResponseOptions
        {
            Model = deployment,
            Instructions = request.Instructions,
            MaxOutputTokenCount = ChatLimits.MaxOutputTokens,
            StoredOutputEnabled = false, // Don't keep visitor conversations on the Azure side.
            TextOptions = JsonReply,
            ParallelToolCallsEnabled = true,
            ToolChoice = request.AllowTools ? ResponseToolChoice.CreateAutoChoice() : ResponseToolChoice.CreateNoneChoice(),
        };

        foreach (var tool in request.Tools)
        {
            options.Tools.Add(ResponseTool.CreateFunctionTool(
                tool.Name, BinaryData.FromString(tool.ParametersSchema), strictModeEnabled: true, tool.Description));
        }

        foreach (var item in request.Items)
        {
            options.InputItems.Add(ToResponseItem(item));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AzureOpenAISettings.Timeout);

        try
        {
            ResponseResult result = await client.CreateResponseAsync(options, timeout.Token);

            var toolCalls = result.OutputItems
                .OfType<FunctionCallResponseItem>()
                .Select(c => new ToolCallItem(c.CallId, c.FunctionName, c.FunctionArguments?.ToString() ?? "{}"))
                .ToList();

            var text = toolCalls.Count == 0 ? result.GetOutputText() : null;
            if (toolCalls.Count == 0 && string.IsNullOrWhiteSpace(text))
            {
                throw new ChatModelException(ChatModelFailure.Unavailable, "The model returned neither text nor tool calls.");
            }

            return new ModelStep(toolCalls, text, result.Usage?.InputTokenCount ?? 0, result.Usage?.OutputTokenCount ?? 0);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatModelException(ChatModelFailure.Timeout, "Azure OpenAI did not answer within 30 seconds.", ex);
        }
        catch (ClientResultException ex)
        {
            // Log the status only; Azure error bodies can echo request content.
            _logger.LogWarning("Azure OpenAI returned HTTP {Status}.", ex.Status);
            var failure = ex.Status == StatusCodes.Status429TooManyRequests ? ChatModelFailure.Busy : ChatModelFailure.Unavailable;
            throw new ChatModelException(failure, $"Azure OpenAI returned HTTP {ex.Status}.", ex);
        }
    }

    private static ResponseItem ToResponseItem(AgentItem item) => item switch
    {
        UserItem user => ResponseItem.CreateUserMessageItem(user.Text),
        AssistantItem assistant => ResponseItem.CreateAssistantMessageItem(assistant.Text, []),
        ToolCallItem call => ResponseItem.CreateFunctionCallItem(call.CallId, call.Name, BinaryData.FromString(call.ArgumentsJson)),
        ToolOutputItem output => ResponseItem.CreateFunctionCallOutputItem(output.CallId, output.OutputJson),
        DeveloperNoteItem note => ResponseItem.CreateDeveloperMessageItem(note.Text),
        _ => throw new ArgumentOutOfRangeException(nameof(item), item.GetType().Name, "Unknown conversation item."),
    };

    private (ResponsesClient, string)? CreateClient(IConfiguration configuration)
    {
        var settings = AzureOpenAISettings.TryRead(configuration, AzureOpenAISettings.ChatDeploymentKey);
        if (settings is null)
        {
            _logger.LogError("Azure OpenAI settings are missing or invalid (endpoint, key, or chat deployment).");
            return null;
        }

        var options = settings.Configure(new ResponsesClientOptions { Endpoint = settings.Endpoint });
        return (new ResponsesClient(settings.Credential, options), settings.Deployment);
    }
}
