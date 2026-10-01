using Puck.Abstractions.Presentation;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Each view and pane allocates the largest rect it reaches over the layout transition in flight, every rect a
/// frame of that transition places fits inside it, it holds one extent until the transition settles, and a settled
/// composition allocates exactly its rects.</summary>
public sealed class WorldViewOutputRegionsLawTests {
    private static readonly WorldCamera[] Cameras = [new(
        Anchor: null,
        Name: "camera",
        RenderHeight: 72U,
        RenderWidth: 128U,
        Rig: new WorldCameraProgram(
            Name: "camera-rig",
            Operations: [new WorldCameraProgramOp.FieldOfView(FieldOfViewRadians: new BindableScalar(literal: 0.9f))],
            Version: WorldCameraProgram.CurrentVersion
        )
    )];

    // Composes the transition from one layout to another, sampling it every tenth of a second until a second has passed,
    // and holds every frame's views and panes inside their envelopes, which keep one value until the transition settles
    // and then equal the settled rects.
    private static void HoldsEveryFrame(WorldViewDefaults views, string from, string to, float interruptAt = -1f, string? interruptTo = null) {
        var composer = new WorldViewComposer();
        var envelopes = new List<NormalizedRect>();
        NormalizedRect[]? during = null;
        var settledFrames = 0;

        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: from, views: views);
        for (var step = 0; (step <= 20); step++) {
            var seconds = (step * 0.1f);
            var layout = (((interruptTo is not null) && (seconds >= interruptAt)) ? interruptTo : to);

            composer.Compose(cameraOverride: null, elapsedSeconds: seconds, joinedCount: 1, layoutOverride: layout, views: views);
            WorldViewOutputRegions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 1);

            var ordinal = 0;

            foreach (var slot in composer.Slots) {
                if (slot.Instance is { } instance) {
                    var pane = WorldViewOutputRegions.Pane(composer: composer, instance: instance, region: slot.Region);

                    Assert.True(condition: (pane.Width >= slot.Region.Width));
                    Assert.True(condition: (pane.Height >= slot.Region.Height));
                    if (composer.TransitionProgress >= 1f) {
                        Assert.Equal(expected: slot.Region with { X = 0f, Y = 0f }, actual: pane);
                    }

                    continue;
                }

                var envelope = envelopes[ordinal++];

                Assert.True(condition: (envelope.Width >= slot.Region.Width));
                Assert.True(condition: (envelope.Height >= slot.Region.Height));
                if (composer.TransitionProgress >= 1f) {
                    Assert.Equal(expected: slot.Region with { X = 0f, Y = 0f }, actual: envelope);
                }
            }

            // The other endpoint may hold more view ordinals than this frame renders.
            Assert.True(condition: (ordinal <= envelopes.Count));
            if (composer.TransitionProgress < 1f) {
                // A transition holds one allocation from its first frame until it settles or is interrupted.
                if ((interruptTo is null) || (seconds < interruptAt)) {
                    during ??= [.. envelopes];
                    Assert.Equal(actual: envelopes, expected: during);
                }
            } else {
                settledFrames++;
            }
        }

        Assert.NotNull(@object: during);
        Assert.True(condition: (settledFrames > 0));
    }

    [Fact]
    public void APaddedDepartingCameraHasAnEnvelopeAfterAnEarlierPaneBecomesACamera() {
        var views = new WorldViewDefaults {
            Layouts = [
                new WorldViewLayout(Name: "mixed", Slots: [
                    new WorldViewSlot(Instance: "pane", Width: 0.75f),
                    new WorldViewSlot(Camera: "camera", Width: 0.25f, X: 0.75f),
                ], TransitionSeconds: 1f),
                new WorldViewLayout(Name: "camera", Slots: [new WorldViewSlot(Camera: "camera")], TransitionSeconds: 1f),
            ],
        };
        var composer = new WorldViewComposer();
        var envelopes = new List<NormalizedRect>();

        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: "mixed", views: views);
        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: "camera", views: views);
        composer.Compose(cameraOverride: null, elapsedSeconds: 0.6f, joinedCount: 1, layoutOverride: "camera", views: views);
        WorldViewOutputRegions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 1);

        // Past the midpoint the pane's slot shows the arriving camera and the departing camera still shrinks in its own:
        // two camera ordinals, each with an envelope covering its rect.
        Assert.Equal(expected: 2, actual: composer.Slots.Count(predicate: static slot => (slot.Instance is null)));
        Assert.Equal(expected: 2, actual: envelopes.Count);
        HoldsEveryFrame(from: "mixed", to: "camera", views: views);
    }
    [Fact]
    public void CameraAndPaneEnvelopesCoverTheOtherOccupantsRectAtTheSamePhysicalSlot() {
        var views = new WorldViewDefaults {
            Layouts = [
                new WorldViewLayout(Name: "camera", Slots: [new WorldViewSlot(Camera: "camera")], TransitionSeconds: 1f),
                new WorldViewLayout(Name: "pane", Slots: [new WorldViewSlot(Instance: "pane", Width: 0.25f, Height: 0.5f)], TransitionSeconds: 1f),
            ],
        };

        HoldsEveryFrame(from: "camera", to: "pane", views: views);
        // Interrupted: the camera eases back from the in-flight pane rect, the new transition's start.
        HoldsEveryFrame(from: "camera", interruptAt: 0.6f, interruptTo: "camera", to: "pane", views: views);
    }
    [Fact]
    public void ASettledSplitAllocatesEachSeatItsHalfAndATransitionHoldsItsLargerEndpoint() {
        var views = new WorldViewDefaults {
            Layouts = [
                new WorldViewLayout(Name: "action", Slots: [new WorldViewSlot()], TransitionSeconds: 0.4f),
                new WorldViewLayout(Name: "split", Slots: [new WorldViewSlot(Width: 0.5f), new WorldViewSlot(Width: 0.5f, X: 0.5f)], TransitionSeconds: 0.4f),
            ],
        };
        var composer = new WorldViewComposer();
        var envelopes = new List<NormalizedRect>();
        var half = new NormalizedRect(Height: 1f, Width: 0.5f, X: 0f, Y: 0f);
        var whole = new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f);

        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 2, layoutOverride: "split", views: views);
        WorldViewOutputRegions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 2);
        Assert.Equal(actual: envelopes, expected: [half, half]);

        // Split to action: the first seat grows to the whole display and the second collapses, so both hold the larger
        // endpoint until the transition settles, then the first allocates the whole display and the second nothing.
        composer.Compose(cameraOverride: null, elapsedSeconds: 0.1f, joinedCount: 2, layoutOverride: "action", views: views);
        WorldViewOutputRegions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 2);
        Assert.Equal(actual: envelopes, expected: [whole, half]);
        composer.Compose(cameraOverride: null, elapsedSeconds: 0.6f, joinedCount: 2, layoutOverride: "action", views: views);
        WorldViewOutputRegions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 2);
        Assert.Equal(actual: envelopes, expected: [whole]);
    }
}
