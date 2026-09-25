using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the host half of the pending-verb table (<see cref="WorldDeferredVerbAnswers"/>): an evicted line
/// is answered and counted once, by its eviction, even when the authority refuses it later, and the table remembers a
/// bounded number of evictions.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class DeferredVerbEvictionAnswerLawTests {
    internal const string EvictedVerb = "world.undo";

    private static WorldEditEcho Refusal(long correlationId) => new(
        Message: "undo refused: nothing to undo",
        Rejected: true,
        Kind: WorldEditEchoKind.Mutation,
        CorrelationId: correlationId
    );

    // Registers correlation 1, then enough later ids to evict it.
    internal static void EvictTheFirst(WorldDeferredVerbEchoes echoes) {
        for (var id = 1L; (id <= (WorldDeferredVerbEchoes.Capacity + 1)); id++) {
            _ = echoes.Register(correlationId: id, verb: ((id == 1L) ? EvictedVerb : "world.reset"));
        }
    }
    internal static string WireErrors(CommandRegistry registry) => registry.Submit(line: "wire.errors").Output;
    internal static (string Out, string Error) Captured(Action action) {
        var (originalOut, originalError) = (Console.Out, Console.Error);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Console.SetOut(newOut: output);
        Console.SetError(newError: error);
        try {
            action();
        } finally {
            Console.SetOut(newOut: originalOut);
            Console.SetError(newError: originalError);
        }

        return (output.ToString(), error.ToString());
    }

    [Fact]
    public void ALateRefusalOfAnEvictedLineIsNeitherPrintedNorCountedAgain() {
        var echoes = new WorldDeferredVerbEchoes();
        var registry = new CommandRegistry(modules: []);
        var answers = WorldDeferredVerbAnswers.Attach(echoes: echoes, registry: registry);

        var evicted = Captured(action: () => EvictTheFirst(echoes: echoes));

        Assert.StartsWith(actualString: evicted.Error, expectedStartString: $"[{EvictedVerb}: evicted unanswered");
        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 1 rejected]");

        var late = Captured(action: () => answers.Answer(echo: Refusal(correlationId: 1L)));

        Assert.Empty(collection: late.Out);
        Assert.Empty(collection: late.Error);
        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 1 rejected]");

        // Control: a refusal answering no registered or evicted line is still counted, and so is a second verdict for
        // the evicted id, which the first one took.
        answers.Answer(echo: Refusal(correlationId: 9999L));
        answers.Answer(echo: Refusal(correlationId: 1L));
        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 3 rejected]");
    }
    [Fact]
    public void TheTableRemembersOnlyItsLastEvictions() {
        var echoes = new WorldDeferredVerbEchoes();

        EvictTheFirst(echoes: echoes);
        for (var id = (WorldDeferredVerbEchoes.Capacity + 2L); (id <= ((2L * WorldDeferredVerbEchoes.Capacity) + WorldDeferredVerbEchoes.EvictedMemory)); id++) {
            _ = echoes.Register(correlationId: id, verb: "world.reset");
        }

        // Ids 1..(Capacity + EvictedMemory) were evicted in order, so correlation 1 has aged out and the latest is
        // remembered.
        Assert.False(condition: echoes.TryTakeEvicted(correlationId: 1L));
        Assert.True(condition: echoes.TryTakeEvicted(correlationId: ((WorldDeferredVerbEchoes.Capacity + WorldDeferredVerbEchoes.EvictedMemory) + 0L)));
    }
}
