using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;
using static Puck.World.Testing.DeferredVerbFixtures;

namespace Puck.World.Tests;

/// <summary>Laws for the host half of the console's table (<see cref="WorldDeferredVerbAnswers"/>): an evicted line
/// prints and counts nothing until its verdict arrives, which prints the line's own answer and counts by its real
/// outcome, once; a line forgotten past the table's memory answers once, on stderr, as an unknown outcome, and its late
/// verdict answers nothing; an echo no console line registered is never counted.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class DeferredVerbEvictionAnswerLawTests {
    private static WorldEditEcho Verdict(long correlationId, bool rejected) => new(
        Message: (rejected
            ? "undo refused: nothing to undo"
            : "dropped 1, 0 remaining"),
        Rejected: rejected,
        Kind: WorldEditEchoKind.Mutation,
        CorrelationId: correlationId
    );

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
