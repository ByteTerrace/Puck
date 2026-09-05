using Puck.Physics.Motion;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: <c>$pair:&lt;bodyRefA&gt;:&lt;bodyRefB&gt;</c> addresses a keyed row's cell by a genuine, DIRECTED
/// (observer, subject) pair — "<c>a_b</c>" and "<c>b_a</c>" are two different cells — through the same
/// <c>CompiledCellRef</c> indirection every other dynamic key (<c>$cell:</c>, a bound <c>$each</c>/<c>$left</c>/
/// <c>$right</c>) resolves through, so it is admitted everywhere a key is authored.
/// </summary>
public sealed class WorldPairKeyLawTests {
    private static CellName Name(string value) => CellName.Parse(value);
    private static WorldStateRow Trust() => new(Name("trust"), CellKind.Int, Capacity: 8);
    private static WorldDefinition Document(params WorldRule[] rules) => Fixtures.BuildDocument() with {
        StateRaw = new(World: [Trust()]),
        Rules = rules,
    };
    private static long? CellValue(WorldDefinition definition, string key) =>
        WorldDefinitionRows.FindCell(WorldDefinitionRows.FindStateRow(definition.State, "trust")!.Cells, Name(key))?.Value;

    [Fact]
    public void APairKeyAddressesTheDirectedCellAndTheReversedPairIsADifferentOne() {
        var definition = Document(
            new WorldRule(Name("write-forward"), Effects: [new ActionEffect.SetState(State: "trust", Key: "$pair:body:0:body:1", Value: 5)], Mode: ActionTriggerMode.Level),
            new WorldRule(Name("write-backward"), Effects: [new ActionEffect.SetState(State: "trust", Key: "$pair:body:1:body:0", Value: 9)], Mode: ActionTriggerMode.Level)
        );

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        Assert.Equal(5L, CellValue(fixture.Server.Definition, "0_1"));
        Assert.Equal(9L, CellValue(fixture.Server.Definition, "1_0"));
    }

    [Fact]
    public void APairKeyResolvesThroughAForEachBindingAndACellIndirectionSubject() {
        // The garden's own shape: an observer bound by forEach ($each) forms a pair with a SUBJECT read live off
        // another row's cell (cell:holder:0) — the exact grammar boneHolderTrust re-authors on.
        var holder = new WorldStateRow(Name("holder"), CellKind.Int, Capacity: 1, Cells: [new WorldStateCell(Name("0"), 1)]);
        var observers = new WorldStateRow(Name("hound"), CellKind.Int, Capacity: 4,
            Cells: [new WorldStateCell(Name("0"), 1), new WorldStateCell(Name("2"), 1)]);
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new(World: [holder, observers, Trust()]),
            Rules = [new WorldRule(
                Name: Name("witness"),
                Effects: [new ActionEffect.SetState(State: "trust", Key: "$pair:each:cell:holder:0", Value: 7)],
                ForEach: "hound",
                Mode: ActionTriggerMode.Level
            )],
        };

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        // Body 0 and body 2 (the two "hound" keys) each witness the SAME subject (holder's cell reads body 1),
        // so each gets its OWN pair cell against that one subject.
        Assert.Equal(7L, CellValue(fixture.Server.Definition, "0_1"));
        Assert.Equal(7L, CellValue(fixture.Server.Definition, "2_1"));
        Assert.Null(CellValue(fixture.Server.Definition, "1_1"));
    }

    [Fact]
    public void AMalformedPairKeyRefusesByNameAndAWellFormedOneValidates() {
        var malformed = Document(new WorldRule(Name("bad"), Effects: [new ActionEffect.SetState(State: "trust", Key: "$pair:body:0", Value: 1)], Mode: ActionTriggerMode.Level));
        var control = Document(new WorldRule(Name("good"), Effects: [new ActionEffect.SetState(State: "trust", Key: "$pair:body:0:body:1", Value: 1)], Mode: ActionTriggerMode.Level));

        Assert.False(WorldDefinitionValidator.TryValidateLocally(malformed, out var reason));
        Assert.Contains(nameof(WorldRuleRefusal.PairKeyMalformed), reason, StringComparison.Ordinal);

        Assert.True(WorldDefinitionValidator.TryValidateLocally(control, out var controlReason), controlReason);
    }
}
