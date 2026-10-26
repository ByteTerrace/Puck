using System.Numerics;
using Puck.Abstractions.Cameras;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// THE LAW: a view draws each mesh at the coarsest level whose recorded error, scaled by its draw and projected over the
/// nearest forward depth of its box at the frustum corner's stretch, is at most one display pixel, and goes back to a finer
/// level only once the level it drew shows more than 1.1 pixels; a cascade takes the coarsest level within one of its
/// texels; a forced level draws that level or the coarsest; a draw with no box, a mesh with no levels, a box reaching the
/// eye's plane and <see cref="SdfMeshDetail.Finest"/> draw the mesh's own triangles.
/// </summary>
public sealed class SdfMeshDetailLawTests {
    // A cube of half extent one whose first level is a thousandth of a unit off and second a hundredth.
    private static readonly SdfMesh Cube = new(
        indices: new uint[] { 0, 1, 2 },
        positions: new Vector3[] { new(x: -1f, y: -1f, z: -1f), new(x: 1f, y: 1f, z: 1f), new(x: 1f, y: -1f, z: 1f) }
    ) {
        Levels = [
            new SdfMeshLevel(Error: 0.001f, Indices: new uint[] { 0, 2, 1 }, TriangleMaterials: default),
            new SdfMeshLevel(Error: 0.01f, Indices: new uint[] { 1, 0, 2 }, TriangleMaterials: default),
        ],
    };
    // A camera at the origin looking along -Z with a 90 degree vertical field and a square image: at the frustum's corner
    // a displacement of e at depth z moves e sqrt(3) / z tangent units, 500 sqrt(3) e / z of a thousand pixels.
    private static readonly CameraSnapshot Camera = CameraSnapshot.LookAt(fieldOfViewRadians: (MathF.PI * 0.5f), position: Vector3.Zero, target: -Vector3.UnitZ, viewportHeight: 1f, viewportWidth: 1f);
    private static readonly SdfMeshDetail View = SdfMeshDetail.Of(camera: in Camera, displayHeight: 1000u);

    private static SdfMeshBox? BoxAt(float distance, float scale = 1f) {
        var draw = new SdfMeshDraw(Identity: "draw", Material: 0, Mesh: Cube, ObjectToWorld: (Matrix4x4.CreateScale(scale: scale) * Matrix4x4.CreateTranslation(position: new Vector3(x: 0f, y: 0f, z: -distance))));

        return (SdfMeshBox.TryOf(box: out var box, draw: in draw) ? box : null);
    }
    private static int LevelAt(float distance, int previous = -1, float scale = 1f) =>
        View.LevelOf(box: BoxAt(distance: distance, scale: scale), mesh: Cube, previous: previous);

