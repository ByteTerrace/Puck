using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// The far cascade's rate (SdfUpdateRates.ShadowFar) at an authored 30 Hz (SdfFrame.FarShadows): a view holds its far
// cascade for the frames a refresh spans, and a cascade at that reduced rate holds a static layer
// (SdfFarShadows.HoldsStaticLayer): each refresh the rate alone calls for draws the moving casters over a copy of the
// static casters, both layers in one frame, and the static layer is drawn again only when the fit it stands for ends.
public sealed partial class SdfWorldPassesLawTests {
    private const int RateFrames = 8;

    // Produces `frames` frames, applying `change` to the frame source before each, and returns how many drew both far
    // layers, how many drew one, how many held, and how many drew the static layer again. The cadence gate is off, so
    // every pass is forced and only the hold can stand the far pass.
    private static (long Rendered, long Split, long Held, long Static) FarCascadeCounts(SdfUpdateRates rates, int frames, Func<SdfFrame, int, SdfFrame> change, SdfUpdateRateIntervals? intervals = null, int displayHertz = 60, SdfMeshCullLevel? cull = null) {
        var counts = SdfUpdateRateCounts.Counters();
        using var rig = new TemporalRig(displayHertz: displayHertz, views: 1);

        rig.SourceFrame = (DrawnFrame() with { RateCounts = counts, RateIntervals = intervals, UpdateRates = rates, FarShadows = new SdfFarShadows(Hertz: 30, TurnRadians: 0f) });
        if (cull is { } mode) { rig.SourceFrame = rig.SourceFrame with { MeshCull = mode }; }
        // The first frames fit and render the far cascade whatever the lever holds. A cull level other than the frame's
        // changes the view's fragment (SdfWorldPasses.FragmentOf) and the new graph's first recording draws both layers
        // whatever the hold says, so the count starts once that graph has installed and drawn.
        for (var frame = 0; (frame < 2); frame++) { rig.Produce(); }
        TestLiveness.Until(step: () => { rig.Produce(); return !rig.World.IsBuildingCandidate; }, reason: static () => "the cull level's graph is still building");
        for (var frame = 0; (frame < 2); frame++) { rig.Produce(); }

        var renderedBefore = counts.Read(kind: SdfUpdateRateCounts.ShadowFarRendered);
        var splitBefore = counts.Read(kind: SdfUpdateRateCounts.ShadowFarSplit);
        var heldBefore = counts.Read(kind: SdfUpdateRateCounts.ShadowFarHeld);
        var staticBefore = counts.Read(kind: SdfUpdateRateCounts.ShadowFarStatic);

        for (var frame = 0; (frame < frames); frame++) {
            rig.SourceFrame = change(arg1: rig.SourceFrame, arg2: frame);
            rig.Produce();
        }

        return (
            (counts.Read(kind: SdfUpdateRateCounts.ShadowFarRendered) - renderedBefore),
            (counts.Read(kind: SdfUpdateRateCounts.ShadowFarSplit) - splitBefore),
            (counts.Read(kind: SdfUpdateRateCounts.ShadowFarHeld) - heldBefore),
            (counts.Read(kind: SdfUpdateRateCounts.ShadowFarStatic) - staticBefore)
        );
    }
    private static SdfFrame WithCamera(SdfFrame frame, Vector3 position, Vector3 target) => (frame with {
        Views = [frame.Views[0] with { Camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: position, target: target, viewportHeight: Extent, viewportWidth: Extent) }],
    });
    // The drawn frame's key light turned by a share of the far cascade's light tolerance
    // (SdfFarShadows.TurnRadiansFor), about an axis across it.
    private static Vector3 TurnedLight(SdfFrame frame, float share) {
        var original = Vector3.Normalize(value: DrawnFrame().Lights[0].Direction);
        var far = SdfMeshShadowCascades.Fit(frame: frame, view: 0);
        var angle = (share * SdfFarShadows.EveryFrame.TurnRadiansFor(camera: far[1], texels: far.Texels));
        var axis = Vector3.Normalize(value: Vector3.Cross(vector1: original, vector2: ((MathF.Abs(x: original.Y) < 0.99f) ? Vector3.UnitY : Vector3.UnitX)));

        return Vector3.Transform(value: original, rotation: Quaternion.CreateFromAxisAngle(axis: axis, angle: angle));
    }
    private static SdfFrame WithLight(SdfFrame frame, Vector3 direction) {
        frame.Lights.Set(index: 0, light: frame.Lights[0] with { Direction = Vector3.Normalize(value: direction) });

        return (frame with { });
    }

    // A light turning within the tolerance on a 120 Hz display refreshes the 30 Hz cascade over its standing static layer
    // and holds between refreshes, under occlusion culling as under box culling: the receiver set it was classified on
    // stands, so the static layer is never drawn again.
    [Theory]
    [InlineData(SdfMeshCullLevel.Camera)]
    [InlineData(SdfMeshCullLevel.Occlusion)]
    public void AStillReceiverSetRetainsTheFarStaticLayerUnderATurningLight(SdfMeshCullLevel cull) {
        var (rendered, split, held, drawnStatic) = FarCascadeCounts(rates: SdfUpdateRates.ShadowFar, frames: RateFrames,
            displayHertz: 120, cull: cull,
            change: static (frame, index) => WithLight(frame, TurnedLight(frame, 0.01f * (index + 1))));

        Assert.True(condition: (rendered > 0), userMessage: $"rendered {rendered}");
        Assert.True(condition: (held > 0), userMessage: $"held {held}");
        Assert.Equal(expected: 0L, actual: split);
        Assert.Equal(expected: 0L, actual: drawnStatic);
        Assert.Equal(expected: RateFrames, actual: (rendered + held));
    }

    // A receiver-conditioned cascade's coverage names the sample grid, not the camera or the geometry: a camera moving
    // within the hold's tolerance keeps its static layer and refreshes at the rate under occlusion culling as it does under
    // box culling, and one moving past it redraws the static layer with every frame's refresh.
    [Theory]
    [InlineData(0.001f, true)]
    [InlineData(2f, false)]
    public void AReceiverConditionedCascadeHoldsWithinTheCameraToleranceAndRefreshesPastIt(float metresAFrame, bool holds) {
        var (rendered, split, held, drawnStatic) = FarCascadeCounts(rates: SdfUpdateRates.ShadowFar, frames: RateFrames,
            displayHertz: 120, cull: SdfMeshCullLevel.Occlusion,
            change: (frame, index) => WithCamera(frame, new Vector3(metresAFrame * (index + 1), 0f, -5f), new Vector3(metresAFrame * (index + 1), 0f, 0f)));

        Assert.Equal(expected: 0L, actual: split);
        if (holds) {
            Assert.Equal(expected: 0L, actual: drawnStatic);
            Assert.True(condition: (held > 0), userMessage: $"held {held}");
            Assert.Equal(expected: RateFrames, actual: (rendered + held));
        } else {
            Assert.Equal(expected: RateFrames, actual: rendered);
            Assert.Equal(expected: RateFrames, actual: drawnStatic);
            Assert.Equal(expected: 0L, actual: held);
        }
    }

    // A view that stands still on a 60 Hz display refreshes its 30 Hz cascade every other frame over the standing static
    // layer, drawing only the moving casters, and holds the frames between, so each refresh's interval is the rate's
    // whole period and the static layer is never drawn again.
    [Fact]
    public void AStillViewRefreshesItsFarCascadeOverTheStaticLayerAtItsRate() {
        var intervals = new SdfUpdateRateIntervals();
        var (rendered, split, held, drawnStatic) = FarCascadeCounts(intervals: intervals, rates: SdfUpdateRates.ShadowFar, frames: RateFrames, change: static (frame, _) => frame);

        Assert.Equal(expected: (RateFrames / 2), actual: rendered);
        Assert.Equal(expected: (RateFrames / 2), actual: held);
        Assert.Equal(expected: 0L, actual: split);
        Assert.Equal(expected: 0L, actual: drawnStatic);
        Assert.Equal(expected: (2d / 60d), actual: (intervals.Longest(hertz: 30, lever: SdfUpdateRates.ShadowFar) ?? 0d), tolerance: 1e-9d);
    }
    // Where two frames do not fit in the 30 Hz period (a 40 Hz display) every frame refreshes, each over the standing
    // static layer, so a slow frame draws only the moving casters and never the whole cascade.
    [Fact]
    public void ASlowerDisplayRefreshesEveryFrameOverTheStaticLayer() {
        var (rendered, split, held, drawnStatic) = FarCascadeCounts(displayHertz: 40, rates: SdfUpdateRates.ShadowFar, frames: RateFrames, change: static (frame, _) => frame);

        Assert.Equal(expected: RateFrames, actual: rendered);
        Assert.Equal(expected: 0L, actual: split);
        Assert.Equal(expected: 0L, actual: held);
        Assert.Equal(expected: 0L, actual: drawnStatic);
    }
    // With the lever off, or a lever other than the far cascade's on, every frame renders both layers, none holds and no
    // static layer is kept.
    [InlineData(SdfUpdateRates.None)]
    [InlineData(SdfUpdateRates.Bodies)]
    [Theory]
    public void TheFarCascadeRendersEveryFrameWhileItsLeverIsOff(SdfUpdateRates rates) {
        var (rendered, split, held, drawnStatic) = FarCascadeCounts(rates: rates, frames: RateFrames, change: static (frame, _) => frame);

        Assert.Equal(expected: RateFrames, actual: rendered);
        Assert.Equal(expected: 0L, actual: split);
        Assert.Equal(expected: 0L, actual: held);
        Assert.Equal(expected: 0L, actual: drawnStatic);
    }
    // A view that turns past the tier's tolerance every frame, moves past it, cuts or whose key light turns past one far
    // texel ends every fit, so every frame redraws the static layer with both cascade layers; the controls turn, move and
    // turn the light within the tolerances of the camera the static layer was drawn from, which a refresh over it keeps
    // as its anchor, so they keep the static layer and refresh only at the rate.
    [InlineData("turn", false)]
    [InlineData("move", false)]
    [InlineData("cut", false)]
    [InlineData("light", false)]
    [InlineData("turn-within", true)]
    [InlineData("move-within", true)]
    [InlineData("light-within", true)]
    [Theory]
    public void AFarCascadeHoldEndsWhenWhatItStandsForChanges(string change, bool holds) {
        var (rendered, split, held, drawnStatic) = FarCascadeCounts(rates: SdfUpdateRates.ShadowFar, frames: RateFrames, change: (frame, index) => {
            var step = (index + 1);

            return change switch {
                "turn" => WithCamera(frame: frame, position: new Vector3(x: 0f, y: 0f, z: -5f), target: new Vector3(x: (step * 0.5f), y: 0f, z: 0f)),
                "turn-within" => WithCamera(frame: frame, position: new Vector3(x: 0f, y: 0f, z: -5f), target: new Vector3(x: (step * 0.001f), y: 0f, z: 0f)),
                "move" => WithCamera(frame: frame, position: new Vector3(x: (step * 2f), y: 0f, z: -5f), target: new Vector3(x: (step * 2f), y: 0f, z: 0f)),
                "move-within" => WithCamera(frame: frame, position: new Vector3(x: (step * 0.01f), y: 0f, z: -5f), target: new Vector3(x: (step * 0.01f), y: 0f, z: 0f)),
                "cut" => (frame with { Views = [frame.Views[0] with { CutRevision = step }] }),
                "light-within" => WithLight(frame: frame, direction: TurnedLight(frame: frame, share: (0.1f * step))),
                _ => WithLight(frame: frame, direction: new Vector3(x: (0.2f * step), y: -1f, z: 0.1f)),
            };
        });

        Assert.Equal(expected: 0L, actual: split);
        if (holds) {
            Assert.Equal(expected: 0L, actual: drawnStatic);
            Assert.Equal(expected: (RateFrames / 2), actual: held);
            Assert.Equal(expected: RateFrames, actual: (rendered + held));
        } else {
            Assert.Equal(expected: RateFrames, actual: rendered);
            Assert.Equal(expected: RateFrames, actual: drawnStatic);
            Assert.Equal(expected: 0L, actual: held);
        }
    }
}
