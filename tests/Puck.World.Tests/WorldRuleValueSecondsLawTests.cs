using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins <c>setState</c>'s <c>valueSeconds</c> boundary: a duration past the signed 64-bit engine-tick
/// carrier refuses by name rather than overflowing.</summary>
public sealed class WorldRuleValueSecondsLawTests {
    private static WorldDefinition DurationDocument(decimal seconds) {
        var countdownName = CellName.Parse(candidate: "cooldown");

        return Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                Name: countdownName,
                Kind: CellKind.Int,
                Min: 0L,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 0L)
                    )]
            )
            ]),
            Rules = [
                new WorldRule(
                Name: CellName.Parse(candidate: "arm-cooldown"),
                Effects: [new ActionEffect.SetState(
                        State: countdownName.Value,
                        ValueSeconds: seconds
                    )]
            )
            ],
        };
    }

    [Fact]
    public void OutOfRangeValueSecondsRefusesByNameWithoutOverflow() {
        var denied = DurationDocument(seconds: decimal.MaxValue);
        var control = DurationDocument(seconds: 1m);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(
            definition: denied,
            neighbours: null,
            reason: out var reason
        ));
        Assert.Contains(
            expectedSubstring: nameof(RuleRefusal.DurationEngineTicksOutOfRange),
            actualString: reason,
            comparisonType: StringComparison.Ordinal
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(
                definition: control,
                neighbours: null,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }
}
