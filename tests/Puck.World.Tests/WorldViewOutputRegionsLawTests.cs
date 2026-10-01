using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

public sealed class WorldViewOutputRegionsLawTests {
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

        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: "mixed", views: views);
        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: "camera", views: views);
        composer.Compose(cameraOverride: null, elapsedSeconds: 0.6f, joinedCount: 1, layoutOverride: "camera", views: views);
        var ordinal = 0;

        foreach (var slot in composer.Slots) {
            Assert.Null(@object: slot.Instance);
            var output = WorldViewOutputRegions.View(view: ordinal++, views: views);

            Assert.True(condition: (slot.Region.Width > 0f));
            Assert.True(condition: (output.Width >= slot.Region.Width));
            Assert.True(condition: (output.Height >= slot.Region.Height));
        }
        Assert.Equal(actual: ordinal, expected: 2);
    }
    [Fact]
    public void CameraAndPaneEnvelopesCoverTheOtherOccupantsRectAtTheSamePhysicalSlot() {
        var views = new WorldViewDefaults {
            Layouts = [
                new WorldViewLayout(Name: "camera", Slots: [new WorldViewSlot(Camera: "camera")], TransitionSeconds: 1f),
                new WorldViewLayout(Name: "pane", Slots: [new WorldViewSlot(Instance: "pane", Width: 0.25f, Height: 0.5f)], TransitionSeconds: 1f),
            ],
        };
        var composer = new WorldViewComposer();

        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: "camera", views: views);
        composer.Compose(cameraOverride: null, elapsedSeconds: 0f, joinedCount: 1, layoutOverride: "pane", views: views);
        composer.Compose(cameraOverride: null, elapsedSeconds: 0.6f, joinedCount: 1, layoutOverride: "pane", views: views);
        var pane = composer.Slots[0];
        var paneOutput = WorldViewOutputRegions.Pane(instance: "pane", region: pane.Region, views: views);

        Assert.Equal(expected: "pane", actual: pane.Instance);
        Assert.True(condition: (paneOutput.Width >= pane.Region.Width));
        Assert.True(condition: (paneOutput.Height >= pane.Region.Height));

        // Interrupt the transition. The camera inherits the larger in-flight pane rectangle at the cut.
        composer.Compose(cameraOverride: null, elapsedSeconds: 0.6f, joinedCount: 1, layoutOverride: "camera", views: views);
        composer.Compose(cameraOverride: null, elapsedSeconds: 1.2f, joinedCount: 1, layoutOverride: "camera", views: views);
        var camera = composer.Slots[0];
        var cameraOutput = WorldViewOutputRegions.View(view: 0, views: views);

        Assert.Equal(expected: "camera", actual: camera.Camera);
        Assert.True(condition: (cameraOutput.Width >= camera.Region.Width));
        Assert.True(condition: (cameraOutput.Height >= camera.Region.Height));
    }
}
