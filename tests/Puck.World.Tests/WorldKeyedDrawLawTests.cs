using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins a keyed draw site: one numeric sample per cell at first fill, a held re-roll of named keys, the
/// cursor accounting that makes both replay, and the refusal of a text source.</summary>
public sealed class WorldKeyedDrawLawTests {
    private static StateCell Cell(string key, long value = 0) => new(
        Name(value: key),
        CellValue.Int(value: value)
    );
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(
        document.State,
        row
    )!;
    // A mutation submission carries an operation id: the ingress door binds one to the stamped actor and the
    // canonical payload, and refuses an envelope that leaves it empty before anything composes.
    private static WorldMutationOutcome Generate(WorldFixture fixture, long sequence, IReadOnlyList<string>? keys = null) {
        WorldSubmissionResult? result = null;

        fixture.Server.Submit(
            new(
                SubmissionEnvelope.LocalConnectionId,
                0,
                sequence,
                sequence,
                Principal.Console,
                new WorldSubmissionPayload.Mutation(Value: new WorldMutation.Generate(
                    Principal.Console,
                    "dice",
                    keys
                )),
                Guid.NewGuid()
            ),
            completed => result = completed
        );
        fixture.Step();

        return Assert.IsType<WorldSubmissionResult.Mutation>(@object: result).Outcome;
    }
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
        var held = Generate(
            fixture: fixture,
            keys: ["d2", "d4"],
            sequence: 1
        );

        Assert.True(
            condition: held.Applied,
            userMessage: held.Detail
        );

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
        var replayed = Generate(
            fixture: twin,
            keys: ["d2", "d4"],
            sequence: 1
        );

        Assert.True(
            condition: replayed.Applied,
            userMessage: replayed.Detail
        );
        Assert.Equal(
            after,
            Find(
                document: twin.Server.Definition,
                row: "dice"
            ).Cells!.Select(selector: c => c.Value)
        );

        var whole = Generate(
            fixture: fixture,
            sequence: 2
        );

        Assert.True(
            condition: whole.Applied,
            userMessage: whole.Detail
        );
        Assert.Equal(
            12L,
            Find(
                document: fixture.Server.Definition,
                row: "dice"
            ).DrawCursor
        );

        // A key the tray does not carry redraws nothing, so the stream stays where the last emission left it.
        var unknown = Generate(
            fixture: fixture,
            keys: ["d9"],
            sequence: 3
        );

        Assert.True(condition: unknown.Refused);
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
                Cells: [new StateCell(Name(value: "a"), CellValue.Text(value: "")), new StateCell(Name(value: "b"), CellValue.Text(value: ""))],
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
                c.Value.AsInt,
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
