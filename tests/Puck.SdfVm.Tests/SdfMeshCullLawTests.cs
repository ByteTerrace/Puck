using System.Numerics;
using Puck.Abstractions.Cameras;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: a raster pass skips a mesh draw only when the draw's world box lies wholly outside what the pass can show:
/// a view's frustum before the frame's far distance, or a cascade's light box. The box carries the mesh's own box through
/// the draw's matrix and carry, a draw whose vertices ride member slots has no box and is always drawn, and
/// <see cref="SdfMeshCull.None"/> keeps everything.
/// </summary>
public sealed class SdfMeshCullLawTests {
    private static readonly SdfMesh Cube = new(
        indices: new uint[] { 0, 1, 2 },
        positions: new Vector3[] { new(x: -1f, y: -1f, z: -1f), new(x: 1f, y: 1f, z: 1f), new(x: 1f, y: -1f, z: 1f) }
    );
    // A camera at the origin looking along -Z with a 90 degree vertical field and a square image.
    private static readonly CameraSnapshot Camera = CameraSnapshot.LookAt(
        fieldOfViewRadians: (MathF.PI * 0.5f),
        position: Vector3.Zero,
        target: -Vector3.UnitZ,
        viewportHeight: 1f,
        viewportWidth: 1f
    );

    private static SdfMeshDraw At(Vector3 position, SdfMesh? mesh = null) =>
        new(Identity: "draw", Material: 0, Mesh: (mesh ?? Cube), ObjectToWorld: Matrix4x4.CreateTranslation(position: position));
    private static bool Keeps(in SdfMeshCull cull, in SdfMeshDraw draw) =>
        cull.Keeps(box: (SdfMeshBox.TryOf(box: out var box, draw: in draw) ? box : null));

    [Fact]
    public void AViewKeepsADrawItsFrustumMayShowBeforeTheFarDistance() {
        var view = SdfMeshCull.Of(camera: in Camera, far: 40f, height: 900u, width: 900u);

        Assert.True(condition: Keeps(cull: in view, draw: At(position: new Vector3(x: 0f, y: 0f, z: -10f))));
        // Straddling the right side: half of it shows.
        Assert.True(condition: Keeps(cull: in view, draw: At(position: new Vector3(x: 10.5f, y: 0f, z: -10f))));
        Assert.False(condition: Keeps(cull: in view, draw: At(position: new Vector3(x: 0f, y: 0f, z: 10f))));
        Assert.False(condition: Keeps(cull: in view, draw: At(position: new Vector3(x: 30f, y: 0f, z: -10f))));
        // Half a metre past the side at its nearest: outside a large view's margin, inside a small nested window's pixels of
        // margin, which the temporal jitter moves a larger share of its frustum.
        Assert.False(condition: Keeps(cull: in view, draw: At(position: new Vector3(x: 12.5f, y: 0f, z: -10f))));
        Assert.True(condition: Keeps(cull: SdfMeshCull.Of(camera: in Camera, far: 40f, height: 90u, width: 90u), draw: At(position: new Vector3(x: 12.5f, y: 0f, z: -10f))));
        Assert.False(condition: Keeps(cull: in view, draw: At(position: new Vector3(x: -30f, y: 0f, z: -10f))));
        Assert.False(condition: Keeps(cull: in view, draw: At(position: new Vector3(x: 0f, y: 30f, z: -10f))));
        Assert.False(condition: Keeps(cull: in view, draw: At(position: new Vector3(x: 0f, y: -30f, z: -10f))));
        Assert.False(condition: Keeps(cull: in view, draw: At(position: new Vector3(x: 0f, y: 0f, z: -60f))));
        Assert.True(condition: Keeps(cull: SdfMeshCull.Of(camera: in Camera, far: float.PositiveInfinity, height: 900u, width: 900u), draw: At(position: new Vector3(x: 0f, y: 0f, z: -60f))));
        Assert.True(condition: Keeps(cull: SdfMeshCull.None, draw: At(position: new Vector3(x: 0f, y: 0f, z: 10f))));
    }
    [Fact]
    public void ADrawsBoxFollowsItsMatrixAndCarry() {
        var view = SdfMeshCull.Of(camera: in Camera, far: 40f, height: 900u, width: 900u);
        // Scaled long along x, the box reaches into the frustum from far to its right.
        var stretched = (At(position: new Vector3(x: 30f, y: 0f, z: -10f)) with {
            ObjectToWorld = (Matrix4x4.CreateScale(scales: new Vector3(x: 25f, y: 1f, z: 1f)) * Matrix4x4.CreateTranslation(position: new Vector3(x: 30f, y: 0f, z: -10f))),
        });
        // Carried a half turn about up from behind the camera into front of it.
        var carried = (At(position: new Vector3(x: 0f, y: 0f, z: 10f)) with {
            CarryRotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: MathF.PI),
        });
        var moved = (At(position: new Vector3(x: 0f, y: 0f, z: -10f)) with { CarryTranslation = new Vector3(x: 0f, y: 0f, z: 30f) });
        // Carried through a door onto one a quarter its size: from past the far distance to a quarter of the way out.
        var shrunk = (At(position: new Vector3(x: 0f, y: 0f, z: -60f)) with { CarryScale = 0.25f });

