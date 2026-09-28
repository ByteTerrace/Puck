using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the host half of the console's table (<see cref="WorldDeferredVerbAnswers"/>): an evicted line
/// prints and counts nothing until its verdict arrives, which prints the line's own answer and counts by its real
/// outcome, once; a line forgotten past the table's memory answers once, on stderr, as an unknown outcome, and its late
/// verdict answers nothing; an echo no console line registered is never counted.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class DeferredVerbEvictionAnswerLawTests {
    internal const string EvictedVerb = "world.undo";

    private static WorldEditEcho Verdict(long correlationId, bool rejected) => new(
        Message: (rejected
            ? "undo refused: nothing to undo"
            : "dropped 1, 0 remaining"),
        Rejected: rejected,
        Kind: WorldEditEchoKind.Mutation,
        CorrelationId: correlationId
    );

    // Registers correlation 1 as the evicted verb, then the given number of later ids, each evicting one earlier line.
    internal static void RegisterAfterTheFirst(WorldDeferredVerbEchoes echoes, long later) {
        for (var id = 1L; (id <= (later + 1L)); id++) {
            _ = echoes.Register(
                correlationId: id,
                row: WorldDeferredVerbEchoes.DefaultRow,
                settlement: new CommandSettlement(),
                verb: ((id == 1L) ? EvictedVerb : "world.reset")
            );
        }
    }
    // Registers correlation 1, then enough later ids to evict it.
    internal static void EvictTheFirst(WorldDeferredVerbEchoes echoes) => RegisterAfterTheFirst(
        echoes: echoes,
        later: WorldDeferredVerbEchoes.Capacity
    );
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

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AnEvictedLineIsAnsweredAndCountedByItsLateVerdict(bool rejected) {
        var echoes = new WorldDeferredVerbEchoes();
        var registry = new CommandRegistry(modules: []);
        var answers = WorldDeferredVerbAnswers.Attach(echoes: echoes, registry: registry);

        var evicted = Captured(action: () => EvictTheFirst(echoes: echoes));

        Assert.Equal(actual: (evicted.Out, evicted.Error), expected: (string.Empty, string.Empty));
        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 0 rejected]");

        var late = Captured(action: () => answers.Answer(
            echo: Verdict(correlationId: 1L, rejected: rejected),
            row: WorldDeferredVerbEchoes.DefaultRow
        ));

        Assert.StartsWith(actualString: (rejected ? late.Error : late.Out), expectedStartString: $"[{EvictedVerb}: ");
        Assert.Equal(actual: WireErrors(registry: registry), expected: $"[wire.errors: {(rejected ? 1 : 0)} rejected]");

        // A second verdict for the answered line, and a refusal no console line registered, answer nothing.
        var again = Captured(action: () => {
            answers.Answer(echo: Verdict(correlationId: 1L, rejected: true), row: WorldDeferredVerbEchoes.DefaultRow);
            answers.Answer(echo: Verdict(correlationId: 9999L, rejected: true), row: WorldDeferredVerbEchoes.DefaultRow);
        });

        Assert.Equal(actual: (again.Out, again.Error), expected: (string.Empty, string.Empty));
        Assert.Equal(actual: WireErrors(registry: registry), expected: $"[wire.errors: {(rejected ? 1 : 0)} rejected]");
    }
    [Fact]
    public void ALineForgottenPastTheMemoryAnswersOnceAndIsNeverCountedTwice() {
        var echoes = new WorldDeferredVerbEchoes();
        var registry = new CommandRegistry(modules: []);
        var answers = WorldDeferredVerbAnswers.Attach(echoes: echoes, registry: registry);

        var forgotten = Captured(action: () => RegisterAfterTheFirst(
            echoes: echoes,
            later: (WorldDeferredVerbEchoes.Capacity + WorldDeferredVerbEchoes.EvictedMemory)
        ));

        Assert.StartsWith(actualString: forgotten.Error, expectedStartString: $"[{EvictedVerb}: unanswered");
        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 1 rejected]");

        var late = Captured(action: () => answers.Answer(
            echo: Verdict(correlationId: 1L, rejected: true),
            row: WorldDeferredVerbEchoes.DefaultRow
        ));

        Assert.Equal(actual: (late.Out, late.Error), expected: (string.Empty, string.Empty));
        Assert.Equal(actual: WireErrors(registry: registry), expected: "[wire.errors: 1 rejected]");
    }
}
