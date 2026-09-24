using System.Numerics;
using Puck.Abstractions.Cameras;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="ViewProjection"/>: its transforms invert each other, depth is reversed-Z with an infinite far
/// plane and orders nearer surfaces first, and a depth reconstructs the SDF march's ray parameter along the same ray
/// the march takes through a pixel.
/// </summary>
public sealed class ViewProjectionLawTests {
    private const float Near = 0.02f;

    private static readonly CameraSnapshot[] Cameras = [
        CameraSnapshot.LookAt(
            position: new Vector3(x: 0f, y: 1.6f, z: 4f),
            target: new Vector3(x: 0f, y: 1f, z: 0f),
            fieldOfViewRadians: 1.2f,
            viewportWidth: 1920,
            viewportHeight: 1080
        ),
        CameraSnapshot.LookAt(
            position: new Vector3(x: -30f, y: 12f, z: 55f),
            target: new Vector3(x: 7f, y: -3f, z: -2f),
            fieldOfViewRadians: 0.4f,
            viewportWidth: 640,
            viewportHeight: 960
        ),
    ];
    private static readonly Vector2[] FrustumOffsets = [
        Vector2.Zero,
        new(x: 0.35f, y: -0.2f),
    ];

    private static IEnumerable<ViewProjection> Views() {
        foreach (var camera in Cameras) {
            foreach (var offset in FrustumOffsets) {
                yield return ViewProjection.Create(
                    camera: camera,
                    frustumOffset: offset,
                    near: Near
                );
            }
        }
    }
    // The ray sdf-world.hlsli's cameraRayDirection takes through a sample, spelled from the camera basis alone.
    private static Vector3 MarchDirection(CameraSnapshot camera, Vector2 offset, Vector2 ndc) =>
        Vector3.Normalize(value: (
            ((camera.Forward +
            (((ndc.X * camera.AspectRatio) * camera.TanHalfFieldOfView) * camera.Right)) +
            ((ndc.Y * camera.TanHalfFieldOfView) * camera.Up)) +
            ((offset.X * camera.Right) + (offset.Y * camera.Up))
        ));
    private static void Near3(Vector3 expected, Vector3 actual, float tolerance) =>
        Assert.True(
            condition: (Vector3.Distance(value1: expected, value2: actual) <= tolerance),
            userMessage: $"expected {expected}, actual {actual}"
        );