        Assert.True(condition: Keeps(cull: in view, draw: in stretched));
        Assert.True(condition: Keeps(cull: in view, draw: in carried));
        Assert.False(condition: Keeps(cull: in view, draw: in moved));
        Assert.False(condition: Keeps(cull: in view, draw: At(position: new Vector3(x: 0f, y: 0f, z: -60f))));
        Assert.True(condition: Keeps(cull: in view, draw: in shrunk));
    }
    [Fact]
    public void ADrawWhoseVerticesRideMembersHasNoBoxAndIsAlwaysDrawn() {
        var skinned = new SdfMesh(
            indices: new uint[] { 0, 1, 2 },
            members: new SdfMeshMember[] { new(Orientation: Quaternion.Identity, Position: Vector3.Zero, SlotOffset: 1) },
            positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            vertexMembers: new byte[] { 0, 0, 0 }
        );
        var draw = At(mesh: skinned, position: new Vector3(x: 0f, y: 0f, z: 10f));

        Assert.False(condition: SdfMeshBox.TryOf(box: out _, draw: in draw));
        Assert.True(condition: Keeps(cull: SdfMeshCull.Of(camera: in Camera, far: 40f, height: 900u, width: 900u), draw: in draw));
    }
    [Fact]
    public void ACascadeKeepsADrawItsLightBoxMayHoldAndThatMayShadowWhatTheViewShows() {
        // A light looking straight down from 100 m up over a 40 m square, holding depths 1 to 150 along its fall, and a
        // view from 8 m south of the origin looking north at it.
        var light = new SdfMeshShadowCamera(
            Far: 150f,
            Forward: -Vector3.UnitY,
            HalfHeight: 20f,
            Near: 1f,
            Position: new Vector3(x: 0f, y: 100f, z: 0f),
            Right: Vector3.UnitX,
            Up: -Vector3.UnitZ
        );
        var view = CameraSnapshot.LookAt(fieldOfViewRadians: (MathF.PI * 0.5f), position: new Vector3(x: 0f, y: 0f, z: 8f), target: Vector3.Zero, viewportHeight: 1f, viewportWidth: 1f);
        var cascade = SdfMeshCull.Of(camera: in view, cascade: in light, far: 40f, height: 900u, texels: 2048, width: 900u);

        Assert.True(condition: Keeps(cull: in cascade, draw: At(position: Vector3.Zero)));
        // High above what the view shows: its shadow falls into it.
        Assert.True(condition: Keeps(cull: in cascade, draw: At(position: new Vector3(x: 0f, y: 60f, z: 0f))));
        // Outside the light's box, across it and along it.
        Assert.False(condition: Keeps(cull: in cascade, draw: At(position: new Vector3(x: 22f, y: 0f, z: 0f))));
        Assert.False(condition: Keeps(cull: in cascade, draw: At(position: new Vector3(x: 0f, y: 101f, z: 0f))));
        Assert.False(condition: Keeps(cull: in cascade, draw: At(position: new Vector3(x: 0f, y: -60f, z: 0f))));
        // Inside the light's box, but its shadow falls behind the view or beside it.
        Assert.False(condition: Keeps(cull: in cascade, draw: At(position: new Vector3(x: 0f, y: 5f, z: 12f))));
        Assert.False(condition: Keeps(cull: in cascade, draw: At(position: new Vector3(x: 11f, y: 5f, z: 0f))));
        Assert.True(condition: Keeps(cull: SdfMeshCull.None, draw: At(position: new Vector3(x: 11f, y: 5f, z: 0f))));
    }
}
