using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>An extended draw site rides the same state-hash and journal contract an ordinary draw site does: nothing
/// about it is persisted runtime state outside the document, so two independent boots of the identical document hash
/// identically, and <c>world.undo</c> rewinds it exactly as it rewinds any other draw.</summary>
public sealed class GeneratorExtendedWorldLawTests {
    private static readonly WorldPrincipal Actor = WorldPrincipal.Seat(slot: 0);

    // WorldServer never resolves a first-fill draw itself (WorldDefinitionLoader — the real file-loading path —
    // does, once, at process boot); Fixtures.FreshServer round-trips the document as-authored. A law over a draw
    // site therefore resolves it explicitly first, at the same instance identity ("boot") the fixture's own
    // WorldServer constructor defaults to, so a live redraw's seed ladder agrees with the settled boot value.
    private static WorldDefinition BuildDocument() {
        var row = new WorldStateRow(
            Name: CellName.Parse(candidate: "extended-roll"),
            Kind: CellKind.Int,
            Draw: new Draw(
                Generator: new StateGenerator(
                    Source: GeneratorSource.StreamDraw,
                    Extended: new GeneratorExtended(
                        K: 8,
                        Script: [12345L]
                    )
                ),
                Timing: DrawTiming.Event
            )
        );
        var authored = Fixtures.BuildDocument().WithWorldState(rows: [row]);

        Assert.True(
            condition: WorldDrawBootResolver.TryResolve(
                definition: authored,
                instanceIdentity: "boot",
                reason: out var reason,
                resolved: out var resolved
            ),
            userMessage: reason
        );

        return resolved;
    }
    private static (long Cursor, long Value) ReadSlot(WorldDefinition definition) {
        var row = WorldDefinitionRows.FindStateRow(
            rows: definition.State,
            name: "extended-roll"
        )!;

        return (row.DrawCursor, row.Cells![0].Value.Raw);
    }

    [Fact]
    public void AuthoritativeScope_IsStableAcrossTwoBoots_WithAnExtendedDrawSite() {
        var definition = BuildDocument();

        using var left = Fixtures.FreshServer(definition: definition);
        using var right = Fixtures.FreshServer(definition: definition);

        var (leftCursor, leftValue) = ReadSlot(definition: left.Server.Definition);
        var (rightCursor, rightValue) = ReadSlot(definition: right.Server.Definition);

        // The scripted first sample settled at boot, identically on both sides.
        Assert.Equal(
            actual: leftValue,
            expected: 12345L
        );
        Assert.Equal(
            actual: rightCursor,
            expected: leftCursor
        );
        Assert.Equal(
            actual: rightValue,
            expected: leftValue
        );
        Assert.Equal(
            expected: WorldStateHashComposition.HashAuthoritative(
                server: left.Server,
                tick: 0UL
            ),
            actual: WorldStateHashComposition.HashAuthoritative(
                server: right.Server,
                tick: 0UL
            )
        );
    }
    [Fact]
    public void Undo_PastAnExtendedDraw_RestoresTheCursorAndTheNextDraw() {
        using var fixture = Fixtures.FreshServer(definition: BuildDocument());

        var (bootCursor, bootValue) = ReadSlot(definition: fixture.Server.Definition);

        Assert.Equal(
            actual: bootValue,
            expected: 12345L
        );

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.Generate(
            Principal: Actor,
            Row: "extended-roll"
        ));
        fixture.Step();

        var (redrawnCursor, redrawnValue) = ReadSlot(definition: fixture.Server.Definition);

        Assert.Equal(
            actual: redrawnCursor,
            expected: (bootCursor + 1L)
        );

        fixture.Server.EnqueueUndo(
            count: 1,
            principal: WorldPrincipal.Console
        );
        fixture.Step();

        var (undoneCursor, undoneValue) = ReadSlot(definition: fixture.Server.Definition);

        Assert.Equal(
            actual: undoneCursor,
            expected: bootCursor
        );
        Assert.Equal(
            actual: undoneValue,
            expected: bootValue
        );

        // The next draw after the undo reproduces exactly what the undone draw produced — the cursor, not a
        // persisted extended-generator table, is the whole of a save/reload/undo proof.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.Generate(
            Principal: Actor,
            Row: "extended-roll"
        ));
        fixture.Step();

        var (replayedCursor, replayedValue) = ReadSlot(definition: fixture.Server.Definition);

        Assert.Equal(
            actual: replayedCursor,
            expected: redrawnCursor
        );
        Assert.Equal(
            actual: replayedValue,
            expected: redrawnValue
        );
    }
}
