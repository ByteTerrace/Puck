using System.Numerics;
using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class EllipsoidColliderLawTests {
    [Fact]
    public void PrimitiveRotationMustLeaveADiagonalInertiaTensorWhileBodyRotationIsUnrestricted() {
        var radii = new FixedVector3(X: FixedQ4816.FromDouble(value: 0.036), Y: FixedQ4816.FromDouble(value: 0.010), Z: FixedQ4816.FromDouble(value: 0.036));
        var volume = new FixedBodyColliderVolume(FixedBodyColliderKind.Ellipsoid, FixedVector3.Zero,
            FixedVector3.Zero, radii, FixedQuaternion.Identity, FixedQ4816.Zero);
        var original = FixedWorldRigid.Compile(new WorldRigid(Mass: 0.1f), new FixedWorldCollider(Volumes: [volume]));
        var oblique = volume with { Rotation = FixedQuaternion.FromAxisAngle(FixedVector3.UnitX, FixedQ4816.FromDegrees(degrees: 35)) };

        Assert.False(condition: FixedWorldRigid.TryCompile(new WorldRigid(Mass: 0.1f), new FixedWorldCollider(Volumes: [oblique]), out _, out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "principal inertia axes");

        var turned = volume with { Rotation = FixedQuaternion.FromAxisAngle(FixedVector3.UnitX, FixedQ4816.FromDegrees(degrees: 90)) };
        var permuted = FixedWorldRigid.Compile(new WorldRigid(Mass: 0.1f), new FixedWorldCollider(Volumes: [turned]));

        Assert.InRange((((double)permuted.InverseInertiaYY) / original.InverseInertiaZZ), 0.996, 1.004);
        Assert.InRange((((double)permuted.InverseInertiaZZ) / original.InverseInertiaYY), 0.996, 1.004);
        var symmetric = volume with { Rotation = FixedQuaternion.FromAxisAngle(FixedVector3.UnitY, FixedQ4816.FromDegrees(degrees: 35)) };

        Assert.True(condition: FixedWorldRigid.TryCompile(new WorldRigid(Mass: 0.1f), new FixedWorldCollider(Volumes: [symmetric]), out _, out var symmetryReason), userMessage: symmetryReason);
    }
    [Fact]
    public void ACreationLensKeepsItsRadiiAndDerivesEllipsoidInertiaFromAuthoredMass() {
        var prototype = CreationFixtures.Prototype(CreationFixtures.Document("lens",
            [CreationFixtures.Shape(SdfSolidPrimitive.Sphere, new Vector3(x: 0.036f, y: 0.010f, z: 0.036f))]));
        var source = Fixtures.BuildDocument();
        var document = source with {
            CreationsRaw = [.. source.Creations, prototype],
            KitRowsRaw = [source.Kits[0] with {
                BodyContact = WorldBodyContactMode.Solid,
                Collider = new WorldCollider.FromCreation(PrototypeId: "lens"),
                Rigid = new WorldRigid(Mass: 0.1f),
            }],
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: document, neighbours: null, reason: out var reason), userMessage: reason);
        var collider = FixedWorldCollider.Compile(collider: document.Kits[0].Collider, creations: document.Creations)!.Value;

        Assert.Equal(FixedBodyColliderKind.Ellipsoid, collider.Volumes[0].Kind);
        var radii = collider.Volumes[0].HalfExtents;

        Assert.InRange(((double)radii.X), 0.03598, 0.03602);
        Assert.InRange(((double)radii.Y), 0.00998, 0.01002);
        var rigid = FixedWorldRigid.Compile(document.Kits[0].Rigid!, collider);
        var mass = ((double)rigid.Mass);
        var expected = (5 / (mass * ((((double)radii.Y) * ((double)radii.Y)) + (((double)radii.Z) * ((double)radii.Z)))));
        var actual = (rigid.InverseInertiaXX / Math.Pow(x: 2, y: FixedWorldRigid.Scales.InverseInertia));

        Assert.InRange(actual: (actual / expected), high: 1.001, low: 0.999);
        Assert.Equal(radii.X, rigid.BoundingRadius);
    }
}