    [Fact]
    public void ClipToWorldInvertsWorldToClip() {
        foreach (var view in Views()) {
            var product = (view.WorldToClip * view.ClipToWorld);

            for (var row = 0; (row < 4); row++) {
                for (var column = 0; (column < 4); column++) {
                    Assert.Equal(
                        expected: ((row == column) ? 1f : 0f),
                        actual: product[row, column],
                        tolerance: 1e-4f
                    );
                }
            }
        }
    }
    [Fact]
    public void AProjectedPointUnprojectsToItself() {
        foreach (var camera in Cameras) {
            foreach (var offset in FrustumOffsets) {
                var view = ViewProjection.Create(camera: camera, frustumOffset: offset, near: Near);

                for (var u = 0; (u <= 8); u++) {
                    for (var v = 0; (v <= 8); v++) {
                        var ndc = ViewProjection.NdcOf(uv: new Vector2(x: (u / 8f), y: (v / 8f)));
                        var direction = MarchDirection(camera: camera, ndc: ndc, offset: offset);

                        foreach (var t in ((float[])[0.05f, 1f, 17f, 400f])) {
                            var world = (camera.Position + (direction * t));
                            var clip = view.ToClip(world: world);
                            var projected = new Vector2(x: (clip.X / clip.W), y: (clip.Y / clip.W));
                            var depth = (clip.Z / clip.W);

                            Assert.Equal(actual: projected.X, expected: ndc.X, tolerance: 1e-3f);
                            Assert.Equal(actual: projected.Y, expected: ndc.Y, tolerance: 1e-3f);
                            Near3(
                                actual: view.Unproject(depth: depth, ndc: projected),
                                expected: world,
                                tolerance: (2e-5f * ((1f + t) + camera.Position.Length()))
                            );
                        }
                    }
                }
            }
        }
    }
    [Fact]
    public void DepthIsReversedWithAnInfiniteFarPlane() {
        foreach (var view in Views()) {
            Assert.Equal(expected: 1f, actual: view.DepthAt(forwardDistance: Near));

            var previous = float.PositiveInfinity;

            foreach (var distance in ((float[])[Near, 0.021f, 0.5f, 1f, 10f, 1e3f, 1e6f, 1e9f])) {
                var depth = view.DepthAt(forwardDistance: distance);

                // Nearer surfaces have strictly greater depth, and no finite distance reaches the far value 0.
                Assert.True(condition: (depth < previous));
                Assert.True(condition: (depth > 0f));
                Assert.True(condition: (depth <= 1f));
                previous = depth;
            }
        }
    }
    [Fact]
    public void ClipDepthOrdersPointsAlongEveryRayNearestFirst() {
        foreach (var camera in Cameras) {
            foreach (var offset in FrustumOffsets) {
                var view = ViewProjection.Create(camera: camera, frustumOffset: offset, near: Near);
                var direction = MarchDirection(camera: camera, ndc: new Vector2(x: 0.3f, y: -0.6f), offset: offset);
                var nearer = float.PositiveInfinity;

                foreach (var t in ((float[])[0.03f, 0.2f, 3f, 90f, 5e4f])) {
                    var clip = view.ToClip(world: (camera.Position + (direction * t)));
                    var depth = (clip.Z / clip.W);

                    Assert.True(condition: (depth < nearer));
                    Assert.InRange(actual: depth, high: 1f, low: 0f);
                    nearer = depth;
                }
            }
        }
    }
    [Fact]
    public void DepthReconstructsTheMarchRayParameter() {
        foreach (var camera in Cameras) {
            foreach (var offset in FrustumOffsets) {
                var view = ViewProjection.Create(camera: camera, frustumOffset: offset, near: Near);

                for (var pixel = 0; (pixel < 16); pixel++) {
                    var uv = new Vector2(x: ((pixel + 0.5f) / 16f), y: (((15 - pixel) + 0.5f) / 16f));
                    var ndc = ViewProjection.NdcOf(uv: uv);
                    var direction = MarchDirection(camera: camera, ndc: ndc, offset: offset);

                    foreach (var t in ((float[])[Near, 0.7f, 12f, 250f])) {
                        var clip = view.ToClip(world: (camera.Position + (direction * t)));

                        // A relative bound on t, plus the float spacing of the world point the test builds.
                        Assert.Equal(
                            expected: t,
                            actual: view.RayParameter(depth: (clip.Z / clip.W), ndc: ndc),
                            tolerance: ((1e-4f * t) + (4e-7f * (1f + camera.Position.Length())))
                        );
                    }
                }
            }
        }
    }
    [Fact]
    public void ACreatedViewIsItsOwnPreviousFrame() {
        var first = ViewProjection.Create(camera: Cameras[0], near: Near);
        var second = ViewProjection.Create(camera: Cameras[1], near: Near);
        var moved = second.WithPrevious(previous: first);

        Assert.Equal(expected: first.WorldToClip, actual: first.PreviousWorldToClip);
        Assert.Equal(expected: first.WorldToView, actual: first.PreviousWorldToView);
        Assert.Equal(expected: first.WorldToClip, actual: moved.PreviousWorldToClip);
        Assert.Equal(expected: first.WorldToView, actual: moved.PreviousWorldToView);
        Assert.Equal(expected: second.WorldToClip, actual: moved.WorldToClip);
        Assert.Equal(expected: Vector2.Zero, actual: ViewProjection.Jitter);
    }
    [Fact]
    public void ANonPositiveOrNonFiniteNearIsRefused() {
        foreach (var near in ((float[])[0f, -1f, float.NaN, float.PositiveInfinity])) {
            _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ViewProjection.Create(camera: Cameras[0], near: near));
        }
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => ViewProjection.Create(
            camera: Cameras[0],
            frustumOffset: new Vector2(x: float.NaN, y: 0f),
            near: Near
        ));
    }
}
