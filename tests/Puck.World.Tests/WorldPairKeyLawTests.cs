using Puck.State.Rules;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>$pair:&lt;bodyRefA&gt;:&lt;bodyRefB&gt;</c> addresses a keyed row's cell by a genuine, DIRECTED
/// (observer, subject) pair — "<c>a_b</c>" and "<c>b_a</c>" are two different cells — through the same
/// <c>CompiledCellRef</c> indirection every other dynamic key (<c>$cell:</c>, a bound <c>$each</c>/<c>$left</c>/
/// <c>$right</c>) resolves through, so it is admitted everywhere a key is authored.
/// </summary>
public sealed class WorldPairKeyLawTests {
    private static long? CellValue(WorldDefinition definition, string key) =>
        StateRows.FindCell(
            cells: WorldDefinitionRows.FindStateRow(
                definition.State,
                "trust"
            )!.Cells,
            key: Name(value: key)
        )?.Value.Raw;
    private static WorldDefinition Document(params WorldRule[] rules) => Fixtures.BuildDocument() with {
        StateRaw = new(World: [Trust()]),
        Rules = rules,
    };
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static WorldStateRow Trust() => new(
        Name(value: "trust"),
        CellKind.Int,
        Capacity: 8
    );

    [Fact]
    public void AWorldHostPairReadDoesNotMintAndAScopedWriteRewindsItsAdmission() {
        using var fixture = Fixtures.FreshServer();
        IRuleKey pair = new WorldPairKeyFact(key: new PairKeyFact(
            bodyA: new CompiledBodyRef(CompiledBodyRefKind.Literal, Index: 0, Row: null),
            bodyB: new CompiledBodyRef(CompiledBodyRefKind.Literal, Index: 1, Row: null)
        ));
        var before = fixture.Server.Arena.Keys.Count;

        Assert.False(condition: pair.Resolve(
            reader: fixture.Server.RuleHost,
            named: out var named
        ).IsValid);
        Assert.True(condition: named);
        Assert.Equal(expected: before, actual: fixture.Server.Arena.Keys.Count);

        var scope = fixture.Server.Arena.BeginScope();

        Assert.True(condition: pair.TryResolveForWrite(
            reader: fixture.Server.RuleHost,
            key: out var admitted,
            reason: out var reason
        ), userMessage: reason);
        Assert.True(condition: admitted.IsValid);
        Assert.Equal(expected: (before + 1), actual: fixture.Server.Arena.Keys.Count);

        fixture.Server.Arena.Rewind(mark: scope);

        Assert.Equal(expected: before, actual: fixture.Server.Arena.Keys.Count);
        Assert.False(condition: fixture.Server.Arena.Keys.TryResolve(
            name: CellName.Parse(candidate: "0_1"),
            key: out _
        ));
    }
    [Fact]
    public void AMalformedPairKeyRefusesByNameAndAWellFormedOneValidates() {
        var malformed = Document(new WorldRule(
            Name(value: "bad"),
            Effects: [new ActionEffect.SetState(
                    State: "trust",
                    Key: "$pair:body:0",
                    Value: 1
                )],
            Mode: ActionTriggerMode.Level
        ));
        var control = Document(new WorldRule(
            Name(value: "good"),
            Effects: [new ActionEffect.SetState(
                    State: "trust",
                    Key: "$pair:body:0:body:1",
                    Value: 1
                )],
            Mode: ActionTriggerMode.Level
        ));

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: malformed,
            reason: out var reason
        ));
        Assert.Contains(
            nameof(WorldRuleRefusal.PairKeyMalformed),
            reason,
            StringComparison.Ordinal
        );

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: control,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
    [Fact]
    public void APairKeyAddressesTheDirectedCellAndTheReversedPairIsADifferentOne() {
        var definition = Document(
            new WorldRule(
                Name(value: "write-forward"),
                Effects: [new ActionEffect.SetState(
                        State: "trust",
                        Key: "$pair:body:0:body:1",
                        Value: 5
                    )],
                Mode: ActionTriggerMode.Level
            ),
            new WorldRule(
                Name(value: "write-backward"),
                Effects: [new ActionEffect.SetState(
                        State: "trust",
                        Key: "$pair:body:1:body:0",
                        Value: 9
                    )],
                Mode: ActionTriggerMode.Level
            )
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        Assert.Equal(
            5L,
            CellValue(
                definition: fixture.Server.Definition,
                key: "0_1"
            )
        );
        Assert.Equal(
            9L,
            CellValue(
                definition: fixture.Server.Definition,
                key: "1_0"
            )
        );
    }
    [Fact]
    public void APairKeyResolvesThroughAForEachBindingAndACellIndirectionSubject() {
        // The garden's own shape: an observer bound by forEach ($each) forms a pair with a SUBJECT read live off
        // another row's cell (cell:holder:0) — the exact grammar boneHolderTrust re-authors on.
        var holder = new WorldStateRow(
            Name(value: "holder"),
            CellKind.Int,
            Capacity: 1,
            Cells: [new StateCell(
                    Name(value: "0"),
                    Puck.State.CellValue.Int(value: 1L)
                )]
        );
        var observers = new WorldStateRow(
            Name(value: "hound"),
            CellKind.Int,
            Capacity: 4,
            Cells: [new StateCell(
                    Name(value: "0"),
                    Puck.State.CellValue.Int(value: 1L)
                ), new StateCell(
                    Name(value: "2"),
                    Puck.State.CellValue.Int(value: 1L)
                )]
        );
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(World: [holder, observers, Trust()]),
            Rules = [new WorldRule(
                Name: Name(value: "witness"),
                Effects: [new ActionEffect.SetState(
                        State: "trust",
                        Key: "$pair:each:cell:holder:0",
                        Value: 7
                    )],
                ForEach: "hound",
                Mode: ActionTriggerMode.Level
            )],
        };

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        // Body 0 and body 2 (the two "hound" keys) each witness the SAME subject (holder's cell reads body 1),
        // so each gets its OWN pair cell against that one subject.
        Assert.Equal(
            7L,
            CellValue(
                definition: fixture.Server.Definition,
                key: "0_1"
            )
        );
        Assert.Equal(
            7L,
            CellValue(
                definition: fixture.Server.Definition,
                key: "2_1"
            )
        );
        Assert.Null(value: CellValue(
            definition: fixture.Server.Definition,
            key: "1_1"
        ));
    }
}
