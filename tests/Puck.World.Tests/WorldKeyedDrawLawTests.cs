using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins a keyed draw site: one numeric sample per cell at first fill, a held re-roll of named keys, the
/// cursor accounting that makes both replay, and the refusal of a text source.</summary>
public sealed class WorldKeyedDrawLawTests {
    private static StateCell Cell(string key, long value = 0) => new(
        Name(value: key),
        value
    );
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(
        document.State,
        row
    )!;
    private static StateGenerator Markov() => new(
        Source: GeneratorSource.Markov,
        Start: Name(value: "start"),
        Contexts: [
        new GeneratorContext(
                Name(value: "start"),
                [new GeneratorAlternative(
                        "hello",
                        1,
                        Name(value: "end")
                    )]
            ),
        new GeneratorContext(Name(value: "end")),
    ]
    );
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static WorldDefinition Tray() => Fixtures.BuildDocument() with {
        StateRaw = new(World: [
            new(
            Name(value: "dice"),
            CellKind.Int,
            Capacity: 5,
            Min: 1,
            Max: 6,
            Cells: Enumerable.Range(
                count: 5,
                start: 1
            ).Select(selector: i => Cell(
                key: $"d{i}",
                value: 1
            )).ToArray(),
            Draw: new Draw(
                Generator: new StateGenerator(
                    Source: GeneratorSource.UniformRange,
                    RangeMin: 1,
                    RangeMax: 6
                ),
                Timing: DrawTiming.Event
            )
        ),
        ]),
        Rules = [],
    };

    [Fact]
    public void AHeldRerollRedrawsOnlyTheNamedDiceAndAdvancesTheCursorByThatCount() {
        Assert.True(
            condition: WorldDrawBootResolver.TryResolve(
                Tray(),
                "test",
                out var rolled,
                out var reason
            ),
            userMessage: reason
        );
        var first = Find(
            document: rolled,
            row: "dice"
        ).Cells!.Select(selector: c => c.Value).ToArray();

        using var fixture = Fixtures.FreshServer(definition: rolled);

        fixture.Server.Submit(
            new(
                SubmissionEnvelope.LocalConnectionId,
                0,
                1,
                1,
                WorldPrincipal.Console,
                new WorldSubmissionPayload.Mutation(Value: new WorldMutation.Generate(
                    WorldPrincipal.Console,
                    "dice",
                    ["d2", "d4"]
                ))
            ),
            _ => { }
        );
        fixture.Step();

        var dice = Find(
            document: fixture.Server.Definition,
            row: "dice"
        );
        var after = dice.Cells!.Select(selector: c => c.Value).ToArray();

        Assert.Equal(
            7L,
            dice.DrawCursor
        );
        Assert.Equal(
            first[0],
            after[0]
        );
        Assert.Equal(
            first[2],
            after[2]
        );
        Assert.Equal(
            first[4],
            after[4]
        );

        using var twin = Fixtures.FreshServer(definition: rolled);

        twin.Server.Submit(
            new(
                SubmissionEnvelope.LocalConnectionId,
                0,
                1,
                1,
                WorldPrincipal.Console,
                new WorldSubmissionPayload.Mutation(Value: new WorldMutation.Generate(
                    WorldPrincipal.Console,
                    "dice",
                    ["d2", "d4"]
                ))
            ),
            _ => { }
        );
        twin.Step();
        Assert.Equal(
            after,
            Find(
                document: twin.Server.Definition,
                row: "dice"
            ).Cells!.Select(selector: c => c.Value)
        );

        fixture.Server.Submit(
            new(
                SubmissionEnvelope.LocalConnectionId,
                0,
                2,
                2,
                WorldPrincipal.Console,
                new WorldSubmissionPayload.Mutation(Value: new WorldMutation.Generate(
                    WorldPrincipal.Console,
                    "dice"
                ))
            ),
            _ => { }
        );
        fixture.Step();
        Assert.Equal(
            12L,
            Find(
                document: fixture.Server.Definition,
                row: "dice"
            ).DrawCursor
        );

        fixture.Server.Submit(
            new(
                SubmissionEnvelope.LocalConnectionId,
                0,
                3,
                3,
                WorldPrincipal.Console,
                new WorldSubmissionPayload.Mutation(Value: new WorldMutation.Generate(
                    WorldPrincipal.Console,
                    "dice",
                    ["d9"]
                ))
            ),
            _ => { }
        );
        fixture.Step();
        Assert.Equal(
            12L,
            Find(
                document: fixture.Server.Definition,
                row: "dice"
            ).DrawCursor
        );
    }
    [Fact]
    public void ATextSourceRefusesAKeyedSiteWhileASlotSiteStillTakesIt() {
        var keyedText = Tray() with {
            StateRaw = new(World: [
            new(
                Name(value: "names"),
                CellKind.Text,
                Capacity: 3,
                Cells: [Cell("a"), Cell("b")],
                Draw: new Draw(
                    Generator: Markov(),
                    Timing: DrawTiming.Event
                )
            ),
        ]),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: keyedText,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "text source"
        );

        var slotText = Tray() with {
            StateRaw = new(World: [
            new(
                Name(value: "name"),
                CellKind.Text,
                Draw: new Draw(
                    Generator: Markov(),
                    Timing: DrawTiming.Event
                )
            ),
        ]),
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: slotText,
                reason: out var slotReason
            ),
            userMessage: slotReason
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Tray(),
                reason: out var trayReason
            ),
            userMessage: trayReason
        );
    }
    [Fact]
    public void ATrayFillsEveryDieOnceAtFirstFillAndReplaysExactly() {
        var definition = Tray();

        Assert.True(
            condition: WorldDrawBootResolver.TryResolve(
                definition: definition,
                instanceIdentity: "test",
                reason: out var reason,
                resolved: out var rolled
            ),
            userMessage: reason
        );
        var dice = Find(
            document: rolled,
            row: "dice"
        );

        Assert.Equal(
            5L,
            dice.DrawCursor
        );
        Assert.Equal(
            new[] { "d1", "d2", "d3", "d4", "d5" },
            dice.Cells!.Select(selector: c => c.Key.Value)
        );
        Assert.All(
            dice.Cells!,
            c => Assert.InRange(
                c.Value,
                1L,
                6L
            )
        );
        Assert.True(condition: (dice.Cells!.Select(selector: c => c.Value).Distinct().Count() > 1));

        Assert.True(
            condition: WorldDrawBootResolver.TryResolve(
                definition: definition,
                instanceIdentity: "test",
                reason: out reason,
                resolved: out var again
            ),
            userMessage: reason
        );
        Assert.Equal(
            dice.Cells!.Select(selector: c => c.Value),
            Find(
                document: again,
                row: "dice"
            ).Cells!.Select(selector: c => c.Value)
        );

        Assert.True(
            condition: WorldDrawBootResolver.TryResolve(
                definition: rolled,
                instanceIdentity: "test",
                reason: out reason,
                resolved: out var resumed
            ),
            userMessage: reason
        );
        Assert.Same(
            actual: resumed,
            expected: rolled
        );
    }
}
