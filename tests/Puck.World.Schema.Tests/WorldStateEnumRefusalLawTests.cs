using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A state row naming an enum the section does not declare, or naming one on a row that is not Int, is refused
/// once, by whole-document validation, at the row's path, <c>state.world[i].enum</c>, whether or not the document's
/// pools compile the state catalog before the rows are validated.</summary>
public sealed class WorldStateEnumRefusalLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static IReadOnlyList<string> Refusals(WorldStateSection state) {
        var errors = new List<string>();

        _ = WorldDefinitionValidator.TryValidateLocally(
            definition: new WorldDefinition(
                Simulation: new WorldSimulationDefaults(RateHz: 240),
                StateRaw: state
            ),
            errors: errors,
            deferred: null,
            compilation: out _
        );

        return errors;
    }

    public static TheoryData<bool> WithPools() => new(values: [false, true]);
    [MemberData(nameof(WithPools))]
    [Theory]
    public void ARowNamingAnUndeclaredEnumIsRefusedOnceAtItsEnumMember(bool pools) {
        var errors = Refusals(state: new WorldStateSection(
            World: [
                new WorldStateRow(Name(value: "turn"), CellKind.Int),
                new WorldStateRow(Name(value: "trump"), CellKind.Int, Enum: Name(value: "Suit")),
            ],
            Enums: (pools ? [new StateEnum(Name(value: "Body"), [Name(value: "First")])] : null),
            Records: (pools ? [new StateRecord(Name(value: "Item"), [new StatePoolField(Name(value: "body"), Enum: Name(value: "Body"))])] : null),
            Pools: (pools ? [new StatePool(Name(value: "items"), Name(value: "Item"), 2)] : null)
        ));
        var refusal = WorldDefinitionValidator.UndeclaredRowEnum(
            enumName: "Suit",
            path: WorldDefinitionValidator.StateRowPath(index: 1),
            row: "trump"
        );

        Assert.Equal(
            actual: refusal,
            expected: "state.world[1].enum: row 'trump' names enum 'Suit', which the state section does not declare."
        );
        Assert.Equal(
            expected: 1,
            actual: errors.Count(predicate: error => string.Equals(a: error, b: refusal, comparisonType: StringComparison.Ordinal))
        );
        Assert.DoesNotContain(
            collection: errors,
            filter: static error => (error.Contains(comparisonType: StringComparison.Ordinal, value: "'Suit'") && !error.StartsWith(comparisonType: StringComparison.Ordinal, value: "state.world[1].enum:"))
        );
    }
    [Fact]
    public void ARowThatIsNotIntCannotNameAnEnum() {
        var errors = Refusals(state: new WorldStateSection(
            World: [new WorldStateRow(Name(value: "label"), CellKind.Text, Enum: Name(value: "Suit"))],
            Enums: [new StateEnum(Name(value: "Suit"), [Name(value: "Clubs")])]
        ));

        Assert.Contains(
            collection: errors,
            filter: static error => error.StartsWith(comparisonType: StringComparison.Ordinal, value: "state.world[0].enum: row 'label' names enum 'Suit' on a Text row")
        );
    }
}
