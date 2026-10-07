using Puck.Abstractions.Presentation;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Each view and pane reserves every rect it reaches over the layout transition in flight whose endpoints
/// nest, keeps its start's extent through one whose endpoints oppose, the spectator fallback fits inside its
/// reservation, interrupted transitions retain reservations until the chain settles, and a settled composition requests
/// exactly its rects.</summary>
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
        var regions = new WorldViewOutputRegions();
        var envelopes = new List<NormalizedRect>();
        NormalizedRect[]? during = null;
        var settledFrames = 0;

        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: from, views: views);
        for (var step = 0; (step <= 20); step++) {
            var seconds = (step * 0.1f);
            var layout = (((interruptTo is not null) && (seconds >= interruptAt)) ? interruptTo : to);

            composer.Compose(cameraOverride: null, elapsedSeconds: seconds, joinedCount: 1, layoutOverride: layout, views: views);
            regions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 1);

            var ordinal = 0;

            foreach (var slot in composer.Slots) {
                if (slot.Instance is { } instance) {
                    var pane = regions.Pane(composer: composer, instance: instance, region: slot.Region);

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
        var regions = new WorldViewOutputRegions();
        var envelopes = new List<NormalizedRect>();

        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: "mixed", views: views);
        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: "camera", views: views);
        composer.Compose(cameraOverride: null, elapsedSeconds: 0.6f, joinedCount: 1, layoutOverride: "camera", views: views);
        regions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 1);

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
    public void ASmallCameraAndAPaneReserveTheWholeDisplaySpectatorOnEitherSideOfTheCut() {
        var views = new WorldViewDefaults {
            Layouts = [
                new WorldViewLayout(Name: "camera", Slots: [new WorldViewSlot(Camera: "camera", Width: 0.25f)], TransitionSeconds: 1f),
                new WorldViewLayout(Name: "pane", Slots: [new WorldViewSlot(Instance: "pane", Width: 0.25f)], TransitionSeconds: 1f),
            ],
        };
        var whole = new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f);

        foreach (var (from, to) in new[] { ("camera", "pane"), ("pane", "camera") }) {
            var composer = new WorldViewComposer();
            var regions = new WorldViewOutputRegions();
            var envelopes = new List<NormalizedRect>();

            composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: from, views: views);
            regions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 1);
            for (var step = 0; (step < 10); step++) {
                composer.Compose(cameraOverride: null, elapsedSeconds: (step * 0.1f), joinedCount: 1, layoutOverride: to, views: views);
                regions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 1);
                Assert.Equal(expected: whole, actual: Assert.Single(collection: envelopes));
            }
            composer.Compose(cameraOverride: null, elapsedSeconds: 1f, joinedCount: 1, layoutOverride: to, views: views);
            regions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 1);
            Assert.Equal(expected: ((to == "pane") ? whole : whole with { Width = 0.25f }), actual: Assert.Single(collection: envelopes));
        }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void InterruptedShrinksKeepTheirReservationUntilTheChainSettles(bool pane) {
        WorldViewSlot Slot(float width) => (pane
            ? new WorldViewSlot(Instance: "pane", Width: width)
            : new WorldViewSlot(Camera: "camera", Width: width));
        var views = new WorldViewDefaults {
            Layouts = [
                new WorldViewLayout(Name: "wide", Slots: [Slot(width: 0.75f)], TransitionSeconds: 1f),
                new WorldViewLayout(Name: "narrow", Slots: [Slot(width: 0.25f)], TransitionSeconds: 1f),
                new WorldViewLayout(Name: "tiny", Slots: [Slot(width: 0.125f)], TransitionSeconds: 1f),
            ],
        };
        var composer = new WorldViewComposer();
        var regions = new WorldViewOutputRegions();
        var envelopes = new List<NormalizedRect>();

        NormalizedRect Envelope() {
            regions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 1);
            return (pane ? regions.Pane(composer: composer, instance: "pane", region: composer.Slots[0].Region) : envelopes[0]);
        }

        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: "wide", views: views);
        var reserved = Envelope();

        for (var step = 0; (step < 10); step++) {
            var layout = (((step % 2) == 0) ? "narrow" : "tiny");

            composer.Compose(cameraOverride: null, elapsedSeconds: step, joinedCount: 1, layoutOverride: layout, views: views);
            Assert.Equal(expected: reserved, actual: Envelope());
            composer.Compose(cameraOverride: null, elapsedSeconds: (step + 0.4f), joinedCount: 1, layoutOverride: layout, views: views);
            Assert.Equal(expected: reserved, actual: Envelope());
        }
        composer.Compose(cameraOverride: null, elapsedSeconds: 11f, joinedCount: 1, layoutOverride: "tiny", views: views);
        Assert.Equal(expected: reserved with { Width = 0.125f }, actual: Envelope());
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AnOppositeAxisTransitionKeepsItsStartUntilItSettles(bool pane) {
        WorldViewSlot Slot(float width, float height) => (pane
            ? new WorldViewSlot(Height: height, Instance: "pane", Width: width)
            : new WorldViewSlot(Camera: "camera", Height: height, Width: width));
        var views = new WorldViewDefaults {
            Layouts = [
                new WorldViewLayout(Name: "wide", Slots: [Slot(height: 0.25f, width: 0.75f)], TransitionSeconds: 1f),
                new WorldViewLayout(Name: "tall", Slots: [Slot(height: 0.75f, width: 0.25f)], TransitionSeconds: 1f),
            ],
        };
        var composer = new WorldViewComposer();
        var regions = new WorldViewOutputRegions();
        var envelopes = new List<NormalizedRect>();
        var wide = new NormalizedRect(Height: 0.25f, Width: 0.75f, X: 0f, Y: 0f);
        var tall = new NormalizedRect(Height: 0.75f, Width: 0.25f, X: 0f, Y: 0f);
        var allocations = new List<NormalizedRect>();

        NormalizedRect Envelope() {
            regions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 1);
            return (pane ? regions.Pane(composer: composer, instance: "pane", region: composer.Slots[0].Region) : envelopes[0]);
        }

        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: "wide", views: views);
        allocations.Add(item: Envelope());
        for (var step = 0; (step <= 12); step++) {
            composer.Compose(cameraOverride: null, elapsedSeconds: (step * 0.1f), joinedCount: 1, layoutOverride: "tall", views: views);
            var envelope = Envelope();

            // The eased rect grows past the start's height, which it resamples from until the transition settles.
            Assert.Equal(expected: ((composer.TransitionProgress < 1f) ? wide : tall), actual: envelope);
            if (envelope != allocations[^1]) {
                allocations.Add(item: envelope);
            }
        }
        // One allocation per transition: the wide start, then the tall rect it settles at, never the 0.75-by-0.75
        // envelope of both.
        Assert.Equal(actual: allocations, expected: [wide, tall]);
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
        var regions = new WorldViewOutputRegions();
        var envelopes = new List<NormalizedRect>();
        var half = new NormalizedRect(Height: 1f, Width: 0.5f, X: 0f, Y: 0f);
        var whole = new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f);

        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 2, layoutOverride: "split", views: views);
        regions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 2);
        Assert.Equal(actual: envelopes, expected: [half, half]);

        // Split to action: the first seat grows to the whole display and the second collapses, so both hold the larger
        // endpoint until the transition settles, then the first allocates the whole display and the second nothing.
        composer.Compose(cameraOverride: null, elapsedSeconds: 0.1f, joinedCount: 2, layoutOverride: "action", views: views);
        regions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 2);
        Assert.Equal(actual: envelopes, expected: [whole, half]);
        composer.Compose(cameraOverride: null, elapsedSeconds: 0.6f, joinedCount: 2, layoutOverride: "action", views: views);
        regions.Views(cameras: Cameras, composer: composer, envelopes: envelopes, joinedCount: 2);
        Assert.Equal(actual: envelopes, expected: [whole]);
    }
}
