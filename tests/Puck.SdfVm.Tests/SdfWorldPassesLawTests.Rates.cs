using System.Numerics;
using Puck.Abstractions.Cameras;
using Xunit;

namespace Puck.SdfVm.Tests;

// The far cascade's rate (SdfUpdateRates.ShadowFar): a view holds its far cascade for the frames a 30 Hz refresh spans,
// and every change the fit stands for ends the hold at once.
public sealed partial class SdfWorldPassesLawTests {
    private const int RateFrames = 8;

    // Produces `frames` frames, applying `change` to the frame source before each, and returns the far cascade's
    // rendered and held counts. The cadence gate is off, so every pass is forced and only the hold can stand the far pass.
    private static (long Rendered, long Held) FarCascadeCounts(SdfUpdateRates rates, int frames, Func<SdfFrame, int, SdfFrame> change, SdfUpdateRateIntervals? intervals = null) {
        var counts = SdfUpdateRateCounts.Counters();
        using var rig = new TemporalRig(displayHertz: 60, views: 1);

        rig.SourceFrame = (DrawnFrame() with { RateCounts = counts, RateIntervals = intervals, UpdateRates = rates });
        // The first frames fit and render the far cascade whatever the lever holds.
        for (var frame = 0; (frame < 2); frame++) { rig.Produce(); }

        var renderedBefore = counts.Read(kind: SdfUpdateRateCounts.ShadowFarRendered);
        var heldBefore = counts.Read(kind: SdfUpdateRateCounts.ShadowFarHeld);

        for (var frame = 0; (frame < frames); frame++) {
            rig.SourceFrame = change(arg1: rig.SourceFrame, arg2: frame);
            rig.Produce();
        }

        return ((counts.Read(kind: SdfUpdateRateCounts.ShadowFarRendered) - renderedBefore), (counts.Read(kind: SdfUpdateRateCounts.ShadowFarHeld) - heldBefore));
    }
    private static SdfFrame WithCamera(SdfFrame frame, Vector3 position, Vector3 target) => (frame with {
        Views = [frame.Views[0] with { Camera = CameraSnapshot.LookAt(fieldOfViewRadians: 1f, position: position, target: target, viewportHeight: Extent, viewportWidth: Extent) }],
    });

    // A view that stands still holds its far cascade between refreshes of a 30 Hz tier on a 60 Hz display: it renders
    // every second frame and counts the others held.
    [Fact]
    public void AStillViewRendersItsFarCascadeEverySecondFrameWhileTheLeverIsOn() {
        var intervals = new SdfUpdateRateIntervals();
        var (rendered, held) = FarCascadeCounts(intervals: intervals, rates: SdfUpdateRates.ShadowFar, frames: RateFrames, change: static (frame, _) => frame);

        Assert.Equal(expected: (RateFrames / 2), actual: rendered);
        Assert.Equal(expected: (RateFrames / 2), actual: held);
        // Each refresh closes an interval of two 60 Hz frames: the 30 Hz tier's whole period.
        Assert.Equal(expected: (2d / 60d), actual: (intervals.Longest(hertz: 30, lever: SdfUpdateRates.ShadowFar) ?? 0d), tolerance: 1e-9d);
    }
    // With the lever off, or a lever other than the far cascade's on, every frame renders it and none is held.
    [InlineData(SdfUpdateRates.None)]
    [InlineData(SdfUpdateRates.Bodies)]
    [Theory]
    public void TheFarCascadeRendersEveryFrameWhileItsLeverIsOff(SdfUpdateRates rates) {
        var (rendered, held) = FarCascadeCounts(rates: rates, frames: RateFrames, change: static (frame, _) => frame);

        Assert.Equal(expected: RateFrames, actual: rendered);
        Assert.Equal(expected: 0L, actual: held);
    }
    // A view that turns past the tier's tolerance every frame, moves past it, cuts or changes the key light's direction
    // ends every hold; the control turns and moves within the tolerance and still holds.
    [InlineData("turn", false)]
    [InlineData("move", false)]
    [InlineData("cut", false)]
    [InlineData("light", false)]
    [InlineData("turn-within", true)]
    [InlineData("move-within", true)]
    [Theory]
    public void AFarCascadeHoldEndsWhenWhatItStandsForChanges(string change, bool holds) {
        var (rendered, held) = FarCascadeCounts(rates: SdfUpdateRates.ShadowFar, frames: RateFrames, change: (frame, index) => {
            var step = (index + 1);

            switch (change) {
                case "turn":
                    return WithCamera(frame: frame, position: new Vector3(x: 0f, y: 0f, z: -5f), target: new Vector3(x: (step * 0.5f), y: 0f, z: 0f));
                case "turn-within":
                    return WithCamera(frame: frame, position: new Vector3(x: 0f, y: 0f, z: -5f), target: new Vector3(x: (step * 0.001f), y: 0f, z: 0f));
                case "move":
                    return WithCamera(frame: frame, position: new Vector3(x: (step * 2f), y: 0f, z: -5f), target: new Vector3(x: (step * 2f), y: 0f, z: 0f));
                case "move-within":
                    return WithCamera(frame: frame, position: new Vector3(x: (step * 0.01f), y: 0f, z: -5f), target: new Vector3(x: (step * 0.01f), y: 0f, z: 0f));
                case "cut":
                    return (frame with { Views = [frame.Views[0] with { CutRevision = step }] });
                default:
                    frame.Lights.Set(index: 0, light: frame.Lights[0] with { Direction = Vector3.Normalize(value: new Vector3(x: (0.2f * step), y: -1f, z: 0.1f)) });

                    return (frame with { });
            }
        });

        if (holds) {
            Assert.True(condition: (held > 0L), userMessage: $"{change} held {held} of {RateFrames} frames");
        } else {
            Assert.Equal(expected: 0L, actual: held);
            Assert.Equal(expected: RateFrames, actual: rendered);
        }
    }
}
