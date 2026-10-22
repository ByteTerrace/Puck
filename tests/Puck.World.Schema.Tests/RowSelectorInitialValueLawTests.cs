using Puck.Physics.Motion;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A selector case naming its register's <c>initial</c> value selects every body, since a <c>state.body</c>
/// register is document-wide and each body holds it until something writes it. The validator refuses that case by
/// name; the same selector naming a deviating value admits.</summary>
public sealed class RowSelectorInitialValueLawTests {
    private static WorldDefinition Document(long caseValue) {
        var source = Fixtures.BuildDocument();
        var kit = source.Kits[0];

        return source with {
            KitsRaw = new WorldKitsSection(
                Assignment: (source.Assignment with {
                    Select = new WorldRowSelector(
                        BodyState: "suit",
                        Cases: [new WorldRowSelectorCase(Value: caseValue, Row: kit.Name)]
                    ),
                }),
                Rows: [kit]
            ),
            StateRaw = ((source.StateRaw ?? new WorldStateSection()) with {
                Body = [new ActionStateSlot(
                    Name: "suit",
                    Kind: ActionStateKind.Counter,
                    Initial: 3f
                )],
            }),
        };
    }

    [Fact]
    public void ACaseNamingTheInitialValueIsRefusedAndADeviatingOneAdmits() {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(caseValue: 3L),
            reason: out var reason
        ));
        Assert.Contains(
            expectedSubstring: "kits.assignment.select.cases[0] value 3 is register 'suit' initial value",
            actualString: reason
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Document(caseValue: 0L),
                reason: out reason
            ),
            userMessage: reason
        );
    }
}
