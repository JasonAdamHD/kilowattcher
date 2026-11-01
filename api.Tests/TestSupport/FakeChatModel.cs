using Kilowattcher.Api.Agent;

namespace Kilowattcher.Api.Tests.TestSupport;

public sealed class FakeChatModel : IChatModel
{
    public List<IReadOnlyList<ChatTurn>> Calls { get; } = [];

    public Func<IReadOnlyList<ChatTurn>, ChatModelReply> Respond { get; set; } =
        _ => new ChatModelReply("Hello from the fake model.", 12, 6);

    public Task<ChatModelReply> CompleteAsync(IReadOnlyList<ChatTurn> turns, CancellationToken cancellationToken)
    {
        Calls.Add(turns);
        return Task.FromResult(Respond(turns));
    }
}

public sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
