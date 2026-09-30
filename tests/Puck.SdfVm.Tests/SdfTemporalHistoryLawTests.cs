using System.Numerics;
using Puck.Abstractions.Cameras;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfTemporalHistoryLawTests {
    private static readonly SdfTemporalEpoch Epoch = new(Binding: 1, Ceiling: 0.5f, Cut: 1, Debug: 0, Enabled: true, Height: 360, Width: 640);
    private static readonly SdfTemporalEpoch Motion = (Epoch with { Debug = DebugViewModes.Motion, Enabled = false });

    [Fact]
    public void PreviousCameraTracksCompletedRendersWithoutTemporalSamplingAndCutsInvalidateIt() {
        var history = new SdfTemporalHistory();
        var camera = new CameraSnapshot(Position: new Vector3(x: 1f, y: 2f, z: 3f),
            Right: Vector3.UnitX, Up: Vector3.UnitY, Forward: Vector3.UnitZ, TanHalfFieldOfView: 0.5f, AspectRatio: 1.5f) {
            FrustumOffset = new Vector2(x: 0.125f, y: -0.25f),
        };
        var next = new CameraSnapshot(Position: new Vector3(x: 4f, y: 5f, z: 6f),
            Right: camera.Right, Up: camera.Up, Forward: camera.Forward,
            TanHalfFieldOfView: camera.TanHalfFieldOfView, AspectRatio: camera.AspectRatio) {
            FrustumOffset = camera.FrustumOffset,
        };
        var epoch = Epoch with { Enabled = false };

        history.Prepare(camera: camera, epoch: epoch, previousPoses: 0, currentPoses: 1);
        Assert.False(condition: history.HasPreviousView);
        history.Rendered();
        history.Prepare(camera: next, epoch: epoch, previousPoses: 1, currentPoses: 2);
        Assert.True(condition: history.HasPreviousView);
        Assert.Equal(expected: new SdfReprojectionView(Camera: camera, Jitter: Vector2.Zero, Width: epoch.Width, Height: epoch.Height), actual: history.PreviousView);
        Assert.Equal(expected: 0u, actual: history.Frames);
        history.Rendered();
        history.Prepare(camera: camera, epoch: epoch, previousPoses: 2, currentPoses: 2);
        Assert.Equal(expected: next, actual: history.PreviousView.Camera);
        history.Prepare(epoch: epoch with { Cut = 2 }, camera: camera, previousPoses: 2, currentPoses: 2);
        Assert.False(condition: history.HasPreviousView);
        history.Rendered();
        // Another view's render moved the poses in between: the previous tables no longer hold this render's poses.
        history.Prepare(epoch: epoch with { Cut = 2 }, camera: next, previousPoses: 3, currentPoses: 3);
        Assert.False(condition: history.HasPreviousView);
    }
    [Fact]
    public void EightSamplesBeginAtTheCenterAndRepeat() {
        Vector2[] expected = [
            Vector2.Zero, new(x: 0f, y: (-1f / 6f)), new(x: -0.25f, y: (1f / 6f)), new(x: 0.25f, y: (-7f / 18f)),
            new(x: -0.375f, y: (-1f / 18f)), new(x: 0.125f, y: (5f / 18f)), new(x: -0.125f, y: (-5f / 18f)), new(x: 0.375f, y: (1f / 18f)),
        ];

        for (uint index = 0; (index < 24); index++) {
            Assert.True(condition: (Vector2.Distance(value1: expected[(index % 8)], value2: SdfTemporalHistory.Sample(index: index)) < 1e-6f));
        }
    }
    [Fact]
    public void EveryEpochInputAndAPoseGapResetAtThePixelCenter() {
        SdfTemporalEpoch[] changes = [
            Epoch with { Binding = 2 }, Epoch with { Cut = 2 }, Epoch with { Width = 800 },
            Epoch with { Height = 600 }, Epoch with { Ceiling = 1f }, Epoch with { Enabled = false },
            Epoch with { Debug = 1 },
        ];

        foreach (var next in changes) {
            var history = new SdfTemporalHistory();

            history.Prepare(camera: default, epoch: Epoch, previousPoses: 0, currentPoses: 0);
            history.Rendered();
            history.Prepare(camera: default, epoch: Epoch, previousPoses: 0, currentPoses: 0);
            Assert.Equal(expected: 1u, actual: history.Frames);
            history.Rendered();
            history.Prepare(camera: default, epoch: next, previousPoses: 0, currentPoses: 0);
            Assert.Equal(expected: 0u, actual: history.Frames);
            Assert.Equal(expected: Vector2.Zero, actual: history.Jitter);
        }
        var gap = new SdfTemporalHistory();

        gap.Prepare(camera: default, epoch: Epoch, previousPoses: 0, currentPoses: 4);
        gap.Rendered();
        gap.Prepare(camera: default, epoch: Epoch, previousPoses: 5, currentPoses: 5);
        Assert.Equal(expected: 0u, actual: gap.Frames);
    }
    [Fact]
    public void DisabledAndDebugViewsAccumulateNoJitter() {
        foreach (var epoch in ((SdfTemporalEpoch[])[Epoch with { Enabled = false }, Epoch with { Debug = 1 }])) {
            var history = new SdfTemporalHistory();

            for (var frame = 1; (frame <= 16); frame++) {
                history.Prepare(camera: default, epoch: epoch, previousPoses: 0, currentPoses: 0);
                Assert.Equal(expected: Vector2.Zero, actual: history.Jitter);
                history.Rendered();
                Assert.Equal(expected: 0u, actual: history.Frames);
            }
        }
    }
    // A converging capture's index is the runtime's count of samples, not the instance's count of renders: a render the
    // runtime does not count (a tainted or stand-in dependency) takes the same sample again.
    [Fact]
    public void ACountedSourceDrivesTheIndexAndUncountedRendersRepeatTheirSample() {
        var history = new SdfTemporalHistory();
        int[] counted = [0, 0, 0, 1, 2, 2, 3];
        uint[] expected = [0, 0, 0, 1, 2, 2, 3];

        for (var render = 0; (render < counted.Length); render++) {
            history.Prepare(camera: default, counted: counted[render], epoch: Epoch, previousPoses: 0, currentPoses: 0);
            Assert.Equal(expected: expected[render], actual: history.Frames);
            Assert.Equal(expected: SdfTemporalHistory.Sample(index: expected[render]), actual: history.Jitter);
            history.Rendered();
        }
        // A reset mid-count restarts at the pixel center from the count it resets at.
        history.Reset();
        history.Prepare(camera: default, counted: 3, epoch: Epoch, previousPoses: 0, currentPoses: 0);
        Assert.Equal(expected: 0u, actual: history.Frames);
        history.Rendered();
        history.Prepare(camera: default, counted: 4, epoch: Epoch, previousPoses: 0, currentPoses: 0);
        Assert.Equal(expected: 1u, actual: history.Frames);
    }
    // A render jittered for a converging capture does not stand once the capture ends: the next render returns to the
    // pixel center, and that one stands.
    [Fact]
    public void AJitteredRenderDoesNotStandOnceSamplingEnds() {
        var history = new SdfTemporalHistory();

        for (var sample = 0; (sample < 8); sample++) {
            history.Prepare(camera: default, counted: sample, epoch: Epoch, previousPoses: 0, currentPoses: 0);
            history.Rendered();
        }
        Assert.NotEqual(expected: Vector2.Zero, actual: SdfTemporalHistory.Sample(index: 7));
        var still = (Epoch with { Enabled = false });

        Assert.False(condition: history.Stands(epoch: still, previousPoses: 0));
        history.Prepare(camera: default, epoch: still, previousPoses: 0, currentPoses: 0);
        Assert.Equal(expected: Vector2.Zero, actual: history.Jitter);
        history.Rendered();
        Assert.True(condition: history.Stands(epoch: still, previousPoses: 0));
        Assert.True(condition: history.Stands(epoch: still, previousPoses: 0));
    }
    // The motion view reads the previous view and poses: its first render after a reset has none, the next has them,
    // and only then does it stand. Without motion, a missing previous view never keeps a view rendering.
    [Fact]
    public void TheMotionViewRendersUntilItsPreviousViewAndPosesSettle() {
        var history = new SdfTemporalHistory();
        var camera = new CameraSnapshot(Position: Vector3.Zero, Right: Vector3.UnitX, Up: Vector3.UnitY, Forward: Vector3.UnitZ,
            TanHalfFieldOfView: 0.5f, AspectRatio: 1.5f);

        history.Prepare(camera: camera, epoch: Motion, previousPoses: 7, currentPoses: 7);
        history.Rendered();
        Assert.False(condition: history.Stands(epoch: Motion, previousPoses: 7));
        history.Prepare(camera: camera, epoch: Motion, previousPoses: 7, currentPoses: 7);
        Assert.True(condition: history.HasPreviousView);
        history.Rendered();
        Assert.True(condition: history.Stands(epoch: Motion, previousPoses: 7));

        // A render that moved a pose owes one settle: the previous tables then advance to the moved poses.
        history.Prepare(camera: camera, epoch: Motion, previousPoses: 7, currentPoses: 8);
        history.Rendered();
        Assert.False(condition: history.Stands(epoch: Motion, previousPoses: 8));
        history.Prepare(camera: camera, epoch: Motion, previousPoses: 8, currentPoses: 8);
        history.Rendered();
        Assert.True(condition: history.Stands(epoch: Motion, previousPoses: 8));

        var plain = new SdfTemporalHistory();
        var off = (Motion with { Debug = 0 });

        plain.Prepare(camera: camera, epoch: off, previousPoses: 7, currentPoses: 7);
        plain.Rendered();
        Assert.True(condition: plain.Stands(epoch: off, previousPoses: 7));
        Assert.True(condition: plain.Stands(epoch: off, previousPoses: 9));
    }
}
