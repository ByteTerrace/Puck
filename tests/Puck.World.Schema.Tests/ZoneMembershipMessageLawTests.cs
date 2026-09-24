using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>An ordered zone's refusal names the condition that failed, never the one the zone must meet: a zone of the
/// wrong kind is refused for its kind, and a zone holding a false cell is refused naming that cell.</summary>
public sealed class ZoneMembershipMessageLawTests {
    private static WorldDefinition Zone(CellKind kind, CellValue first, CellValue second) {
        var tokens = CellName.Parse(candidate: "tokens");

        return new WorldDefinition(StateRaw: new WorldStateSection(World: [
            new WorldStateRow(Capacity: 2, Cells: [new StateCell(Key: CellName.Parse(candidate: "a"), Value: CellValue.Int(value: 1L)), new StateCell(Key: CellName.Parse(candidate: "b"), Value: CellValue.Int(value: 2L))], Kind: CellKind.Int, Name: tokens),
            new WorldStateRow(Capacity: 2, Cells: [new StateCell(Key: CellName.Parse(candidate: "a"), Value: first), new StateCell(Key: CellName.Parse(candidate: "b"), Value: second)], Domain: new StateDomain.KeysOf(Ordered: true, Row: tokens), Kind: kind, Name: CellName.Parse(candidate: "pile")),
        ]));
    }
    private static string Refusal(WorldDefinition definition) => (WorldDefinitionValidator.TryValidateLocally(
        definition: definition,
        reason: out var reason
    )
        ? string.Empty
        : reason);

    [Fact]
    public void AZoneRefusalNamesTheFailingConditionAndATrueZoneIsAdmitted() {
        Assert.Contains(
            actualString: Refusal(definition: Zone(first: CellValue.Bool(value: true), kind: CellKind.Bool, second: CellValue.Bool(value: false))),
            expectedSubstring: "state row 'pile': an ordered keysOf (pile/zone) row's membership cell 'b' is false"
        );
        Assert.Contains(
            actualString: Refusal(definition: Zone(first: CellValue.Int(value: 1L), kind: CellKind.Int, second: CellValue.Int(value: 1L))),
            expectedSubstring: "state row 'pile': an ordered keysOf (pile/zone) row is kind Int"
        );
        Assert.DoesNotContain(
            actualString: Refusal(definition: Zone(first: CellValue.Bool(value: true), kind: CellKind.Bool, second: CellValue.Bool(value: true))),
            expectedSubstring: "ordered keysOf"
        );
    }
}
