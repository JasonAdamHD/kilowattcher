using System.Text.Json;
using Kilowattcher.Api.Agent;

namespace Kilowattcher.Api.Tests.TestSupport;

/// <summary>
/// Plays back scripted model steps in order and records every request it receives.
/// When the script runs out, it answers with a plain valid reply.
/// </summary>
public sealed class FakeChatModel : IChatModel
{
    private readonly Queue<Func<ModelRequest, ModelStep>> _script = new();

    public List<ModelRequest> Requests { get; } = [];

    public FakeChatModel Then(ModelStep step) => Then(_ => step);

    public FakeChatModel Then(Func<ModelRequest, ModelStep> step)
    {
        _script.Enqueue(step);
        return this;
    }

    public Task<ModelStep> NextAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        // Snapshot the items: the runner keeps appending to its own list after the call.
        Requests.Add(request with { Items = request.Items.ToList() });
        var step = _script.Count > 0 ? _script.Dequeue()(request) : Steps.Final("Hello from the fake model.");
        return Task.FromResult(step);
    }
}

public static class Steps
{
    public static ModelStep Final(
        string answer, IEnumerable<string>? citations = null, string? suggestedQuestion = null, string topic = "home_energy") =>
        Text(JsonSerializer.Serialize(new { topic, answer, citations = citations ?? [], suggestedQuestion }));

    public static ModelStep Text(string text) => new([], text, 100, 20);

    public static ModelStep Call(string name, string argumentsJson = "{}", string? callId = null) =>
        Calls((name, argumentsJson, callId));

    public static ModelStep Calls(params (string Name, string Arguments, string? CallId)[] calls) =>
        new(calls.Select((c, i) => new ToolCallItem(c.CallId ?? $"call_{c.Name}_{i}", c.Name, c.Arguments)).ToList(), null, 100, 10);

    public static ModelStep Fail(ChatModelFailure failure) => throw new ChatModelException(failure, "test");
}

public sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Allows everything unless told otherwise; records what it reviewed.</summary>
public sealed class FakeAnswerJudge : Kilowattcher.Api.Safety.IAnswerJudge
{
    public List<(string Question, string Answer)> Reviews { get; } = [];

    public Func<string, Kilowattcher.Api.Safety.JudgeVerdict> Verdict { get; set; } = _ => new(true, "none");

    public Task<Kilowattcher.Api.Safety.JudgeVerdict> ReviewAsync(string question, string answer, CancellationToken cancellationToken)
    {
        Reviews.Add((question, answer));
        return Task.FromResult(Verdict(answer));
    }
}

/// <summary>Flags nothing unless told otherwise; records what it checked.</summary>
public sealed class FakePromptShield : Kilowattcher.Api.Safety.IPromptShield
{
    public List<string> Checked { get; } = [];

    public Func<string, bool> IsAttack { get; set; } = _ => false;

    public Task<bool> IsAttackAsync(string message, CancellationToken cancellationToken)
    {
        Checked.Add(message);
        return Task.FromResult(IsAttack(message));
    }
}
