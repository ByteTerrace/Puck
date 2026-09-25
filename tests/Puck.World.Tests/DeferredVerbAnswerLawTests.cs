using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for how a deferred verb answers over the loopback against a real server: an applied
/// <c>world.undo</c> settles its registered line with its own verdict at the tick boundary, like a refused one, and a
/// submission the loopback codec refuses is reported once, by its answer, never again on stderr beside it.</summary>
[Collection(name: ConsoleRedirectionCollection.Name)]
public sealed class DeferredVerbAnswerLawTests {
    private static void JournalOneEdit(WorldFixture fixture) {
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: new WorldStateRow(
                Name: CellName.Parse(candidate: "undoProbe"),
                Kind: CellKind.Int
            )
        ));
        fixture.Step();
        Assert.Equal(actual: fixture.Server.JournalLength, expected: 1);
    }

    [Fact]
    public void AnAppliedUndoSettlesItsLineWithItsVerdict() {
        using var fixture = Fixtures.FreshServer();
        var link = new LoopbackTransport(server: fixture.Server);
        var echoes = new WorldDeferredVerbEchoes();
        string? verdict = null;

        JournalOneEdit(fixture: fixture);
        fixture.Server.EchoTap = echo => verdict ??= echoes.Settle(echo: in echo);

        var submitted = link.SubmitUndo(
            count: 1,
            echoes: echoes,
            principal: Principal.Console,
            verb: "world.undo"
        );

        Assert.False(condition: submitted.IsError, userMessage: submitted.Output);
        fixture.Step();
        Assert.Equal(actual: verdict, expected: "[world.undo: dropped 1, 0 remaining]");
        Assert.Equal(actual: fixture.Server.JournalLength, expected: 0);
    }
    [Fact]
    public void ACodecRefusalWithAnAnswerIsReportedOnlyByThatAnswer() {
        using var fixture = Fixtures.FreshServer();
        var link = new LoopbackTransport(server: fixture.Server);
        var oversized = new string(c: 'x', count: (WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Mutation) + 1));
        var original = Console.Error;
        using var captured = new StringWriter();
        CommandResult answered;

        Console.SetError(newError: captured);
        try {
            answered = link.Submit(
                echoes: new WorldDeferredVerbEchoes(),
                mutation: new WorldMutation.RemoveKit(Principal.Console, oversized),
                verb: "edit"
            );
        } finally {
            Console.SetError(newError: original);
        }

        Assert.True(condition: answered.IsError);
        Assert.Contains(actualString: answered.Output, comparisonType: StringComparison.Ordinal, expectedSubstring: "world.transport.codec_refused");
        Assert.DoesNotContain(actualString: captured.ToString(), comparisonType: StringComparison.Ordinal, expectedSubstring: "codec refused");
    }
    /// <summary>The control: a submission whose caller takes no verdict has no answer, so stderr is its one report.</summary>
    [Fact]
    public void ACodecRefusalWithNoAnswerIsReportedOnStderr() {
        using var fixture = Fixtures.FreshServer();
        var link = new LoopbackTransport(server: fixture.Server);
        var oversized = new string(c: 'x', count: (WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Mutation) + 1));
        var original = Console.Error;
        using var captured = new StringWriter();

        Console.SetError(newError: captured);
        try {
            _ = link.Submit(mutation: new WorldMutation.RemoveKit(Principal.Console, oversized));
        } finally {
            Console.SetError(newError: original);
        }

        Assert.Contains(actualString: captured.ToString(), comparisonType: StringComparison.Ordinal, expectedSubstring: "[world.codec refused: PayloadTooLarge");
    }
}
