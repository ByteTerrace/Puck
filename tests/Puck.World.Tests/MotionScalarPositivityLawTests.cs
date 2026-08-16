using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Pins the positivity wall on locomotion-rate authoring, both doors. A <see cref="MotionScalarEnvelope"/> bounds a
/// speed MAGNITUDE (reverse travel is its own positive scalar, e.g. <c>reverseTopSpeed</c>), so a negative endpoint
/// only ever widens the clamp past the bound's apparent intent — an authored <c>[-100, 10]</c> admits a 100 u/s
/// magnitude under a bound that reads as 10. <see cref="WorldDefinitionValidator"/> refuses that BY NAME at load.
/// The identity-side door is the same invariant one document over: an owned world's named speed-state rows feed
/// <see cref="WorldIdentity"/>'s live rates RAW at construction (no verb door runs on a load), so the validator
/// refuses a non-positive persisted value, and the property setters themselves throw — the type-level wall no
/// future caller can skip (the <c>identity.motion</c> verb door refuses the same range with a console error first).
/// </summary>
public sealed class MotionScalarPositivityLawTests {
    [Fact]
    public void NegativeEnvelopeMinRefusesByName() {
        var document = Fixtures.BuildDocument();
        var kit = document.Kits[0];
        var grounded = ((WorldMotionModel.Grounded)kit.Motion);
        var negative = document with {
            Kits = [kit with { Motion = grounded with { MoveSpeedEnvelope = new MotionScalarEnvelope(Min: -100f, Max: 10f) } }],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: negative, reason: out var reason, neighbours: null), userMessage: "a [-100, 10] envelope was expected to refuse");
        Assert.Contains(expectedSubstring: "moveSpeedEnvelope.min", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "-100", actualString: reason, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void NonNegativeEnvelopeValidates() {
        var document = Fixtures.BuildDocument();
        var kit = document.Kits[0];
        var grounded = ((WorldMotionModel.Grounded)kit.Motion);
        // Min 0 is the legitimate edge (full slowdown admitted); the kit's own moveSpeed must sit inside the bound.
        var control = document with {
            Kits = [kit with { Motion = grounded with { MoveSpeedEnvelope = new MotionScalarEnvelope(Min: 0f, Max: (grounded.MoveSpeed + 1f)) } }],
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: control, reason: out var reason, neighbours: null), userMessage: reason);
    }
    [Fact]
    public void IdentityMoveSpeedSetterThrowsOnNonPositive() {
        var identity = WorldIdentity.Pinned(name: "law", moveSpeed: Puck.Maths.FixedQ4816.FromDouble(value: 6.0), turnSpeed: Puck.Maths.FixedQ4816.FromDouble(value: 3.0), defaults: Fixtures.BuildDocument().PlayerDefaults);

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => identity.MoveSpeed = -5f);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => identity.MoveSpeed = 0f);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => identity.TurnSpeed = float.NaN);
    }
    [Fact]
    public void IdentityMoveSpeedSetterAcceptsPositive() {
        var identity = WorldIdentity.Pinned(name: "law", moveSpeed: Puck.Maths.FixedQ4816.FromDouble(value: 6.0), turnSpeed: Puck.Maths.FixedQ4816.FromDouble(value: 3.0), defaults: Fixtures.BuildDocument().PlayerDefaults);

        identity.MoveSpeed = 4.5f;

        Assert.Equal(expected: 4.5f, actual: identity.MoveSpeed, precision: 3);
    }
}
