using System.Numerics;

using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.World.Authoring;

using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The relief-contact review's findings (R4 to R8), each in the case the review stated: a field collider and
/// the field provider measure one certified surface, so a body touching the drawn rock is pushed out of it, and a point
/// whose field proves no direction or no surface is never read as clear.</summary>
public sealed class CertifiedFieldContactLawTests {
    private static FixedQ4816 Q(double value) => FixedQ4816.FromDouble(value: value);
    private static FixedVector3 V(double x, double y, double z) => new(X: Q(value: x), Y: Q(value: y), Z: Q(value: z));
    private static ShapeDocument Shape(int id, SdfSolidPrimitive type, Vector3 scale, SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, bool? detail = null) =>
        new(Id: id, Name: $"shape-{id}", Type: type, Position: Vector3.Zero, Rotation: Quaternion.Identity, Scale: scale, Material: 0,
            Blend: blend, Smooth: smooth, Group: 0, Detail: detail);
    private static WorldDefinition Document(IReadOnlyList<ShapeDocument> shapes, CreationNoiseDocument noise) {
        var canonical = CreationCanonicalizer.Canonicalize(document: new CreationDocument(
            Schema: CreationDocument.CurrentSchema, Name: "rock", Palette: null, Shapes: shapes, Frames: null, Noise: noise), source: "rock");
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);

