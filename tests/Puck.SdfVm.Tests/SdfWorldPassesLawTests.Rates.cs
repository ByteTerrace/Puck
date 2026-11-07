using System.Numerics;
using Puck.Abstractions.Cameras;
using Xunit;

namespace Puck.SdfVm.Tests;

// The far cascade's rate (SdfUpdateRates.ShadowFar): a view holds its far cascade for the frames a 30 Hz refresh spans,
// splits a refresh the rate alone calls for over two frames when both fit in that period, and every change the fit stands
// for ends the hold at once and redraws both layers.
public sealed partial class SdfWorldPassesLawTests {
    private const int RateFrames = 8;

    // Produces `frames` frames, applying `change` to the frame source before each, and returns how many drew both far
    // layers, how many drew one and how many held. The cadence gate is off, so every pass is forced and only the hold can
    // stand the far pass.
    private static (long Rendered, long Split, long Held) FarCascadeCounts(SdfUpdateRates rates, int frames, Func<SdfFrame, int, SdfFrame> change, SdfUpdateRateIntervals? intervals = null, int displayHertz = 60, SdfMeshCullLevel? cull = null) {
        var counts = SdfUpdateRateCounts.Counters();
        using var rig = new TemporalRig(displayHertz: displayHertz, views: 1);

        rig.SourceFrame = (DrawnFrame() with { RateCounts = counts, RateIntervals = intervals, UpdateRates = rates });
        if (cull is { } mode) { rig.SourceFrame = rig.SourceFrame with { MeshCull = mode }; }
        // The first frames fit and render the far cascade whatever the lever holds.
        for (var frame = 0; (frame < 2); frame++) { rig.Produce(); }

        var renderedBefore = counts.Read(kind: SdfUpdateRateCounts.ShadowFarRendered);
        var splitBefore = counts.Read(kind: SdfUpdateRateCounts.ShadowFarSplit);
        var heldBefore = counts.Read(kind: SdfUpdateRateCounts.ShadowFarHeld);

        for (var frame = 0; (frame < frames); frame++) {
            rig.SourceFrame = change(arg1: rig.SourceFrame, arg2: frame);
            rig.Produce();
        }

        return (
            (counts.Read(kind: SdfUpdateRateCounts.ShadowFarRendered) - renderedBefore),
            (counts.Read(kind: SdfUpdateRateCounts.ShadowFarSplit) - splitBefore),
            (counts.Read(kind: SdfUpdateRateCounts.ShadowFarHeld) - heldBefore)
        );
    }
    private static SdfFrame WithCamera(SdfFrame frame, Vector3 position, Vector3 target) => (frame with {
        Views = [frame.Views[0] with { Camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: position, target: target, viewportHeight: Extent, viewportWidth: Extent) }],
    });
    // The drawn frame's key light turned by a share of the far cascade's light tolerance
    // (SdfUpdateRateTiers.ShadowFarLightTurnRadians), about an axis across it.
    private static Vector3 TurnedLight(SdfFrame frame, float share) {
        var original = Vector3.Normalize(value: DrawnFrame().Lights[0].Direction);
        var far = SdfMeshShadowCascades.Fit(frame: frame, view: 0);
        var angle = (share * SdfUpdateRateTiers.ShadowFarLightTurnRadians(camera: far[1], texels: far.Texels));
        var axis = Vector3.Normalize(value: Vector3.Cross(vector1: original, vector2: ((MathF.Abs(x: original.Y) < 0.99f) ? Vector3.UnitY : Vector3.UnitX)));

        return Vector3.Transform(value: original, rotation: Quaternion.CreateFromAxisAngle(axis: axis, angle: angle));
    }
    private static SdfFrame WithLight(SdfFrame frame, Vector3 direction) {
        frame.Lights.Set(index: 0, light: frame.Lights[0] with { Direction = Vector3.Normalize(value: direction) });

        return (frame with { });
    }

    [Theory]
    [InlineData(SdfMeshCullLevel.Camera)]
    [InlineData(SdfMeshCullLevel.Occlusion)]
    public void AStillReceiverSetRetainsFarHoldsAndSplitsUnderATurningLight(SdfMeshCullLevel cull) {
        var (rendered, split, held) = FarCascadeCounts(rates: SdfUpdateRates.ShadowFar, frames: RateFrames,
            displayHertz: 120, cull: cull,
            change: static (frame, index) => WithLight(frame, TurnedLight(frame, 0.01f * (index + 1))));
        Assert.Equal(0L, rendered);
        Assert.True(split > 0);
        Assert.True(held > 0);
        Assert.Equal(RateFrames, rendered + split + held);
    }

    [Fact]
    public void AChangedReceiverCameraRefreshesBothOcclusionLayers() {
        var (rendered, split, held) = FarCascadeCounts(rates: SdfUpdateRates.ShadowFar, frames: RateFrames,
            displayHertz: 120, cull: SdfMeshCullLevel.Occlusion,
            change: static (frame, index) => WithCamera(frame, new Vector3(0.001f * (index + 1), 0f, -5f), Vector3.Zero));
        Assert.Equal(RateFrames, rendered);
        Assert.Equal(0L, split);
        Assert.Equal(0L, held);
    }

    // A view that stands still on a 60 Hz display splits each 30 Hz refresh: one frame draws the depth, the next the exit
    // layer, so every frame draws one layer, none draws both, and each layer's interval is the tier's whole period.
    [Fact]
    public void AStillViewDrawsOneFarLayerAFrameAtSixtyHertz() {
        var intervals = new SdfUpdateRateIntervals();
        var (rendered, split, held) = FarCascadeCounts(intervals: intervals, rates: SdfUpdateRates.ShadowFar, frames: RateFrames, change: static (frame, _) => frame);

        Assert.Equal(expected: 0L, actual: rendered);
        Assert.Equal(expected: RateFrames, actual: split);
        Assert.Equal(expected: 0L, actual: held);
        Assert.Equal(expected: (2d / 60d), actual: (intervals.Longest(hertz: 30, lever: SdfUpdateRates.ShadowFar) ?? 0d), tolerance: 1e-9d);
    }
    // Where two frames do not fit in the 30 Hz period (a 40 Hz display), no refresh splits: each draws both layers, so no
    // layer refreshes less than 30 times a second.
    [Fact]
    public void ASlowerDisplayRedrawsBothFarLayersOnEveryRefresh() {
        var (rendered, split, held) = FarCascadeCounts(displayHertz: 40, rates: SdfUpdateRates.ShadowFar, frames: RateFrames, change: static (frame, _) => frame);

        Assert.Equal(expected: RateFrames, actual: rendered);
        Assert.Equal(expected: 0L, actual: split);
        Assert.Equal(expected: 0L, actual: held);
    }
    // With the lever off, or a lever other than the far cascade's on, every frame renders both layers and none holds.
    [InlineData(SdfUpdateRates.None)]
    [InlineData(SdfUpdateRates.Bodies)]
    [Theory]
    public void TheFarCascadeRendersEveryFrameWhileItsLeverIsOff(SdfUpdateRates rates) {
        var (rendered, split, held) = FarCascadeCounts(rates: rates, frames: RateFrames, change: static (frame, _) => frame);

        Assert.Equal(expected: RateFrames, actual: rendered);
        Assert.Equal(expected: 0L, actual: split);
        Assert.Equal(expected: 0L, actual: held);
    }
    // A view that turns past the tier's tolerance every frame, moves past it, cuts or whose key light turns past one far
    // texel ends every hold and redraws both layers; the controls turn, move and turn the light within the tolerances and
    // keep splitting.
    [InlineData("turn", false)]
    [InlineData("move", false)]
    [InlineData("cut", false)]
    [InlineData("light", false)]
    [InlineData("turn-within", true)]
    [InlineData("move-within", true)]
    [InlineData("light-within", true)]
    [Theory]
    public void AFarCascadeHoldEndsWhenWhatItStandsForChanges(string change, bool holds) {
        var (rendered, split, held) = FarCascadeCounts(rates: SdfUpdateRates.ShadowFar, frames: RateFrames, change: (frame, index) => {
            var step = (index + 1);

            return change switch {
                "turn" => WithCamera(frame: frame, position: new Vector3(x: 0f, y: 0f, z: -5f), target: new Vector3(x: (step * 0.5f), y: 0f, z: 0f)),
                "turn-within" => WithCamera(frame: frame, position: new Vector3(x: 0f, y: 0f, z: -5f), target: new Vector3(x: (step * 0.001f), y: 0f, z: 0f)),
                "move" => WithCamera(frame: frame, position: new Vector3(x: (step * 2f), y: 0f, z: -5f), target: new Vector3(x: (step * 2f), y: 0f, z: 0f)),
                "move-within" => WithCamera(frame: frame, position: new Vector3(x: (step * 0.01f), y: 0f, z: -5f), target: new Vector3(x: (step * 0.01f), y: 0f, z: 0f)),
                "cut" => (frame with { Views = [frame.Views[0] with { CutRevision = step }] }),
                "light-within" => WithLight(frame: frame, direction: TurnedLight(frame: frame, share: (0.2f * step))),
                _ => WithLight(frame: frame, direction: new Vector3(x: (0.2f * step), y: -1f, z: 0.1f)),
            };
        });

        if (holds) {
            Assert.Equal(expected: 0L, actual: rendered);
            Assert.Equal(expected: RateFrames, actual: (split + held));
        } else {
            Assert.Equal(expected: RateFrames, actual: rendered);
            Assert.Equal(expected: 0L, actual: (split + held));
        }
    }
}
