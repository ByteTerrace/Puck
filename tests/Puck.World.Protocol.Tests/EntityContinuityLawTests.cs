using System.Numerics;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>Laws for combining two pose changes no client saw between (<see cref="EntityContinuity.Then"/>), as a
/// projection that skips snapshots must: two in-place crossings compose into the one rigid motion that moves the body
/// through both, a crossing keeps an earlier snap, and an ordinary step changes nothing.</summary>
public sealed class EntityContinuityLawTests {
    private static readonly WorldPoseCarry Lift = new(
        Rotation: Quaternion.Identity,
        Translation: new Vector3(x: 0f, y: 6f, z: 0f)
    );
    // A half turn through a door onto one a quarter its size.
    private static readonly WorldPoseCarry HalfTurn = new(
        Rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: MathF.PI),
        Translation: new Vector3(x: 1f, y: 0f, z: 0f),
        Scale: 0.25f
    );

    [Fact]
    public void TwoCrossingsComposeIntoOneCarry() {
        var both = EntityContinuity.Carry(carried: Lift).Then(later: EntityContinuity.Carry(carried: HalfTurn));
        var point = new Vector3(x: 2f, y: 3f, z: 4f);

        Assert.Equal(expected: EntityContinuityKind.Carry, actual: both.Kind);
        Assert.True(condition: (Vector3.Distance(
            value1: both.Carried!.Value.Point(point: point),
            value2: HalfTurn.Point(point: Lift.Point(point: point))
        ) < 1e-5f));
    }
    [Fact]
    public void AnOrdinaryStepKeepsWhatCameBeforeAndACrossingKeepsAnEarlierSnap() {
        var carry = EntityContinuity.Carry(carried: Lift);

        Assert.Equal(expected: carry, actual: carry.Then(later: EntityContinuity.Continuous));
        Assert.Equal(expected: EntityContinuity.Teleport, actual: EntityContinuity.Teleport.Then(later: carry));
        Assert.Equal(expected: carry, actual: EntityContinuity.Continuous.Then(later: carry));
        Assert.Equal(expected: EntityContinuity.Teleport, actual: carry.Then(later: EntityContinuity.Teleport));
    }
}
