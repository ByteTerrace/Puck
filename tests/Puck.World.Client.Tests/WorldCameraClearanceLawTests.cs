using System.Numerics;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>The chase eye sees its body from outside: never inside the body's collider, never inside static geometry,
/// and never behind geometry that hides the target. Running the World cannot show where the eye stands relative to
/// the body's own volume, so these laws resolve the eye against walls and a body whose collider is the debug world's
/// Moth: a capsule of radius 0.35 standing 2.06 m tall, framed from 1 m above its root.</summary>
public sealed class WorldCameraClearanceLawTests {
    private static readonly WorldCollider.Capsule MothCollider = new(
        Endpoint: new DocumentVector3(x: 0f, y: 1.36f, z: 0f),
        Radius: 0.35f
    );
    private static readonly Vector3 Target = new(x: 0f, y: 1f, z: 0f);

    private static FixedPosition At(Vector3 value) => FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: value));
    private static WorldChaseBody Body(WorldCollider? collider = null, float scale = 1f, Quaternion? orientation = null) => new(
        collider: FixedWorldCollider.Compile(
            collider: (collider ?? MothCollider),
            creations: []
        ),
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
    private static Vector3 Resolve(SdfFieldEvaluator? field, Vector3 desired, WorldChaseBody body, Vector3? target = null) => WorldCameraClearance.Resolve(
        desiredEye: desired,
        field: field,
        subject: body,
        target: (target ?? Target),
        up: Vector3.UnitY
    );
    // The boom's heading in the level plane: what the clearance may never change.
    private static float Heading(Vector3 eye, Vector3 target) => MathF.Atan2(
        x: (eye.Z - target.Z),
        y: (eye.X - target.X)
    );

    /// <summary>The body pressed into a corner with the boom pointing into it: no length of the authored boom clears both
    /// walls and the body, so the boom rises over the body on the same heading, outside the body by the eye's radius,
    /// clear of both walls and with a clear sightline.</summary>
    [Fact]
    public void ACornerBehindTheBodyRaisesTheEyeOverItRatherThanIntoIt() {
        var field = Walls(
            (new Vector3(x: -0.65f, y: 1.5f, z: 0f), new Vector3(x: 0.05f, y: 1.5f, z: 4f)),
            (new Vector3(x: 0f, y: 1.5f, z: 0.65f), new Vector3(x: 4f, y: 1.5f, z: 0.05f))
        );
        var yaw = (-MathF.PI / 4f);
        var desired = Desired(
            pitch: 0.3f,
            yaw: yaw
        );
        var eye = Resolve(
            body: Body(),
            desired: desired,
            field: field
        );

        Assert.True(condition: (CapsuleDistance(point: eye) >= (WorldCameraClearance.EyeRadius - 0.01f)), userMessage: $"eye {eye} is within the eye radius of the body");
        Assert.True(condition: (FieldDistance(field: field, point: eye) >= 0f), userMessage: $"eye {eye} is inside a wall");
        Assert.True(condition: field.LineOfSight(from: At(value: eye), to: At(value: Target)));
        Assert.True(condition: (eye.Y > (Target.Y + 1f)), userMessage: $"eye {eye} did not rise over the body");
        Assert.Equal(expected: yaw, actual: Heading(eye: eye, target: Target), tolerance: 1e-3f);
    }
    /// <summary>The body backed flat against a wall with the boom pointing at it: even the steepest rise cannot clear
    /// the eye's whole radius of the wall, so the eye stands just outside the body on the rise that leaves it farthest
    /// from the wall, its centre still in front of the wall.</summary>
    [Fact]
    public void ABodyBackedIntoAWallKeepsTheEyeOutsideBothOnTheSteepestRise() {
        var field = Walls((new Vector3(x: 0f, y: 1.5f, z: 0.45f), new Vector3(x: 4f, y: 1.5f, z: 0.05f)));
        var eye = Resolve(
            body: Body(),
            desired: Desired(pitch: 0.3f, yaw: 0f),
            field: field
        );
        var boom = (eye - Target);

        Assert.True(condition: (CapsuleDistance(point: eye) >= (WorldCameraClearance.EyeRadius - 0.01f)), userMessage: $"eye {eye} is within the eye radius of the body");
        Assert.True(condition: (FieldDistance(field: field, point: eye) > 0f), userMessage: $"eye {eye} is inside the wall");
        Assert.Equal(expected: WorldCameraClearance.LiftCeiling, actual: MathF.Asin(x: (boom.Y / boom.Length())), tolerance: 1e-3f);
    }
    /// <summary>A wall far enough behind the body only shortens the boom, along its own direction, and an open field
    /// leaves the eye exactly where the rig put it.</summary>
    [Fact]
    public void AnUncrowdedBoomShortensAlongItselfAndAnOpenOneStandsAsAuthored() {
        var field = Walls((new Vector3(x: 0f, y: 1.5f, z: 2.05f), new Vector3(x: 4f, y: 1.5f, z: 0.05f)));
        var desired = Desired(pitch: 0.3f, yaw: 0f);
        var shortened = Resolve(
            body: Body(),
            desired: desired,
            field: field
        );
        var open = Walls((new Vector3(x: 0f, y: -10f, z: 0f), new Vector3(x: 1f, y: 1f, z: 1f)));

        Assert.True(condition: ((shortened - Target).Length() < 2f));
        Assert.True(condition: ((shortened - Target).Length() > 1.5f));
        Assert.Equal(expected: 1f, actual: Vector3.Dot(vector1: Vector3.Normalize(value: (shortened - Target)), vector2: Vector3.Normalize(value: (desired - Target))), tolerance: 1e-4f);
        Assert.Equal(expected: desired, actual: Resolve(body: Body(), desired: desired, field: open));
        Assert.Equal(expected: Target, actual: Resolve(body: Body(), desired: Target, field: open));
    }
    /// <summary>With no geometry the eye keeps out of the body alone: a boom shorter than the body is pushed out of it,
    /// and a body with no collider keeps the eye out of nothing.</summary>
    [Fact]
    public void WithNoFieldTheEyeKeepsOutOfTheBodyAlone() {
        var shortBoom = Desired(distance: 0.2f, pitch: 0f, yaw: 0f);
        var pushed = Resolve(body: Body(), desired: shortBoom, field: null);
        var none = new WorldChaseBody(collider: null, orientation: Quaternion.Identity, position: Vector3.Zero, scale: 1f);

        Assert.Equal(expected: (0.35f + WorldCameraClearance.EyeRadius), actual: (pushed - Target).Length(), tolerance: 1e-4f);
        Assert.Equal(expected: Desired(pitch: 0f, yaw: 0f), actual: Resolve(body: Body(), desired: Desired(pitch: 0f, yaw: 0f), field: null));
        Assert.Equal(expected: shortBoom, actual: Resolve(body: none, desired: shortBoom, field: null));
    }
    /// <summary>A crowded boom with nowhere to rise stands just outside the body: one already straight up under a low
    /// ceiling has no level heading to rise along, and one already past the lift ceiling has no rise left.</summary>
    [Fact]
    public void ACrowdedBoomWithNoRiseLeftStandsJustOutsideTheBody() {
        var ceiling = Walls((new Vector3(x: 0f, y: 2.5f, z: 0f), new Vector3(x: 4f, y: 0.05f, z: 4f)));

        foreach (var pitch in new[] { (MathF.PI / 2f), 1.45f }) {
            var eye = Resolve(
                body: Body(),
                desired: Desired(pitch: pitch, yaw: 0f),
                field: ceiling
            );

            Assert.True(condition: (CapsuleDistance(point: eye) >= (WorldCameraClearance.EyeRadius - 0.01f)), userMessage: $"pitch {pitch}: eye {eye} is within the eye radius of the body");
        }
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

        Assert.Equal(expected: Desired(distance: 1f, pitch: 0f, yaw: 0f), actual: Resolve(body: none, desired: Desired(distance: 1f, pitch: 0f, yaw: 0f), field: plate));
        Assert.Equal(expected: desired, actual: Resolve(body: none, desired: desired, field: plate));
        Assert.Equal(expected: desired, actual: Resolve(body: none, desired: desired, field: thick));
        Assert.Equal(expected: (3f - 0.05f - WorldCameraClearance.EyeRadius - 0.05f), actual: Resolve(body: none, desired: desired, field: beyond).Z, tolerance: 0.02f);
    }
    /// <summary>The body's keep-out is its collider placed at its pose and scale, inflated by the swept radius: exact
    /// for a sphere, a capsule and a box along their axes, scaled with the body, turned with it, and zero for a ray
    /// that meets no volume.</summary>
    [Fact]
    public void TheBodysKeepOutIsItsPosedColliderInflatedByTheRadius() {
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
    }
}
