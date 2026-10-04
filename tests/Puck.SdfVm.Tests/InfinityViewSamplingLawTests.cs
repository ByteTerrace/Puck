using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the viewer's sky samples an infinity view's image at the tangent the pixel's world direction has on
/// the viewer's basis (<see cref="InfinityViewSampling"/>, the reference of <c>sky/kinds/view.hlsli</c>), so the instance
/// pixel that renders a viewer pixel's ray is the texel that pixel reads: for every pixel of the instance, the viewer ray
/// through the tangent that pixel casts reads back that pixel's coordinate, with the image's top row at zero. A direction
/// behind the camera plane or outside the rectangle reads nothing, and the packed record carries the viewer's basis, the
/// rectangle, the fallback colour and the view kind's coverage.
/// </summary>
public sealed class InfinityViewSamplingLawTests {
    private const uint ViewerHeight = 900u;
    private const uint ViewerWidth = 1600u;

    [Fact]
    public void PackedInfinityBindingsFollowAuthoredLayersAcrossMuteAndTierChanges() {
        var sky = new SdfSky { Quality = SdfSkyTier.Low };
        var muted = sky.Add(parameters: new SdfSkyView(), label: "muted", opacity: 0);
        var high = sky.Add(parameters: new SdfSkyView(), label: "high", tier: SdfSkyTier.High);
        var visible = sky.Add(parameters: new SdfSkyView(), label: "visible");
        var details = new SdfSkyDetails();
        for (var index = 0; index < 40; index++) { _ = details.RowOf($"earlier-{index}"); }
        var layers = new SdfSkyLayer[SdfSky.MaxLayers];
        var authored = new int[SdfSky.MaxLayers];

        sky.Pack(lights: new SdfLights(), farDistance: 100, details: details, block: out _, layers: layers, authoredIndices: authored);
        Assert.Equal(new[] { 0, visible, -1, -1, -1, -1, -1, -1 }, authored);
        Assert.Equal(details.RowOf("visible"), layers[1].Detail);

        sky.Quality = SdfSkyTier.High;
        sky.LayerAt(muted).Opacity = 1;
        sky.Pack(lights: new SdfLights(), farDistance: 100, details: details, block: out _, layers: layers, authoredIndices: authored);
        Assert.Equal(new[] { 0, muted, high, visible, -1, -1, -1, -1 }, authored);
        Assert.Equal(details.RowOf("visible"), layers[3].Detail);
    }

    private static InfinityViewSpec Spec(CameraSnapshot viewer, InfinityViewKind kind = InfinityViewKind.World) => new(
        Anchor: new Vector3(x: 40f, y: -3f, z: 7f),
        Fallback: new Vector3(x: 0.1f, y: 0.2f, z: 0.3f),
        FarDistance: 500f,
        Kind: kind,
        Levers: InfinityViewLevers.None,
        Mask: new InfinityViewMask(Axis: ((viewer.Forward + (0.15f * viewer.Right)) + (0.1f * viewer.Up)), HalfAngle: 0.25f),
        MinimumTier: QualityTier.Low,
        Name: "lobby",
        Orientation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 0.7f),
        Refresh: 1,
        Scale: 1f
    );
    private static CameraSnapshot Viewer(float yaw) => CameraSnapshot.LookAt(
        fieldOfViewRadians: 1.0f,
        position: new Vector3(x: 3f, y: 1f, z: -2f),
        target: (new Vector3(x: 3f, y: 1f, z: -2f) + Vector3.Transform(value: -Vector3.UnitZ, rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: yaw))),
        viewportHeight: ViewerHeight,
        viewportWidth: ViewerWidth
    );

    [InlineData(0f)]
    [InlineData(0.4f)]
    [InlineData(-0.9f)]
    [Theory]
    public void AnInstancePixelsRayReadsBackThatPixelsCoordinate(float yaw) {
        var viewer = Viewer(yaw: yaw);
        var spec = Spec(viewer: viewer);
        var frame = InfinityViewFit.Fit(spec: spec, tier: QualityTier.High, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);

        Assert.True(condition: frame.Visible);

        var view = InfinityViewSampling.Describe(frame: frame, imageSlot: 3, spec: spec, viewer: viewer);

        foreach (var ndc in new[] { new Vector2(x: -0.9f, y: 0.9f), new Vector2(x: 0f, y: 0f), new Vector2(x: 0.6f, y: -0.3f), new Vector2(x: 0.98f, y: -0.98f) }) {
            // The tangent the instance pixel casts on the rectangle, and the viewer's ray through it.
            var tangent = new Vector2(
                x: (((ndc.X * frame.Camera.AspectRatio) * frame.Camera.TanHalfFieldOfView) + frame.Camera.FrustumOffset.X),
                y: ((ndc.Y * frame.Camera.TanHalfFieldOfView) + frame.Camera.FrustumOffset.Y)
            );
            var ray = ((viewer.Forward + (tangent.X * viewer.Right)) + (tangent.Y * viewer.Up));

            Assert.True(condition: InfinityViewSampling.TryUv(direction: ray, uv: out var uv, view: in view));
            Assert.Equal(actual: uv.X, expected: ((ndc.X + 1f) / 2f), precision: 3);
            Assert.Equal(actual: uv.Y, expected: ((1f - ndc.Y) / 2f), precision: 3);
        }
    }
    [Fact]
    public void ADirectionBehindTheCameraPlaneOrOutsideTheRectangleReadsNothing() {
        var viewer = Viewer(yaw: 0f);
        var spec = Spec(viewer: viewer);
        var frame = InfinityViewFit.Fit(spec: spec, tier: QualityTier.High, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);
        var view = InfinityViewSampling.Describe(frame: frame, imageSlot: 0, spec: spec, viewer: viewer);
        var past = (frame.Rect.Z + 0.05f);

        Assert.False(condition: InfinityViewSampling.TryUv(direction: -viewer.Forward, uv: out _, view: in view));
        Assert.False(condition: InfinityViewSampling.TryUv(direction: (viewer.Forward + (past * viewer.Right)), uv: out _, view: in view));
        Assert.False(condition: InfinityViewSampling.TryUv(direction: (viewer.Forward + ((frame.Rect.X - 0.05f) * viewer.Right)), uv: out _, view: in view));
    }
    [InlineData(InfinityViewKind.World, 0u)]
    [InlineData(InfinityViewKind.Far, 1u)]
    [Theory]
    public void ThePackedRecordCarriesTheViewersBasisTheRectangleAndTheFallback(InfinityViewKind kind, uint coverage) {
        var viewer = Viewer(yaw: 0.3f);
        var spec = Spec(kind: kind, viewer: viewer);
        var frame = InfinityViewFit.Fit(spec: spec, tier: QualityTier.High, viewer: viewer, viewerHeight: ViewerHeight, viewerWidth: ViewerWidth);
        var view = InfinityViewSampling.Describe(frame: frame, imageSlot: 5, spec: spec, viewer: viewer);

        Assert.Equal(expected: viewer.Right, actual: view.Right);
        Assert.Equal(expected: viewer.Up, actual: view.Up);
        Assert.Equal(expected: viewer.Forward, actual: view.Forward);
        Assert.Equal(expected: frame.Rect, actual: view.Rect);
        Assert.Equal(expected: spec.Fallback, actual: view.Fallback);
        Assert.Equal(actual: view.ImageSlot, expected: 5);
        Assert.Equal(actual: view.Coverage, expected: coverage);
        Assert.Equal(expected: SdfSkyLayerKind.View, actual: SdfSkyView.Kind);
    }
}
