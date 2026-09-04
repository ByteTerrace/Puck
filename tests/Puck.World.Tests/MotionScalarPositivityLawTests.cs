using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Pins the positivity wall on locomotion-rate authoring, both doors. A <see cref="MotionScalarEnvelope"/> bounds a
/// speed MAGNITUDE (backward travel is its own non-negative scalar, an anisotropic row's <c>along.backwardSpeed</c>), so a negative endpoint
/// only ever widens the clamp past the bound's apparent intent — an authored <c>[-100, 10]</c> admits a 100 u/s
/// magnitude under a bound that reads as 10. <see cref="WorldDefinitionValidator"/> refuses that BY NAME at load.
/// The identity-side door is the same invariant one document over: an owned world's named speed-state rows feed
/// <see cref="WorldIdentity"/>'s live rates RAW at construction (no verb door runs on a load), so the validator
/// refuses a non-positive persisted value, and the rate setters themselves throw — the type-level wall no
/// future caller can skip (the <c>identity.motion</c> verb door refuses the same range with a console error first).
/// An ABSENT rate row is legal and different: the identity claims no rate, and the kit's own drives the seat.
/// </summary>
public sealed class MotionScalarPositivityLawTests {
    [Fact]
    public void NegativeEnvelopeMinRefusesByName() {
        var document = Fixtures.BuildDocument();
        var kit = document.Kits[0];
        var motion = kit.Motion;
        var negative = document with {
            KitRowsRaw = [kit with { Motion = motion with { Speed = motion.Speed with { Envelope = new MotionScalarEnvelope(Max: 10f, Min: -100f) } } }],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: negative, neighbours: null, reason: out var reason), userMessage: "a [-100, 10] envelope was expected to refuse");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "speed.envelope.min");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "-100");
    }
    [Fact]
    public void NonNegativeEnvelopeValidates() {
        var document = Fixtures.BuildDocument();
        var kit = document.Kits[0];
        var motion = kit.Motion;
        // Min 0 is the legitimate edge (full slowdown admitted); the kit's own speed.value must sit inside the bound.
        var control = document with {
            KitRowsRaw = [kit with { Motion = motion with { Speed = motion.Speed with { Envelope = new MotionScalarEnvelope(Min: 0f, Max: (motion.Speed.Value + 1f)) } } }],
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: control, neighbours: null, reason: out var reason), userMessage: reason);
    }
    [Fact]
    public void IdentityMoveSpeedSetterThrowsOnNonPositive() {
        var identity = WorldIdentity.Pinned(name: "law", moveSpeed: Puck.Maths.FixedQ4816.FromDouble(value: 6.0), turnSpeed: Puck.Maths.FixedQ4816.FromDouble(value: 3.0), defaults: Fixtures.BuildDocument().PlayerDefaults);

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => identity.SetMoveSpeed(value: -5f));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => identity.SetMoveSpeed(value: 0f));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => identity.SetTurnSpeed(value: float.NaN));
    }
    [Fact]
    public void IdentityMoveSpeedSetterAcceptsPositive() {
        var identity = WorldIdentity.Pinned(name: "law", moveSpeed: Puck.Maths.FixedQ4816.FromDouble(value: 6.0), turnSpeed: Puck.Maths.FixedQ4816.FromDouble(value: 3.0), defaults: Fixtures.BuildDocument().PlayerDefaults);

        identity.SetMoveSpeed(value: 4.5f);

        Assert.NotNull(@object: identity.FixedMoveSpeed);
        Assert.Equal(expected: 4.5f, actual: ((float)((double)identity.FixedMoveSpeed!.Value)), precision: 3);
    }
}
