using System.Numerics;
using Puck.Abstractions.Cameras;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfTemporalHistoryLawTests {
    private static readonly SdfTemporalEpoch Epoch = new(Binding: 1, Ceiling: 0.5f, Cut: 1, Debug: 0, Enabled: true, Height: 360, Width: 640);

    [Fact]
    public void ConvergenceCountsEightCompletedSamplesAfterEachContentChangeWithoutDiscardingMotionHistory() {
        var history = new SdfTemporalHistory();

        for (var frame = 1; frame <= 8; frame++) {
            history.Prepare(camera: default, epoch: Epoch, frame: frame);
            Assert.False(condition: history.Converged);
            history.Rendered();
        }
        Assert.True(condition: history.Converged);
        Assert.Equal(expected: 8u, actual: history.Frames);
        history.Changed();
        Assert.False(condition: history.Converged);
        Assert.True(condition: history.HasPreviousView);
        Assert.Equal(expected: 8u, actual: history.Frames);
        for (var frame = 9; frame <= 16; frame++) {
            history.Prepare(camera: default, epoch: Epoch, frame: frame);
            Assert.False(condition: history.Converged);
            history.Rendered();
        }
        Assert.True(condition: history.Converged);
        history.Prepare(camera: default, epoch: Epoch with { Debug = 1 }, frame: 17);
        Assert.True(condition: history.Converged);
        Assert.Equal(expected: 0u, actual: history.Frames);
    }

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

        history.Prepare(camera: camera, epoch: epoch, frame: 1);
        Assert.False(condition: history.HasPreviousView);
        history.Rendered();
        history.Prepare(camera: next, epoch: epoch, frame: 2);
        Assert.True(condition: history.HasPreviousView);
        Assert.Equal(expected: new SdfReprojectionView(Camera: camera, Jitter: Vector2.Zero, Width: epoch.Width, Height: epoch.Height), actual: history.PreviousView);
        Assert.Equal(expected: 0u, actual: history.Frames);
        history.Rendered();
        history.Prepare(camera: camera, epoch: epoch, frame: 3);
        Assert.Equal(expected: next, actual: history.PreviousView.Camera);
        history.Prepare(epoch: epoch with { Cut = 2 }, frame: 4, camera: camera);
        Assert.False(condition: history.HasPreviousView);
        history.Rendered();
        history.Prepare(epoch: epoch with { Cut = 2 }, frame: 6, camera: next);
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
    public void EveryEpochInputAndAResidencyGapResetAtThePixelCenter() {
        SdfTemporalEpoch[] changes = [
            Epoch with { Binding = 2 }, Epoch with { Cut = 2 }, Epoch with { Width = 800 },
            Epoch with { Height = 600 }, Epoch with { Ceiling = 1f }, Epoch with { Enabled = false },
            Epoch with { Debug = 1 },
        ];

        foreach (var next in changes) {
            var history = new SdfTemporalHistory();

            history.Prepare(camera: default, epoch: Epoch, frame: 1);
            history.Rendered();
            history.Prepare(camera: default, epoch: Epoch, frame: 2);
            Assert.Equal(expected: 1u, actual: history.Frames);
            history.Rendered();
            history.Prepare(camera: default, epoch: next, frame: 3);
            Assert.Equal(expected: 0u, actual: history.Frames);
            Assert.Equal(expected: Vector2.Zero, actual: history.Jitter);
        }
        var gap = new SdfTemporalHistory();

        gap.Prepare(camera: default, epoch: Epoch, frame: 1);
        gap.Rendered();
        gap.Prepare(camera: default, epoch: Epoch, frame: 3);
        Assert.Equal(expected: 0u, actual: gap.Frames);
    }
    [Fact]
    public void DisabledAndDebugViewsAccumulateNoJitter() {
        foreach (var epoch in ((SdfTemporalEpoch[])[Epoch with { Enabled = false }, Epoch with { Debug = 1 }])) {
            var history = new SdfTemporalHistory();

            for (var frame = 1; (frame <= 16); frame++) {
                history.Prepare(camera: default, epoch: epoch, frame: frame);
                Assert.Equal(expected: Vector2.Zero, actual: history.Jitter);
                history.Rendered();
                Assert.Equal(expected: 0u, actual: history.Frames);
            }
        }
    }
}
