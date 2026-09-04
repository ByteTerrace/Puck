using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins a keyed draw site: one numeric sample per cell at first fill, a held re-roll of named keys, the
/// cursor accounting that makes both replay, and the refusal of a text source.</summary>
public sealed class WorldKeyedDrawLawTests {
    [Fact]
    public void ATrayFillsEveryDieOnceAtFirstFillAndReplaysExactly() {
        var definition = Tray();

        Assert.True(WorldDrawBootResolver.TryResolve(definition, "test", out var rolled, out var reason), reason);
        var dice = Find(rolled, "dice");
        Assert.Equal(5L, dice.DrawCursor);
        Assert.Equal(new[] { "d1", "d2", "d3", "d4", "d5" }, dice.Cells!.Select(c => c.Key.Value));
        Assert.All(dice.Cells!, c => Assert.InRange(c.Value, 1L, 6L));
        Assert.True(dice.Cells!.Select(c => c.Value).Distinct().Count() > 1);

        Assert.True(WorldDrawBootResolver.TryResolve(definition, "test", out var again, out reason), reason);
        Assert.Equal(dice.Cells!.Select(c => c.Value), Find(again, "dice").Cells!.Select(c => c.Value));

        Assert.True(WorldDrawBootResolver.TryResolve(rolled, "test", out var resumed, out reason), reason);
        Assert.Same(rolled, resumed);
    }

    [Fact]
    public void AHeldRerollRedrawsOnlyTheNamedDiceAndAdvancesTheCursorByThatCount() {
        Assert.True(WorldDrawBootResolver.TryResolve(Tray(), "test", out var rolled, out var reason), reason);
        var first = Find(rolled, "dice").Cells!.Select(c => c.Value).ToArray();

        using var fixture = Fixtures.FreshServer(definition: rolled);
        fixture.Server.Submit(new(SubmissionEnvelope.LocalConnectionId, 0, 1, 1, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.Generate(WorldPrincipal.Console, "dice", ["d2", "d4"]))), _ => { });
        fixture.Step();

        var dice = Find(fixture.Server.Definition, "dice");
        var after = dice.Cells!.Select(c => c.Value).ToArray();
        Assert.Equal(7L, dice.DrawCursor);
        Assert.Equal(first[0], after[0]);
        Assert.Equal(first[2], after[2]);
        Assert.Equal(first[4], after[4]);

        using var twin = Fixtures.FreshServer(definition: rolled);
        twin.Server.Submit(new(SubmissionEnvelope.LocalConnectionId, 0, 1, 1, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.Generate(WorldPrincipal.Console, "dice", ["d2", "d4"]))), _ => { });
        twin.Step();
        Assert.Equal(after, Find(twin.Server.Definition, "dice").Cells!.Select(c => c.Value));

        fixture.Server.Submit(new(SubmissionEnvelope.LocalConnectionId, 0, 2, 2, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.Generate(WorldPrincipal.Console, "dice"))), _ => { });
        fixture.Step();
        Assert.Equal(12L, Find(fixture.Server.Definition, "dice").DrawCursor);

        fixture.Server.Submit(new(SubmissionEnvelope.LocalConnectionId, 0, 3, 3, WorldPrincipal.Console,
            new WorldSubmissionPayload.Mutation(new WorldMutation.Generate(WorldPrincipal.Console, "dice", ["d9"]))), _ => { });
        fixture.Step();
        Assert.Equal(12L, Find(fixture.Server.Definition, "dice").DrawCursor);
    }

    [Fact]
    public void ATextSourceRefusesAKeyedSiteWhileASlotSiteStillTakesIt() {
        var keyedText = Tray() with { StateRaw = new(World: [
            new(Name("names"), CellKind.Text, Capacity: 3, Cells: [Cell("a"), Cell("b")], Draw: new WorldDraw(Generator: Markov(), Timing: WorldDrawTiming.Event)),
        ]) };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(keyedText, out var reason));
        Assert.Contains("text source", reason);

        var slotText = Tray() with { StateRaw = new(World: [
            new(Name("name"), CellKind.Text, Draw: new WorldDraw(Generator: Markov(), Timing: WorldDrawTiming.Event)),
        ]) };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(slotText, out var slotReason), slotReason);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(Tray(), out var trayReason), trayReason);
    }

    private static WorldGenerator Markov() => new(Source: WorldGeneratorSource.Markov, Start: Name("start"), Contexts: [
        new WorldGeneratorContext(Name("start"), [new WorldGeneratorAlternative("hello", 1, Name("end"))]),
        new WorldGeneratorContext(Name("end")),
    ]);
    private static WorldDefinition Tray() => Fixtures.BuildDocument() with {
        StateRaw = new(World: [
            new(Name("dice"), CellKind.Int, Capacity: 5, Min: 1, Max: 6,
                Cells: Enumerable.Range(1, 5).Select(i => Cell($"d{i}", 1)).ToArray(),
                Draw: new WorldDraw(Generator: new WorldGenerator(Source: WorldGeneratorSource.UniformRange, RangeMin: 1, RangeMax: 6), Timing: WorldDrawTiming.Event)),
        ]),
        Rules = [],
    };
    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldStateCell Cell(string key, long value = 0) => new(Name(key), value);
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(document.State, row)!;
}
