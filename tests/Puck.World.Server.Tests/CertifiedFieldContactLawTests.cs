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
        Assert.True(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: centre, push: out var push, skin: Q(value: 0.05d),
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
        Assert.True(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: FixedVector3.Zero, push: out _, skin: Q(value: 0.05d),
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
        Assert.True(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: body, push: out _, skin: Q(value: 0.001d),
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

        Assert.True(condition: collider.TryGetPush(uncertain: out _, orientation: FixedQuaternion.Identity, position: V(x: 0d, y: -0.35d, z: 0d), push: out var push,
            skin: Q(value: 0.05d), volume: capsule));
        Assert.True(condition: (push.Normal.Y > Q(value: 0.99d)));
        Assert.False(condition: FixedSurfaceQuery.TryNearest(candidate: out _, colliders: [collider], dynamicColliders: [], probe: FixedVector3.Zero,
            reach: Q(value: 0.1d)));
    }

    // A program read as a field collider whose box bounds are its own evaluator's, the contact reading.
    private static FixedStaticCollider ProgramCollider(SdfProgram program) {
        var evaluator = new SdfFieldEvaluator(program: program);

        return FixedStaticCollider.FieldSurface(center: FixedVector3.Zero, field: new FixedColliderField(bounded: false, field: evaluator,
            gradientStep: Q(value: 1d / 32d)) { Bounds = evaluator }, halfExtent: FixedVector3.Zero, rotation: FixedQuaternion.Identity);
    }
    private static FixedFieldContact Solve(FixedStaticCollider collider, FixedVector3 position, FixedBodyColliderVolume volume) =>
        FixedFieldColliderContact.Solve(collider: in collider, orientation: FixedQuaternion.Identity, position: position, skin: FixedQ4816.Zero, volume: in volume);

    // Clearance rests on a true bound: a uniform scale of 1024 amplifies the point walk's rounding past any fixed value
    // error, so a sphere 128 raws inside the scaled unit sphere is never proved clear.
    [Fact]
    public void AScaledShapesRoundingNeverProvesAPenetratingBodyClear() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Scale(scale: new Vector3(value: 1024f)).Sphere(material: material, radius: 1f);
        var collider = ProgramCollider(program: builder.Build());
        var body = new FixedVector3(X: (FixedQ4816.FromInteger(value: 1024L) + FixedQ4816.FromRawBits(value: 768L)), Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
        var contact = Solve(collider: collider, position: body, volume: (Ball(radius: 0d) with { Radius = FixedQ4816.FromRawBits(value: 896L) }));

        Assert.NotEqual(expected: FixedFieldContactOutcome.Clear, actual: contact.Outcome);
    }

    // The core's pieces cover it end to end: a capsule whose top pierces a plane by eight raws is not proved clear.
    [Fact]
    public void ACapsulesUpperEndIsInsideItsOwnProof() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Plane(material: material, normal: -Vector3.UnitY, offset: (16f - (8f / 65536f)));
        var collider = ProgramCollider(program: builder.Build());
        var capsule = new FixedBodyColliderVolume(Center: FixedVector3.Zero, Endpoint: V(x: 0d, y: 14d, z: 0d), HalfExtents: FixedVector3.Zero,
            Kind: FixedBodyColliderKind.Capsule, Radius: Q(value: 2d), Rotation: FixedQuaternion.Identity);
        var contact = Solve(collider: collider, position: FixedVector3.Zero, volume: capsule);

        Assert.NotEqual(expected: FixedFieldContactOutcome.Clear, actual: contact.Outcome);
        Assert.True(condition: contact.Pushes);
        Assert.True(condition: (contact.Normal.Y < FixedQ4816.Zero));
    }

    // A union scope's scale is kept wherever a later operator reads its magnitude: a dilation after a chamfered scope
    // reads the drawn solid, which holds a small body at (2.4, 0, 0).
    [Fact]
    public void AScopeAMagnitudeReaderFollowsKeepsItsScale() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.PushField(compose: SdfBlendOp.Union);
        _ = builder.Sphere(material: material, radius: 1f);
        _ = builder.ResetPoint().Sphere(blend: SdfBlendOp.ChamferUnion, material: material, radius: 1f, smooth: 1f);
        _ = builder.PopField();
        _ = builder.Dilate(radius: 1f);
        var program = builder.Build();
        var point = FixedPosition.FromLocal(local: V(x: 2.4d, y: 0d, z: 0d));

        Assert.True(condition: new SdfFieldEvaluator(program: program).TryDistance(distance: out var contact, material: out _, position: point));
        Assert.True(condition: new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render).TryDistance(distance: out var drawn, material: out _, position: point));
        Assert.True(condition: (drawn < FixedQ4816.Zero) && (contact < FixedQ4816.Zero), userMessage: $"drawn {(double)drawn}, contact {(double)contact}");
        Assert.NotEqual(expected: FixedFieldContactOutcome.Clear, actual: Solve(collider: ProgramCollider(program: program), position: V(x: 2.4d, y: 0d, z: 0d),
            volume: Ball(radius: 0.05d)).Outcome);
    }

    // An unproved or stuck volume reaches the callers that must not act on it, through both contact paths.
    [Fact]
    public void AnUnprovedPoseIsReportedUnprovedByBothSolvers() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Translate(offset: new Vector3(x: -0.75f, y: 0f, z: 0f)).Sphere(material: material, radius: 0.1f);
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 0.75f, y: 0f, z: 0f)).Sphere(material: material, radius: 0.1f);
        var program = builder.Build();
        var evaluator = new SdfFieldEvaluator(program: program);
        var collider = ProgramCollider(program: program);
        var body = Ball(radius: 1d);

        Assert.Equal(expected: FixedFieldContactOutcome.Unproved, actual: Solve(collider: collider, position: FixedVector3.Zero, volume: body).Outcome);

        var provider = new FixedFieldContactSolver(bounds: evaluator, contactSkin: FixedQ4816.Zero, field: evaluator, gradientProbe: FixedQ4816.Zero,
            gradientUp: false, groundedThreshold: Q(value: 0.7d), maxIterations: 4, query: evaluator, sweep: new CertifiedFieldSweep(field: evaluator),
            sweepBoundsQueryBudget: FixedFieldContactSolver.DefaultSweepBoundsQueryBudget, sweepWork: new FixedContactSweepWork());
        var position = FixedVector3.Zero;
        var velocity = FixedVector3.Zero;

        Assert.True(condition: provider.ResolveSweep(orientation: FixedQuaternion.Identity, position: ref position, previousPosition: FixedVector3.Zero,
            up: FixedVector3.UnitY, velocity: ref velocity, volumes: [body]).Unproved);

        var solver = new FixedStaticContactSolver(ContactSkin: FixedQ4816.Zero, GroundedThreshold: Q(value: 0.7d), MaxIterations: 4);

        Assert.True(condition: solver.ResolveSweep(colliders: new(colliders: [collider]), dynamicColliders: [], orientation: FixedQuaternion.Identity, position: ref position,
            previousPosition: FixedVector3.Zero, up: FixedVector3.UnitY, velocity: ref velocity, volumes: [body]).Unproved);

        var sphere = new SdfProgramBuilder();
        var sphereMaterial = sphere.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = sphere.Sphere(material: sphereMaterial, radius: 10f);
        Assert.True(condition: solver.ResolveSweep(colliders: new(colliders: [ProgramCollider(program: sphere.Build())]), dynamicColliders: [], orientation: FixedQuaternion.Identity,
            position: ref position, previousPosition: FixedVector3.Zero, up: FixedVector3.UnitY, velocity: ref velocity, volumes: [Ball(radius: 0.35d)]).Unproved);
    }

    // The relief is charged only what it reaches inside the box a proof reads: a capsule half a metre above rock relieved
    // by the valley's 4 m fBm is proved clear by its first box, where charging the relief's whole reach proved nothing
    // within 4 m; resting on the rock it is a contact, never clear.
    [Fact]
    public void ACapsuleHalfAMetreAboveReliefIsProvedClear() {
        var collider = Collider(definition: Document(
            noise: new CreationNoiseDocument(Frequency: 0.02f, Amplitude: 4f, Octaves: 6, Gain: 0.5f, Lacunarity: 2f, Seed: 7u),
            shapes: [Shape(id: 0, scale: new Vector3(value: 100f), type: SdfSolidPrimitive.Sphere)]));
        var capsule = new FixedBodyColliderVolume(Center: V(x: 0d, y: 0.35d, z: 0d), Endpoint: V(x: 0d, y: 1.2d, z: 0d), HalfExtents: FixedVector3.Zero,
            Kind: FixedBodyColliderKind.Capsule, Radius: Q(value: 0.35d), Rotation: FixedQuaternion.Identity);
        var (top, bottom) = (110d, 90d);

        // The relieved surface above the sphere's pole, by bisection on the field's sign.
        for (var halving = 0; (halving < 40); halving++) {
            var middle = ((top + bottom) / 2d);

            Assert.True(condition: Reads(collider: collider, value: out var value, world: V(x: 0d, y: middle, z: 0d)));
            (top, bottom) = ((value > FixedQ4816.Zero) ? (middle, bottom) : (top, middle));
        }

        var above = FixedFieldColliderContact.Solve(collider: in collider, orientation: FixedQuaternion.Identity, position: V(x: 0d, y: (top + 0.5d), z: 0d),
            skin: Q(value: 0.02d), volume: in capsule);
        var resting = FixedFieldColliderContact.Solve(collider: in collider, orientation: FixedQuaternion.Identity, position: V(x: 0d, y: top, z: 0d),
            skin: Q(value: 0.02d), volume: in capsule);

        Assert.Equal(expected: FixedFieldContactOutcome.Clear, actual: above.Outcome);
        Assert.Equal(expected: FixedFieldContactOutcome.Contact, actual: resting.Outcome);
    }

    // One swept move a snap runs, through the static solver over fixed colliders.
    private readonly struct StaticStep(FixedStaticContactSolver solver, FixedStaticCollider[] colliders, FixedBodyColliderVolume[] volumes) : IFixedContactStep {
        public ContactResolution Sweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity) =>
            solver.ResolveSweep(colliders: new(colliders: colliders), dynamicColliders: [], orientation: FixedQuaternion.Identity, position: ref position,
                previousPosition: previousPosition, up: FixedVector3.UnitY, velocity: ref velocity, volumes: volumes);
    }
    // Two radius-1/32 spheres either side of the axis at a height, as one field collider.
    private static FixedStaticCollider SpherePair(double x, double y) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.Translate(offset: new Vector3(x: (float)x, y: (float)y, z: 0f)).Sphere(material: material, radius: (1f / 32f));
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: (float)-x, y: (float)y, z: 0f)).Sphere(material: material, radius: (1f / 32f));
        return ProgramCollider(program: builder.Build());
    }

    // A snap lowers an uncertain body only onto support that adds no uncertainty of its own. A unit sphere starts
    // unproved against one field (A, a symmetric pair it cannot measure past) and clear of another (B, below its box);
    // lowered onto a floor half a metre down, it would overlap B's pair, which its symmetry also hides, while the floor
    // grounds it. The landing's uncertainty is B's, which the start did not carry, so the snap refuses it.
    [Fact]
    public void ASnapRefusesALandingThatOverlapsAFieldClearAtItsStart() {
        var solver = new FixedStaticContactSolver(ContactSkin: FixedQ4816.Zero, GroundedThreshold: Q(value: 0.7d), MaxIterations: 4);
        var colliders = new[] {
            SpherePair(x: 0.75d, y: 0.8125d),
            SpherePair(x: 0.5d, y: -1.125d),
            FixedStaticCollider.HalfSpace(normal: FixedVector3.UnitY, point: V(x: 0d, y: -1.25d, z: 0d)),
        };
        var step = new StaticStep(colliders: colliders, solver: solver, volumes: [Ball(radius: 1d)]);
        var start = FixedVector3.Zero;
        var velocity = FixedVector3.Zero;
        var at = start;
        var startResolution = step.Sweep(position: ref at, previousPosition: in start, velocity: ref velocity);

        Assert.True(condition: startResolution.Unproved);
        Assert.Equal(expected: start, actual: at);
        Assert.False(condition: FixedStepUp.TrySnap(distance: Q(value: 0.5d), result: out var landing, start: in start, step: ref step,
            uncertain: startResolution.Uncertainty, up: FixedVector3.UnitY, velocity: FixedVector3.Zero), userMessage: $"landed at {landing.Position}");

        // Without B the same unproved start lands: the floor grounds it and nothing newly uncertain comes with it.
        var unhidden = new StaticStep(colliders: [colliders[0], colliders[2]], solver: solver, volumes: [Ball(radius: 1d)]);

        Assert.True(condition: FixedStepUp.TrySnap(distance: Q(value: 0.5d), result: out landing, start: in start, step: ref unhidden,
            uncertain: startResolution.Uncertainty, up: FixedVector3.UnitY, velocity: FixedVector3.Zero));
        Assert.Equal(expected: Q(value: -0.25d), actual: landing.Position.Y);
    }

    // A pipe subtraction's bead lies outside its subject; the relief collider's bound holds it. A creation clamps the
    // blend radius to half a unit, so the bead reaches past 1.3 on the spheres' shared seam.
    [Fact]
    public void TheBroadphaseBoundHoldsAPipeSubtractionsBead() {
        var collider = Collider(definition: Document(
            noise: new CreationNoiseDocument(Frequency: 1f, Amplitude: 0.01f, Octaves: 1, Seed: 7u),
            shapes: [
                Shape(id: 0, scale: Vector3.One, type: SdfSolidPrimitive.Sphere),
                Shape(id: 1, blend: SdfBlendOp.PipeSubtraction, scale: Vector3.One, smooth: 1f, type: SdfSolidPrimitive.Sphere),
            ]));

        Assert.True(condition: Reads(collider: collider, value: out var bead, world: V(x: 1.3d, y: 0d, z: 0d)));
        Assert.True(condition: (bead < FixedQ4816.Zero), userMessage: $"reads {(double)bead}");
        Assert.True(condition: ((collider.Center.X + collider.WorldHalfExtent()!.Value.X) >= Q(value: 1.31d)),
            userMessage: $"bound right {(double)(collider.Center.X + collider.WorldHalfExtent()!.Value.X)}");
    }
}
