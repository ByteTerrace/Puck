using System.Numerics;

using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// Pins <c>PlanarImpulse.BodyDirection</c>'s unit-length requirement. <c>WorldBody.RunInstruction</c> (the
/// <c>BodyMotionOp.PlanarImpulse</c> arm) rides the compiled direction AS AUTHORED — rotated by the body's attitude
/// and multiplied by <c>Speed</c>, never normalized — so an unnormalized direction silently rescales the impulse: an
/// author who typo'd <c>(3, 0, 4)</c> meaning <c>+X</c> at speed 10 gets a 50 u/s impulse, not a refusal.
/// <see cref="WorldDefinitionValidator"/> now refuses that BY NAME at load, the same "author states intent, the
/// engine never guesses" discipline the neighboring zero-direction refusal already applies.
/// </summary>
public sealed class PlanarImpulseUnitDirectionLawTests {
    [Fact]
    public void NonUnitBodyDirectionRefusesByName() {
        var typoed = DashDocument(bodyDirection: new Vector3(x: 3f, y: 0f, z: 4f)); // magnitude 5

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: typoed, reason: out var reason, neighbours: null), userMessage: "a magnitude-5 direction was expected to refuse");
        Assert.Contains(expectedSubstring: "bodyDirection", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "magnitude 5", actualString: reason, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void UnitBodyDirectionValidates() {
        var control = DashDocument(bodyDirection: new Vector3(x: 0f, y: 0f, z: 1f));

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: control, reason: out var reason, neighbours: null), userMessage: reason);
    }

    /// <summary>Builds the shared fixture document with a "dash" composition channel and a one-effect PlanarImpulse
    /// action wired to the traveler kit's onPress — the smallest shape that reaches <c>ValidateEffect</c>'s
    /// <c>PlanarImpulse</c> arm.</summary>
    private static WorldDefinition DashDocument(Vector3 bodyDirection) {
        var document = Fixtures.BuildDocument();
        var dashChannel = new WorldChannel(Name: "dash", Shape: ChannelShape.Binary, Composition: true);
        var dashAction = new ActionSpec(
            OnPress: new ActionTrigger(
                Gate: null,
                LatchSeconds: 0f,
                Effects: [new ActionEffect.PlanarImpulse(BodyDirection: bodyDirection, Speed: 10f, DurationSeconds: 0.2f)]
            ),
            OnRelease: null
        );

        return document with {
            Channels = [.. document.Channels, dashChannel],
            Kits = [document.Kits[0] with { Actions = new Dictionary<string, ActionSpec> { ["dash"] = dashAction } }],
        };
    }
}