        return source with {
            CreationsRaw = [new WorldPrototype(Id: "rock", Document: canonical.Document, HashRaw: canonical.Hash)],
            PlacementRowsRaw = [new WorldPlacement(Id: "rock", PrototypeId: "rock", Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f,
                Solid: new WorldSolid(Margin: 0f))],
        };
    }
    private static FixedStaticCollider Collider(WorldDefinition definition) =>
        Assert.Single(collection: WorldColliderSet.Build(definition: definition).DiagnosticColliders).Collider;
    private static FixedBodyColliderVolume Ball(double radius) => new(Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero,
        HalfExtents: FixedVector3.Zero, Kind: FixedBodyColliderKind.Sphere, Radius: Q(value: radius), Rotation: FixedQuaternion.Identity);
    private static bool Reads(FixedStaticCollider collider, FixedVector3 world, out FixedQ4816 value) =>
        collider.Field!.Field.TryDistance(distance: out value, material: out _,
            position: FixedPosition.FromLocal(local: collider.Rotation.RotateInverse(vector: (world - collider.Center))));

    // R4: value over gradient length is a first-order estimate, not a distance bound. The sphere's bottom is inside the
    // relieved plane, so the sphere is pushed.
    [Fact]
    public void ASphereWhoseBottomIsInsideTheReliefIsPushed() {
        var collider = Collider(definition: Document(
            noise: new CreationNoiseDocument(Frequency: 2f, Amplitude: 0.4f, Octaves: 3, Gain: 0.5f, Lacunarity: 2f, Seed: 7u),
            shapes: [Shape(id: 0, scale: Vector3.One, type: SdfSolidPrimitive.Plane)]));
        var centre = new FixedVector3(X: FixedQ4816.FromRawBits(value: -39322L), Y: FixedQ4816.FromRawBits(value: 7209L), Z: FixedQ4816.Zero);

        Assert.Equal(expected: -0.600006103515625d, actual: (double)centre.X);
        Assert.Equal(expected: 0.1100006103515625d, actual: (double)centre.Y);
        Assert.True(condition: Reads(collider: collider, value: out var bottom, world: (centre - V(x: 0d, y: 0.35d, z: 0d))));
        Assert.True(condition: (bottom < FixedQ4816.Zero), userMessage: $"bottom reads {(double)bottom}");
        Assert.True(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: centre, push: out var push, skin: Q(value: 0.05d),
            volume: Ball(radius: 0.35d)));
        Assert.True(condition: (push.Normal.Y > FixedQ4816.Zero));
    }

    // R5: a scope scale below the fixed-point grid must not leave the compiled field claiming a Lipschitz bound of one.
    [Fact]
    public void AScopeScaleBelowTheGridKeepsTheFieldsBound() {
        var collider = Collider(definition: Document(
            noise: new CreationNoiseDocument(Frequency: 8f, Amplitude: 4f, Octaves: 8, Gain: 1f, Lacunarity: 4f, Seed: 7u),
            shapes: [Shape(id: 0, scale: Vector3.One, type: SdfSolidPrimitive.Plane)]));

        Assert.True(condition: Reads(collider: collider, value: out var origin, world: FixedVector3.Zero));
        Assert.True(condition: Reads(collider: collider, value: out var witness, world: new FixedVector3(X: FixedQ4816.FromRawBits(value: 64L), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)));
        Assert.True(condition: (origin > FixedQ4816.Zero) && (witness < FixedQ4816.Zero), userMessage: $"{(double)origin} {(double)witness}");
        Assert.True(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: FixedVector3.Zero, push: out _, skin: Q(value: 0.05d),
            volume: Ball(radius: 0.1d)));
    }

    // R6: two coincident unit spheres joined by a smooth union of radius 0.5 reach 1.125, past their own radius; the
    // relieved collider's broadphase bound holds the composed surface.
    [Fact]
    public void TheBroadphaseBoundHoldsTheBlendsGrowth() {
        var definition = Document(
            noise: new CreationNoiseDocument(Frequency: 1f, Amplitude: 0.01f, Octaves: 1, Seed: 7u),
            shapes: [
                Shape(id: 0, scale: Vector3.One, type: SdfSolidPrimitive.Sphere),
                Shape(id: 1, blend: SdfBlendOp.SmoothUnion, scale: Vector3.One, smooth: 0.5f, type: SdfSolidPrimitive.Sphere),
            ]);
        var collider = Collider(definition: definition);
        var body = V(x: 0d, y: 1.1d, z: 0d);

        Assert.True(condition: Reads(collider: collider, value: out var inside, world: body));
        Assert.True(condition: (inside < FixedQ4816.Zero), userMessage: $"reads {(double)inside}");
        Assert.True(condition: ((collider.Center.Y + collider.WorldHalfExtent()!.Value.Y) >= Q(value: 1.115d)),
            userMessage: $"bound top {(double)(collider.Center.Y + collider.WorldHalfExtent()!.Value.Y)}");
        Assert.True(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: body, push: out _, skin: Q(value: 0.001d),
            volume: Ball(radius: 0.01d)));
    }

    // R7: a relieved creation whose shapes contact drops entirely compiles no field in either provider.
    [Fact]
    public void ARelievedCopyWithoutContactShapesCompilesNoField() {
        var definition = Document(
            noise: new CreationNoiseDocument(Frequency: 1f, Amplitude: 0.1f, Octaves: 2, Seed: 7u),
            shapes: [Shape(id: 0, detail: true, scale: Vector3.One, type: SdfSolidPrimitive.Sphere)]);
        var set = WorldColliderSet.Build(definition: definition);

        Assert.Empty(collection: set.DiagnosticColliders);
        Assert.Equal(expected: 0, actual: set.FieldCount);
        Assert.True(condition: WorldSolidField.TryBuild(definition: definition, built: out var field, reason: out var reason), userMessage: reason);
        Assert.Equal(expected: 0L, actual: field!.Census.PlacementFieldCount);
    }

    // R8: at a sphere field's centre every tap reads alike. An embedded capsule there is pushed out along a direction
    // the field measures elsewhere on its core, and a projection that finds no surface offers no anchor.
    [Fact]
    public void ASymmetricPointIsNeitherClearNorASurface() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Sphere(material: material, radius: 10f);
        var collider = FixedStaticCollider.FieldSurface(center: FixedVector3.Zero, field: new FixedColliderField(
            field: new SdfFieldEvaluator(program: builder.Build()), gradientStep: Q(value: 1d / 32d)),
            halfExtent: V(x: 10d, y: 10d, z: 10d), rotation: FixedQuaternion.Identity);
        var ball = new FixedBodyColliderVolume(Center: FixedVector3.Zero, Endpoint: FixedVector3.Zero, HalfExtents: FixedVector3.Zero,
            Kind: FixedBodyColliderKind.Sphere, Radius: Q(value: 0.35d), Rotation: FixedQuaternion.Identity);

        // A ball at the very centre: no reading measures a way out, so the solve reports it embedded and stuck, never
        // clear and never pushed along a direction the field does not measure.
        var centre = FixedFieldColliderContact.Solve(collider: in collider, orientation: FixedQuaternion.Identity, position: FixedVector3.Zero,
            skin: Q(value: 0.05d), volume: ball);

        Assert.Equal(expected: FixedFieldContactOutcome.Embedded, actual: centre.Outcome);
        Assert.True(condition: centre.Stuck);
        Assert.False(condition: centre.Pushes);
        var capsule = new FixedBodyColliderVolume(Center: V(x: 0d, y: 0.35d, z: 0d), Endpoint: V(x: 0d, y: 1.7d, z: 0d), HalfExtents: FixedVector3.Zero,
            Kind: FixedBodyColliderKind.Capsule, Radius: Q(value: 0.35d), Rotation: FixedQuaternion.Identity);

        Assert.True(condition: collider.TryGetPush(orientation: FixedQuaternion.Identity, position: V(x: 0d, y: -0.35d, z: 0d), push: out var push,
            skin: Q(value: 0.05d), volume: capsule));
        Assert.True(condition: (push.Normal.Y > Q(value: 0.99d)));
        Assert.False(condition: FixedSurfaceQuery.TryNearest(candidate: out _, colliders: [collider], dynamicColliders: [], probe: FixedVector3.Zero,
            reach: Q(value: 0.1d)));
    }
}