    [Fact]
    public void AViewTakesTheCoarsestLevelWithinOneDisplayPixel() {
        // The first level shows within a pixel past a nearest depth of 0.866, the second past 8.66.
        Assert.Equal(expected: [0, 1, 1, 2], actual: [LevelAt(distance: 1.8f), LevelAt(distance: 1.9f), LevelAt(distance: 9.6f), LevelAt(distance: 9.7f)]);
        // A box reaching the eye's plane shows any error.
        Assert.Equal(expected: 0, actual: LevelAt(distance: 0.5f));
        // Twice the size, the draw's errors are twice as large and its box reaches twice as near.
        Assert.Equal(expected: [0, 1], actual: [LevelAt(distance: 3.7f, scale: 2f), LevelAt(distance: 3.8f, scale: 2f)]);
    }
    [Fact]
    public void AViewGoesBackFinerOnlyPastATenthOfAPixelMore() {
        // At 9.2 the second level shows 1.06 pixels: a fresh choice takes the first, a draw that took the second keeps it.
        Assert.Equal(expected: [1, 2, 1], actual: [LevelAt(distance: 9.2f), LevelAt(distance: 9.2f, previous: 2), LevelAt(distance: 9.2f, previous: 1)]);
        // At 8.8 it shows 1.11, and the draw goes back.
        Assert.Equal(expected: 1, actual: LevelAt(distance: 8.8f, previous: 2));
        // Back near, it goes back as far as it must, and a level past the mesh's coarsest is its coarsest.
        Assert.Equal(expected: [0, 2], actual: [LevelAt(distance: 1.5f, previous: 2), LevelAt(distance: 50f, previous: 7)]);
    }
    [Fact]
    public void ACascadeTakesTheCoarsestLevelWithinOneTexel() {
        var cascade = new SdfMeshShadowCamera(Far: 100f, Forward: -Vector3.UnitY, HalfHeight: 5f, Near: 0f, Position: (10f * Vector3.UnitY), Right: Vector3.UnitX, Up: Vector3.UnitZ);

        // A hundredth-unit texel holds both levels; a two-hundredth only the first; a stretched draw's errors grow.
        Assert.Equal(expected: 2, actual: SdfMeshDetail.Of(cascade: in cascade, texels: 1000).LevelOf(box: BoxAt(distance: 1f), mesh: Cube, previous: -1));
        Assert.Equal(expected: 1, actual: SdfMeshDetail.Of(cascade: in cascade, texels: 2000).LevelOf(box: BoxAt(distance: 1f), mesh: Cube, previous: 2));
        Assert.Equal(expected: 1, actual: SdfMeshDetail.Of(cascade: in cascade, texels: 1000).LevelOf(box: BoxAt(distance: 1f, scale: 2f), mesh: Cube, previous: -1));
    }
    [Fact]
    public void AForcedLevelTheFinestAndAnUnboundedDrawAreChosenAlike() {
        Assert.Equal(expected: [1, 2, 0], actual: [SdfMeshDetail.Forced(level: 1).LevelOf(box: BoxAt(distance: 1f), mesh: Cube, previous: -1), SdfMeshDetail.Forced(level: 5).LevelOf(box: BoxAt(distance: 1f), mesh: Cube, previous: -1), SdfMeshDetail.Forced(level: 0).LevelOf(box: BoxAt(distance: 50f), mesh: Cube, previous: 2)]);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => SdfMeshDetail.Forced(level: -1));
        Assert.Equal(expected: 0, actual: SdfMeshDetail.Finest.LevelOf(box: BoxAt(distance: 50f), mesh: Cube, previous: 2));
        Assert.Equal(expected: 0, actual: View.LevelOf(box: null, mesh: Cube, previous: 2));
        Assert.Equal(expected: 0, actual: View.LevelOf(box: BoxAt(distance: 50f), mesh: new SdfMesh(indices: Cube.Indices, positions: Cube.Positions), previous: -1));
    }
    [Fact]
    public void ABoxsStretchBoundsHowFarItsDrawCarriesAnObjectLength() {
        static float StretchOf(Matrix4x4 matrix, float carry = 1f) {
            var draw = new SdfMeshDraw(Identity: "draw", Material: 0, Mesh: Cube, ObjectToWorld: matrix) { CarryScale = carry };

            Assert.True(condition: SdfMeshBox.TryOf(box: out var box, draw: in draw));

            return box.Stretch;
        }

        // Exact for a turn and a uniform or axis scale, and the carry's scale multiplies it.
        Assert.Equal(expected: 2f, actual: StretchOf(matrix: (Matrix4x4.CreateScale(scale: 2f) * Matrix4x4.CreateRotationY(radians: 0.7f))), precision: 5);
        Assert.Equal(expected: 3f, actual: StretchOf(matrix: Matrix4x4.CreateScale(xScale: 3f, yScale: 1f, zScale: 0.5f)), precision: 5);
        Assert.Equal(expected: 6f, actual: StretchOf(matrix: Matrix4x4.CreateScale(xScale: 3f, yScale: 1f, zScale: 0.5f), carry: 2f), precision: 5);
        // A shear lengthens its diagonal past either row; the bound holds it.
        var shear = Matrix4x4.Identity with { M21 = 1f };

        Assert.True(condition: (StretchOf(matrix: shear) >= Vector3.TransformNormal(normal: Vector3.Normalize(value: new Vector3(x: 0.5257f, y: 0.8507f, z: 0f)), matrix: shear).Length()));
    }
}
