using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfTemporalHistoryLawTests {
    private static readonly SdfTemporalEpoch Epoch = new(Binding: 1, Ceiling: 0.5f, Cut: 1, Debug: 0, Enabled: true, Height: 360, Width: 640);

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

            history.Prepare(epoch: Epoch, frame: 1);
            history.Rendered();
            history.Prepare(epoch: Epoch, frame: 2);
            Assert.Equal(expected: 1u, actual: history.Frames);
            history.Rendered();
            history.Prepare(epoch: next, frame: 3);
            Assert.Equal(expected: 0u, actual: history.Frames);
            Assert.Equal(expected: Vector2.Zero, actual: history.Jitter);
        }
        var gap = new SdfTemporalHistory();

        gap.Prepare(epoch: Epoch, frame: 1);
        gap.Rendered();
        gap.Prepare(epoch: Epoch, frame: 3);
        Assert.Equal(expected: 0u, actual: gap.Frames);
    }
    [Fact]
    public void DisabledAndDebugViewsAccumulateNoJitter() {
        foreach (var epoch in ((SdfTemporalEpoch[])[Epoch with { Enabled = false }, Epoch with { Debug = 1 }])) {
            var history = new SdfTemporalHistory();

            for (var frame = 1; (frame <= 16); frame++) {
                history.Prepare(epoch: epoch, frame: frame);
                Assert.Equal(expected: Vector2.Zero, actual: history.Jitter);
                history.Rendered();
                Assert.Equal(expected: 0u, actual: history.Frames);
            }
        }
    }
}
