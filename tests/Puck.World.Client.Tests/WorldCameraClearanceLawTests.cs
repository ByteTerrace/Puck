using System.Numerics;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>The chase eye sees its body from outside and comes back to its boom. Running the World cannot show where the
/// eye stands relative to the body's own volume, nor that an arm pulled in by geometry is not kept as the new boom, so
/// these laws resolve the eye frame by frame against walls and a body whose collider is the debug world's Moth: a
/// capsule of radius 0.35 standing 2.06 m tall, framed from 1 m above its root.</summary>
public sealed class WorldCameraClearanceLawTests {
    // The debug world's camera-boom row: 4 Hz, critically damped.
    private static readonly SdfCameraDynamics Boom = new(Frequency: 4f, Damping: 1f, Response: 0f);
    private const float Frame = (1f / 60f);
    private static readonly WorldCollider.Capsule MothCollider = new(
        Endpoint: new DocumentVector3(x: 0f, y: 1.36f, z: 0f),
        Radius: 0.35f
    );
    private static readonly Vector3 Target = new(x: 0f, y: 1f, z: 0f);

    private static FixedPosition At(Vector3 value) => FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: value));
    private static WorldChaseBody Body(WorldCollider? collider = null, float scale = 1f, Quaternion? orientation = null, ReadOnlyMemory<Vector4> drawn = default) => new(
        collider: FixedWorldCollider.Compile(
            collider: (collider ?? MothCollider),
            creations: []
        ),
        drawn: drawn,
        orientation: (orientation ?? Quaternion.Identity),
        position: Vector3.Zero,
        scale: scale
    );
    // The distance from a point to the Moth's capsule surface, negative inside it.
    private static float CapsuleDistance(Vector3 point) {
        var axisY = Math.Clamp(
            max: 1.71f,
            min: 0.35f,
            value: point.Y
        );

        return ((point - new Vector3(x: 0f, y: axisY, z: 0f)).Length() - 0.35f);
    }
    private static Vector3 Desired(float yaw, float pitch, float distance = 4.2f) => (Target + OrbitRig.Offset(
        distance: distance,
        pitch: pitch,
        yaw: yaw
    ));
    private static float FieldDistance(SdfFieldEvaluator field, Vector3 point) {
        Assert.True(condition: field.TryDistance(
            distance: out var distance,
            material: out _,
            position: At(value: point)
        ));

        return ((float)((double)distance));
    }
    private static SdfFieldEvaluator Walls(params (Vector3 Center, Vector3 HalfExtents)[] boxes) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: 0.5f)));

        foreach (var (center, halfExtents) in boxes) {
            builder.ResetPoint();
            builder.Translate(offset: center);
            builder.Box(
                halfExtents: halfExtents,
                material: material,
                round: 0f
            );
        }

        return new SdfFieldEvaluator(program: builder.Build());
    }
    // Resolves the same pose for a run of frames on the boom dynamics, returning the last eye.
    private static Vector3 Run(WorldCameraClearance arm, SdfFieldEvaluator? field, Vector3 desired, WorldChaseBody body, int frames, SdfCameraDynamics? dynamics = null) {
        var eye = desired;

        for (var frame = 0; (frame < frames); frame++) {
            eye = arm.Resolve(
                deltaSeconds: Frame,
                desiredEye: desired,
                dynamics: (dynamics ?? Boom),
                field: field,
                subject: body,
                target: Target,
                up: Vector3.UnitY
            );
        }

        return eye;
    }
    // The boom's heading in the level plane: what the clearance may never change.
    private static float Heading(Vector3 eye, Vector3 target) => MathF.Atan2(
        x: (eye.Z - target.Z),
        y: (eye.X - target.X)
    );

    /// <summary>A ledge close behind the body, the boom pointing into it, raises the eye over the body on the same
    /// heading, outside the body by the eye's radius, clear of the ledge with a clear sightline, no steeper than the
    /// lift ceiling. With the ledge gone the arm eases back down and out to the authored boom and stands there: the
    /// pull-in is never kept as the boom, and no frame on the way jumps.</summary>
    [Fact]
    public void ALedgeRaisesTheEyeOverTheBodyAndTheArmEasesBackOutWhenItClears() {
        var ledge = Walls((new Vector3(x: 0f, y: 0.6f, z: 0.7f), new Vector3(x: 4f, y: 0.6f, z: 0.1f)));
        var open = Walls((new Vector3(x: 0f, y: -10f, z: 0f), new Vector3(x: 1f, y: 1f, z: 1f)));
        var desired = Desired(
            pitch: 0.3f,
            yaw: 0f
        );
        var arm = new WorldCameraClearance();
        var eye = Run(arm: arm, body: Body(), desired: desired, field: ledge, frames: 60);
        var boom = (eye - Target);

        Assert.Equal(expected: WorldCameraClearanceMode.Risen, actual: arm.Mode);
        Assert.True(condition: (CapsuleDistance(point: eye) >= (WorldCameraClearance.EyeRadius - 0.01f)), userMessage: $"eye {eye} is within the eye radius of the body");
        Assert.True(condition: (FieldDistance(field: ledge, point: eye) >= 0f), userMessage: $"eye {eye} is inside the ledge");
        Assert.True(condition: ledge.LineOfSight(from: At(value: eye), to: At(value: Target)));
        Assert.True(condition: (MathF.Asin(x: (boom.Y / boom.Length())) > 0.5f), userMessage: $"eye {eye} did not rise over the ledge");
        Assert.True(condition: (MathF.Asin(x: (boom.Y / boom.Length())) <= (WorldCameraClearance.LiftCeiling + 1e-3f)));
        Assert.Equal(expected: Heading(eye: desired, target: Target), actual: Heading(eye: eye, target: Target), tolerance: 1e-3f);

        var previous = eye;

        for (var frame = 0; (frame < 90); frame++) {
            eye = Run(arm: arm, body: Body(), desired: desired, field: open, frames: 1);
            Assert.True(condition: (Vector3.Distance(value1: eye, value2: previous) < 0.6f), userMessage: $"frame {frame} jumped from {previous} to {eye}");
            previous = eye;
        }

        Assert.Equal(expected: WorldCameraClearanceMode.Open, actual: arm.Mode);
        Assert.Equal(expected: 0f, actual: arm.Near);
        Assert.True(condition: (Vector3.Distance(value1: desired, value2: eye) < 1e-3f), userMessage: $"eye {eye} did not ease back out to {desired}");
        Assert.Contains(expectedSubstring: "clearance=open", actualString: arm.Describe());
    }
    /// <summary>The eye never stands inside the body: a boom shorter than the body is pushed out of its collider and
    /// out of the shapes it is drawn with, a body backed flat against a wall with no rise that clears keeps the eye
    /// outside her rather than inside the wall's side of her, and a body with neither keeps the eye out of nothing.
    /// With no dynamics the eye lands on each frame's answer, and a boom with no length passes through.</summary>
    [Fact]
    public void TheEyeNeverStandsInsideTheBodyOrTheShapesItIsDrawnWith() {
        var shortBoom = Desired(distance: 0.2f, pitch: 0f, yaw: 0f);
        var arm = new WorldCameraClearance();
        var pushed = Run(arm: arm, body: Body(), desired: shortBoom, dynamics: SdfCameraDynamics.None, field: null, frames: 1);

        Assert.Equal(expected: (0.35f + WorldCameraClearance.EyeRadius), actual: (pushed - Target).Length(), tolerance: 1e-4f);

        // A drawn pack behind her reaches past the collider: the eye clears the pack, not just the capsule.
        ReadOnlyMemory<Vector4> pack = new[] { new Vector4(x: 0f, y: 1.1f, z: 0.4f, w: 0.3f) };
        var cleared = Run(arm: new WorldCameraClearance(), body: Body(drawn: pack), desired: shortBoom, field: null, frames: 1);

        Assert.True(condition: (Vector3.Distance(value1: cleared, value2: new Vector3(x: 0f, y: 1.1f, z: 0.4f)) >= (0.3f + WorldCameraClearance.EyeRadius - 1e-3f)), userMessage: $"eye {cleared} is within the eye radius of the pack");
        Assert.True(condition: (CapsuleDistance(point: cleared) >= (WorldCameraClearance.EyeRadius - 0.01f)), userMessage: $"eye {cleared} is within the eye radius of the body");

        var wall = Walls((new Vector3(x: 0f, y: 1.5f, z: 0.45f), new Vector3(x: 4f, y: 1.5f, z: 0.05f)));
        var crowded = new WorldCameraClearance();
        var backed = Run(arm: crowded, body: Body(), desired: Desired(pitch: 0.3f, yaw: 0f), field: wall, frames: 60);

        Assert.Equal(expected: WorldCameraClearanceMode.Crowded, actual: crowded.Mode);
        Assert.True(condition: (CapsuleDistance(point: backed) >= (WorldCameraClearance.EyeRadius - 0.01f)), userMessage: $"eye {backed} is within the eye radius of the body");

        // The wall between that eye and her is cut away by the near plane, which stops short of her.
        var sight = Vector3.Normalize(value: (Target - backed));

        Assert.True(condition: (crowded.Near > 0f), userMessage: $"eye {backed} looks at her through the wall");
        Assert.True(condition: (FieldDistance(field: wall, point: (backed + (sight * crowded.Near))) >= -0.06f), userMessage: $"the image begins inside the wall, {crowded.Near} m from {backed}");
        Assert.True(condition: (CapsuleDistance(point: (backed + (sight * crowded.Near))) > 0f), userMessage: $"the near plane at {crowded.Near} m cuts into her");

        var none = new WorldChaseBody(collider: null, orientation: Quaternion.Identity, position: Vector3.Zero, scale: 1f);

        Assert.Equal(expected: shortBoom, actual: Run(arm: new WorldCameraClearance(), body: none, desired: shortBoom, field: null, frames: 1));
        Assert.Equal(expected: Target, actual: Run(arm: arm, body: Body(), desired: Target, field: null, frames: 1));
        Assert.Equal(expected: WorldCameraClearanceMode.None, actual: arm.Mode);
        arm.Reset();
    }
    /// <summary>A target inside geometry sits inside a shell around the pivot, such as a portal frame: the sweep starts
    /// past the shell, a boom no longer than the shell skip stands as authored, as does one whose shell reaches past
    /// the skip, and geometry beyond the shell shortens the boom.</summary>
    [Fact]
    public void AnObstructedTargetSkipsTheShellAroundThePivot() {
        var plate = Walls((new Vector3(x: 0f, y: 1f, z: 0f), new Vector3(x: 4f, y: 4f, z: 0.02f)));
        var thick = Walls((new Vector3(x: 0f, y: 1f, z: 0f), new Vector3(x: 4f, y: 4f, z: 1.2f)));
        var beyond = Walls(
            (new Vector3(x: 0f, y: 1f, z: 0f), new Vector3(x: 4f, y: 4f, z: 0.02f)),
            (new Vector3(x: 0f, y: 1f, z: 3f), new Vector3(x: 4f, y: 4f, z: 0.05f))
        );
        var none = new WorldChaseBody(collider: null, orientation: Quaternion.Identity, position: Vector3.Zero, scale: 1f);
        var desired = Desired(pitch: 0f, yaw: 0f);

        Assert.Equal(expected: Desired(distance: 1f, pitch: 0f, yaw: 0f), actual: Run(arm: new WorldCameraClearance(), body: none, desired: Desired(distance: 1f, pitch: 0f, yaw: 0f), field: plate, frames: 1));
        Assert.Equal(expected: desired, actual: Run(arm: new WorldCameraClearance(), body: none, desired: desired, field: plate, frames: 1));
        Assert.Equal(expected: desired, actual: Run(arm: new WorldCameraClearance(), body: none, desired: desired, field: thick, frames: 1));
        Assert.Equal(expected: (3f - 0.05f - WorldCameraClearance.EyeRadius - 0.05f), actual: Run(arm: new WorldCameraClearance(), body: none, desired: desired, field: beyond, frames: 60).Z, tolerance: 0.02f);
    }
    /// <summary>The body's keep-out is its collider and its drawn shapes placed at its pose and scale, inflated by the
    /// swept radius: exact for a sphere, a capsule and a box along their axes, scaled with the body, turned with it, and
    /// zero for a ray that meets no volume. A creation's drawn volumes are its material-adding shapes' bounds; a cut
    /// adds none.</summary>
    [Fact]
    public void TheBodysKeepOutIsItsPosedColliderAndDrawnShapesInflatedByTheRadius() {
        var sphere = Body(collider: new WorldCollider.Sphere(Radius: 0.5f));
        var box = Body(collider: new WorldCollider.Box(
            HalfExtents: new DocumentVector3(x: 0.5f, y: 0.5f, z: 0.5f),
            Rotation: new DocumentQuaternion(value: Quaternion.Identity)
        ));
        var point = Body(collider: new WorldCollider.Capsule(Endpoint: new DocumentVector3(x: 0f, y: 0f, z: 0f), Radius: 0.25f));
        var capsule = Body();
        var center = new Vector3(x: 0f, y: 0.5f, z: 0f);

        Assert.Equal(expected: 0.8f, actual: sphere.Exit(direction: Vector3.UnitX, origin: center, radius: 0.3f), tolerance: 1e-4f);
        Assert.Equal(expected: 0f, actual: sphere.Exit(direction: Vector3.UnitX, origin: new Vector3(x: 0f, y: 5f, z: 0f), radius: 0.3f));
        Assert.Equal(expected: 0.8f, actual: box.Exit(direction: Vector3.UnitX, origin: center, radius: 0.3f), tolerance: 1e-4f);
        Assert.Equal(expected: 0f, actual: box.Exit(direction: Vector3.UnitX, origin: new Vector3(x: 0f, y: 5f, z: 0f), radius: 0.3f));
        Assert.Equal(expected: 0f, actual: box.Exit(direction: Vector3.Normalize(value: new Vector3(x: 1f, y: 1f, z: 0f)), origin: new Vector3(x: -3f, y: 0.5f, z: 0f), radius: 0.3f));
        Assert.Equal(expected: 0.55f, actual: point.Exit(direction: Vector3.UnitX, origin: new Vector3(x: 0f, y: 0.25f, z: 0f), radius: 0.3f), tolerance: 1e-4f);
        Assert.Equal(expected: 0.65f, actual: capsule.Exit(direction: Vector3.UnitX, origin: Target, radius: 0.3f), tolerance: 1e-4f);
        Assert.Equal(expected: (2.06f + 0.3f - 1f), actual: capsule.Exit(direction: Vector3.UnitY, origin: Target, radius: 0.3f), tolerance: 1e-4f);
        Assert.Equal(expected: 0f, actual: capsule.Exit(direction: Vector3.UnitY, origin: new Vector3(x: 2f, y: 1f, z: 0f), radius: 0.3f));
        Assert.Equal(expected: 0f, actual: capsule.Exit(direction: Vector3.UnitX, origin: new Vector3(x: -3f, y: 1f, z: 2f), radius: 0.3f));
        Assert.Equal(expected: 0f, actual: capsule.Exit(direction: Vector3.UnitX, origin: new Vector3(x: -3f, y: -2f, z: 0f), radius: 0.3f));
        Assert.Equal(expected: 0f, actual: capsule.Exit(direction: Vector3.Normalize(value: new Vector3(x: 1f, y: 0.05f, z: 0f)), origin: new Vector3(x: -3f, y: -1f, z: 0f), radius: 0.3f));
        Assert.Equal(expected: 1.3f, actual: Body(scale: 2f).Exit(direction: Vector3.UnitX, origin: (Target * 2f), radius: 0.6f), tolerance: 1e-4f);
        Assert.Equal(expected: 0.65f, actual: Body(scale: 0f).Exit(direction: Vector3.UnitX, origin: Target, radius: 0.3f), tolerance: 1e-4f);
        Assert.Equal(expected: 0.65f, actual: Body(orientation: default(Quaternion)).Exit(direction: Vector3.UnitX, origin: Target, radius: 0.3f), tolerance: 1e-4f);
        Assert.Equal(expected: (2.06f + 0.3f - 1f), actual: Body(orientation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: (-MathF.PI / 2f))).Exit(direction: Vector3.UnitX, origin: Vector3.UnitX, radius: 0.3f), tolerance: 1e-4f);
        Assert.Equal(expected: 0f, actual: new WorldChaseBody(collider: null, orientation: Quaternion.Identity, position: Vector3.Zero, scale: 1f).Exit(direction: Vector3.UnitX, origin: Target, radius: 0.3f));

        var drawn = WorldChaseBody.DrawnVolumes(
            document: CreationFixtures.Document(name: "drawn", shapes: [
                (CreationFixtures.UnitSphereShape with { Id = 0, Position = new Vector3(x: 2f, y: 0f, z: 0f) }),
                (CreationFixtures.Shape(type: SdfSolidPrimitive.Box, scale: new Vector3(value: 5f)) with { Id = 1, Blend = SdfBlendOp.Subtraction }),
                (CreationFixtures.Shape(type: SdfSolidPrimitive.Box, scale: new Vector3(value: 5f)) with { Id = 2, Blend = SdfBlendOp.Intersection }),
            ]),
            scale: 0.5f
        );
        // A near plane may stand up to the nearest depth at which any volume begins, off the sightline as well as on it.
        var eye = new Vector3(x: 0f, y: 0.5f, z: 3f);

        Assert.Equal(expected: 2.5f, actual: sphere.NearestDepth(eye: eye, forward: -Vector3.UnitZ), tolerance: 1e-4f);
        Assert.Equal(expected: (3f - 0.25f), actual: point.NearestDepth(eye: eye, forward: -Vector3.UnitZ), tolerance: 1e-3f);
        Assert.Equal(expected: (3f - new Vector3(value: 0.5f).Length()), actual: box.NearestDepth(eye: eye, forward: -Vector3.UnitZ), tolerance: 1e-4f);
        Assert.Equal(expected: (3f - 0.35f), actual: capsule.NearestDepth(eye: eye, forward: -Vector3.UnitZ), tolerance: 1e-4f);
        Assert.True(condition: float.IsPositiveInfinity(f: new WorldChaseBody(collider: null, orientation: Quaternion.Identity, position: Vector3.Zero, scale: 1f).NearestDepth(eye: eye, forward: -Vector3.UnitZ)));

        var only = Assert.Single(collection: drawn);

        Assert.Equal(expected: new Vector3(x: 1f, y: 0f, z: 0f), actual: new Vector3(x: only.X, y: only.Y, z: only.Z));
        Assert.Equal(expected: 1.8f, actual: new WorldChaseBody(collider: null, drawn: drawn, orientation: Quaternion.Identity, position: Vector3.Zero, scale: 1f).Exit(direction: Vector3.UnitX, origin: Vector3.Zero, radius: 0.3f), tolerance: 0.05f);
        Assert.Equal(expected: (3f - 0.5f), actual: new WorldChaseBody(collider: null, drawn: drawn, orientation: Quaternion.Identity, position: Vector3.Zero, scale: 1f).NearestDepth(eye: new Vector3(x: 4f, y: 0f, z: 0f), forward: -Vector3.UnitX), tolerance: 0.01f);
    }
}
