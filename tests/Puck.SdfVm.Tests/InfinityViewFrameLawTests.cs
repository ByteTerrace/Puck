using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SdfVm.Views;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: an infinity view renders exactly the rectangle its region's bounding cone projects to, through an
/// off-axis frustum of the viewer's own, from a fixed anchor, turned as the viewer is and never translated by it
/// (<see cref="InfinityViewFit"/>); a view below its tier renders nothing, below the high tier at half its scale; and a
/// view renders only while a viewer's previous frame showed it (<see cref="InfinityViewDemand"/>).
/// </summary>
public sealed class InfinityViewFrameLawTests {
    private const uint ViewerHeight = 900u;
    private const uint ViewerWidth = 1600u;

    private static readonly Vector3 Anchor = new(x: 40f, y: -3f, z: 7f);

    private static CameraSnapshot Viewer(Vector3 position, float yaw = 0f, float pitch = 0f) {
        var direction = Vector3.Transform(
            value: -Vector3.UnitZ,
            rotation: (Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: yaw) * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: pitch))
        );

        return CameraSnapshot.LookAt(
            fieldOfViewRadians: 1.0f,
            position: position,
            target: (position + direction),
            viewportHeight: ViewerHeight,
            viewportWidth: ViewerWidth
        );
    }
    private static InfinityViewSpec Spec(InfinityViewMask? mask, float scale = 1f, Quaternion? orientation = null, QualityTier minimum = QualityTier.Low) => new(
        Anchor: Anchor,
        Fallback: new Vector3(x: 0.1f, y: 0.2f, z: 0.3f),
        FarDistance: 500f,
        Kind: InfinityViewKind.World,
        Levers: InfinityViewLevers.None,
        Mask: mask,
        MinimumTier: minimum,
        Name: "lobby",
        Orientation: (orientation ?? Quaternion.Identity),
        Refresh: 1,
        Scale: scale
    );
    // The cone's direction at an angle around its rim, in the viewer's frame.
    private static Vector3 Rim(InfinityViewMask mask, float around) {
        var axis = mask.Direction;
        var side = Vector3.Normalize(value: Vector3.Cross(vector1: axis, vector2: ((MathF.Abs(x: axis.Y) < 0.9f) ? Vector3.UnitY : Vector3.UnitX)));
        var other = Vector3.Cross(vector1: axis, vector2: side);
        var rim = ((MathF.Cos(x: around) * side) + (MathF.Sin(x: around) * other));

        return ((MathF.Cos(x: mask.HalfAngle) * axis) + (MathF.Sin(x: mask.HalfAngle) * rim));
    }
    private static Vector2 Tangents(CameraSnapshot viewer, Vector3 direction) {
        var forward = Vector3.Dot(vector1: direction, vector2: viewer.Forward);

        return new Vector2(
            x: (Vector3.Dot(vector1: direction, vector2: viewer.Right) / forward),
            y: (Vector3.Dot(vector1: direction, vector2: viewer.Up) / forward)
        );
    }
    private static InfinityViewMask Mask(CameraSnapshot viewer, float right, float up, float halfAngle) => new(
        Axis: Vector3.Normalize(value: ((viewer.Forward + (MathF.Tan(x: right) * viewer.Right)) + (MathF.Tan(x: up) * viewer.Up))),
        HalfAngle: halfAngle
    );

    [Fact]
    public void EveryInstancePixelCastsTheRayOfTheViewerPixelItCovers() {
        var viewer = Viewer(position: new Vector3(x: 1f, y: 2f, z: 3f), yaw: 0.4f, pitch: -0.2f);
        var frame = InfinityViewFit.Fit(
            spec: Spec(mask: Mask(halfAngle: 0.12f, right: 0.25f, up: 0.1f, viewer: viewer)),
            tier: QualityTier.High,
            viewer: viewer,
            viewerHeight: ViewerHeight,
            viewerWidth: ViewerWidth
        );

        Assert.True(condition: frame.Visible);
        Assert.False(condition: frame.WholeFrustum);

        foreach (var x in new[] { -1f, -0.37f, 0f, 0.81f, 1f }) {
            foreach (var y in new[] { -1f, -0.5f, 0.2f, 1f }) {
                var instance = Vector3.Normalize(value: InfinityViewFit.RayThrough(camera: frame.Camera, ndc: new Vector2(x: x, y: y)));
                // The tangent point the instance pixel sits at, read back as the viewer pixel that covers it.
                var tangentX = (frame.Rect.X + (((x + 1f) / 2f) * (frame.Rect.Z - frame.Rect.X)));
                var tangentY = (frame.Rect.Y + (((y + 1f) / 2f) * (frame.Rect.W - frame.Rect.Y)));
                var viewerNdc = new Vector2(
                    x: ((tangentX - viewer.FrustumOffset.X) / (viewer.AspectRatio * viewer.TanHalfFieldOfView)),
                    y: ((tangentY - viewer.FrustumOffset.Y) / viewer.TanHalfFieldOfView)
                );
                var covered = Vector3.Normalize(value: InfinityViewFit.RayThrough(camera: viewer, ndc: viewerNdc));

                Assert.True(condition: (Vector3.Distance(value1: instance, value2: covered) < 1e-5f), userMessage: $"ndc ({x}, {y}): {instance} against {covered}");
            }
        }
    }
    [Fact]
    public void TheRectangleHoldsEveryDirectionOfTheConeAndIsTightAroundIt() {
        var viewer = Viewer(position: Vector3.Zero, yaw: -0.3f, pitch: 0.15f);
        var mask = Mask(halfAngle: 0.2f, right: -0.3f, up: 0.2f, viewer: viewer);
        var frame = InfinityViewFit.Fit(spec: Spec(mask: mask), tier: QualityTier.High, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);
        float lowX = float.MaxValue, highX = float.MinValue, lowY = float.MaxValue, highY = float.MinValue;

        for (var step = 0; (step < 4096); step++) {
            var tangent = Tangents(viewer: viewer, direction: Rim(around: ((step / 4096f) * MathF.Tau), mask: mask));

            lowX = MathF.Min(x: lowX, y: tangent.X);
            highX = MathF.Max(x: highX, y: tangent.X);
            lowY = MathF.Min(x: lowY, y: tangent.Y);
            highY = MathF.Max(x: highY, y: tangent.Y);
        }

        Assert.Equal(expected: lowX, actual: frame.Rect.X, tolerance: 1e-4f);
        Assert.Equal(expected: highX, actual: frame.Rect.Z, tolerance: 1e-4f);
        Assert.Equal(expected: lowY, actual: frame.Rect.Y, tolerance: 1e-4f);
        Assert.Equal(expected: highY, actual: frame.Rect.W, tolerance: 1e-4f);
    }
    [Fact]
    public void AViewerMovingAnywhereNeverMovesTheInstance() {
        var mask = new InfinityViewMask(Axis: new Vector3(x: 0.2f, y: 0.1f, z: -1f), HalfAngle: 0.3f);
        var spec = Spec(mask: mask);
        var home = InfinityViewFit.Fit(spec: spec, tier: QualityTier.High, viewer: Viewer(position: Vector3.Zero), viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);

        foreach (var position in new[] { new Vector3(x: 900f, y: 0f, z: 0f), new Vector3(x: -3f, y: 5000f, z: 12f), new Vector3(x: 0.001f, y: 0f, z: 0f) }) {
            var moved = InfinityViewFit.Fit(spec: spec, tier: QualityTier.High, viewer: Viewer(position: position), viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);

            Assert.Equal(actual: moved, expected: home);
        }

        Assert.Equal(expected: Anchor, actual: home.Camera.Position);
    }
    [Fact]
    public void TheInstanceTurnsWithTheViewerAndTheViewsOrientationCarriesItsBasis() {
        var spec = Spec(mask: null);
        var level = InfinityViewFit.Fit(spec: spec, tier: QualityTier.High, viewer: Viewer(position: Vector3.Zero), viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);
        var turned = InfinityViewFit.Fit(spec: spec, tier: QualityTier.High, viewer: Viewer(position: Vector3.Zero, yaw: 0.9f), viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);
        var yaw = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 0.9f);

        Assert.True(condition: (Vector3.Distance(value1: turned.Camera.Forward, value2: Vector3.Transform(value: level.Camera.Forward, rotation: yaw)) < 1e-5f));
        Assert.True(condition: (Vector3.Distance(value1: turned.Camera.Right, value2: Vector3.Transform(value: level.Camera.Right, rotation: yaw)) < 1e-5f));

        var tilt = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: 0.5f);
        var carried = InfinityViewFit.Fit(spec: Spec(mask: null, orientation: tilt), tier: QualityTier.High, viewer: Viewer(position: Vector3.Zero), viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);

        Assert.True(condition: (Vector3.Distance(value1: carried.Camera.Forward, value2: Vector3.Transform(value: level.Camera.Forward, rotation: tilt)) < 1e-5f));
        Assert.True(condition: (Vector3.Distance(value1: carried.Camera.Up, value2: Vector3.Transform(value: level.Camera.Up, rotation: tilt)) < 1e-5f));
    }
    [Fact]
    public void ARegionBehindOrBesideTheViewerRendersNothing() {
        var viewer = Viewer(position: Vector3.Zero);

        // Wholly behind: the cone stays on the far side of the horizon plane, which has no bound there and, with no part
        // of it in the frustum, would be reported whole; the viewer's frustum is what it is clipped to.
        var behind = InfinityViewFit.Fit(spec: Spec(mask: new InfinityViewMask(Axis: -viewer.Forward, HalfAngle: 0.2f)), tier: QualityTier.High, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);
        // Wholly in front but outside the frustum: 60 degrees right of a 1 radian vertical field of view.
        var beside = InfinityViewFit.Fit(spec: Spec(mask: Mask(halfAngle: 0.1f, right: 1.2f, up: 0f, viewer: viewer)), tier: QualityTier.High, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);

        Assert.False(condition: beside.Visible);
        Assert.True(condition: (behind.WholeFrustum || !behind.Visible));
    }
    [Fact]
    public void AConeReachingTheHorizonPlaneRendersTheViewersWholeFrustumAndNoMaskDoesTheSame() {
        var viewer = Viewer(position: Vector3.Zero);
        var horizon = InfinityViewFit.Fit(spec: Spec(mask: Mask(halfAngle: 0.5f, right: 1.2f, up: 0f, viewer: viewer)), tier: QualityTier.High, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);
        var open = InfinityViewFit.Fit(spec: Spec(mask: null), tier: QualityTier.High, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);

        Assert.True(condition: horizon.WholeFrustum);
        Assert.True(condition: open.WholeFrustum);
        Assert.Equal(expected: ViewerWidth, actual: open.Width);
        Assert.Equal(expected: ViewerHeight, actual: open.Height);
        Assert.Equal(expected: viewer.TanHalfFieldOfView, actual: open.Camera.TanHalfFieldOfView, tolerance: 1e-6f);
        Assert.Equal(expected: viewer.AspectRatio, actual: open.Camera.AspectRatio, tolerance: 1e-5f);
    }
    [Fact]
    public void TheExtentIsTheRectanglesShareOfTheViewersAtTheViewsScale() {
        var viewer = Viewer(position: Vector3.Zero);
        var mask = Mask(halfAngle: 0.15f, right: 0f, up: 0f, viewer: viewer);
        var high = InfinityViewFit.Fit(spec: Spec(mask: mask, scale: 0.5f), tier: QualityTier.High, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);
        var medium = InfinityViewFit.Fit(spec: Spec(mask: mask, scale: 0.5f), tier: QualityTier.Medium, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);
        var full = InfinityViewFit.Fit(spec: Spec(mask: mask, scale: 1f), tier: QualityTier.High, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);

        // A cone of half-angle a, dead ahead, spans tan(a) either way on a camera plane of half-height tan(0.5).
        var share = (MathF.Tan(x: 0.15f) / viewer.TanHalfFieldOfView);

        Assert.Equal(expected: MathF.Ceiling(x: (ViewerHeight * share)), actual: full.Height, tolerance: 1f);
        Assert.True(condition: (full.Width < (ViewerWidth / 4u)));
        Assert.True(condition: (full.Height < (ViewerHeight / 3u)));
        Assert.True(condition: ((high.Width * 2u) <= (full.Width + 2u)));
        Assert.True(condition: ((medium.Width * 4u) <= (full.Width + 4u)));
        Assert.True(condition: (medium.Width >= 1u));
    }
    [Fact]
    public void AViewBelowItsTierRendersNothing() {
        var viewer = Viewer(position: Vector3.Zero);
        var spec = Spec(mask: null, minimum: QualityTier.Medium);

        Assert.False(condition: InfinityViewFit.Fit(spec: spec, tier: QualityTier.Low, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth).Visible);
        Assert.True(condition: InfinityViewFit.Fit(spec: spec, tier: QualityTier.Medium, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth).Visible);
    }
    [Fact]
    public void ARecordIsRefusedByNameForEveryNumberOutOfRange() {
        var good = Spec(mask: new InfinityViewMask(Axis: Vector3.UnitX, HalfAngle: 0.5f));

        Assert.True(condition: good.TryValidate(reason: out _));

        foreach (var (spec, fragment) in new (InfinityViewSpec, string)[] {
            (good with { Name = "a$b" }, "name"),
            (good with { Name = "" }, "name"),
            (good with { Scale = 0f }, "scale"),
            (good with { Scale = 1.5f }, "scale"),
            (good with { Refresh = 0 }, "refresh"),
            (good with { FarDistance = float.NaN }, "far distance"),
            (good with { Anchor = new Vector3(x: float.PositiveInfinity, y: 0f, z: 0f) }, "anchor"),
            (good with { Orientation = new Quaternion(w: 2f, x: 0f, y: 0f, z: 0f) }, "orientation"),
            (good with { Mask = new InfinityViewMask(Axis: Vector3.Zero, HalfAngle: 0.5f) }, "axis"),
            (good with { Mask = new InfinityViewMask(Axis: Vector3.UnitX, HalfAngle: 1.6f) }, "half-angle"),
            (good with { Mask = new InfinityViewMask(Axis: Vector3.UnitX, HalfAngle: 0f) }, "half-angle"),
        }) {
            Assert.False(condition: spec.TryValidate(reason: out var reason));
            Assert.Contains(actualString: reason, expectedSubstring: fragment);
        }
    }
    [Fact]
    public void AViewRendersOnlyWhileTheLatestReportShowedIt() {
        var demand = new InfinityViewDemand();

        Assert.False(condition: demand.IsDemanded(view: "sky$lobby"));

        demand.Report(texels: 1200L, view: "sky$lobby");

        Assert.True(condition: demand.IsDemanded(view: "sky$lobby"));
        Assert.Equal(expected: 1, actual: demand.Count);

        demand.Report(texels: 40L, view: "sky$lobby");

        Assert.Equal(expected: 1, actual: demand.Count);

        demand.Report(texels: 0L, view: "sky$lobby");

        Assert.False(condition: demand.IsDemanded(view: "sky$lobby"));
        Assert.Equal(expected: 0, actual: demand.Count);

        demand.Report(texels: 9L, view: "sky$lobby");
        demand.Report(texels: 9L, view: "sky$moon");
        demand.Forget(view: "sky$lobby");

        Assert.False(condition: demand.IsDemanded(view: "sky$lobby"));
        Assert.True(condition: demand.IsDemanded(view: "sky$moon"));
        Assert.Equal(expected: 1, actual: demand.Count);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => demand.Report(texels: -1L, view: "sky$moon"));
    }
}
