using System.Collections.Concurrent;
using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Tests;

// A presented capture takes one frame per presentation, numbers its PNGs, measures each frame against the one before
// it, and completes only once every encode has finished.
public sealed class PresentedFrameLawTests {
    private static byte[] Pixels(int width, int height, params (int Pixel, byte Red, byte Green, byte Blue)[] set) {
        var rgba = new byte[(width * height * 4)];

        foreach (var (pixel, red, green, blue) in set) {
            rgba[(pixel * 4)] = red;
            rgba[((pixel * 4) + 1)] = green;
            rgba[((pixel * 4) + 2)] = blue;
        }

        return rgba;
    }

    [Fact]
    public async Task ABurstTakesOneFramePerPresentationAndMeasuresEachAgainstTheOneBefore() {
        var request = FrameCaptureRequest.Presented(path: "burst.png", frames: 3);
        var encoded = new ConcurrentBag<string>();
        void Encode(string path, byte[] rgba, int width, int height) => encoded.Add(item: path);

        Assert.Equal(expected: FrameCaptureMode.Presented, actual: request.Mode);
        Assert.False(condition: request.Present(encode: Encode, height: 2, presentation: 10, rendered: 10, rgba: Pixels(width: 2, height: 2), tick: 4, width: 2));
        // A presentation already taken is not taken again.
        Assert.False(condition: request.Present(encode: Encode, height: 2, presentation: 10, rendered: 10, rgba: Pixels(width: 2, height: 2), tick: 4, width: 2));
        Assert.False(condition: request.Present(encode: Encode, height: 2, presentation: 11, rendered: 10, rgba: Pixels(width: 2, height: 2, (0, 255, 0, 0), (3, 0, 10, 0)), tick: 4, width: 2));
        Assert.True(condition: request.Present(encode: Encode, height: 2, presentation: 13, rendered: 13, rgba: Pixels(width: 2, height: 2, (0, 255, 0, 0), (3, 0, 10, 0)), tick: 5, width: 2));

        var result = await request.Completion;

        Assert.Null(@object: result.Error);
        Assert.Equal(expected: new[] { "burst-000.png", "burst-001.png", "burst-002.png" }, actual: result.Presented.Select(selector: static frame => frame.Path).ToArray());
        Assert.Equal(expected: new[] { "burst-000.png", "burst-001.png", "burst-002.png" }, actual: encoded.Order().ToArray());
        Assert.Equal(expected: 4UL, actual: result.Tick);
        Assert.Equal(expected: 10UL, actual: result.Frame);
        Assert.Null(@object: result.Presented[0].Delta);

        var moved = result.Presented[1].Delta!;

        Assert.Equal(expected: 2L, actual: moved.Changed);
        Assert.Equal(expected: 4L, actual: moved.Pixels);
        Assert.Equal(expected: (((2126 * 255) + (7152 * 10)) / 10000d / 4d), actual: moved.MeanLumaDelta, precision: 9);
        Assert.Equal(expected: ((2126 * 255) / 10000d), actual: moved.MaxLumaDelta, precision: 9);
        Assert.Equal(expected: 0L, actual: result.Presented[2].Delta!.Changed);

        var report = PresentedFrameReport.Format(frames: result.Presented, region: result.Region);

        Assert.StartsWith(expectedStartString: "[capture: presented 3 frames 2x2, differences over the whole frame]", actualString: report, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "frame 1 presentation 11 rendered 10 tick 4 (standing) changed 2 px (50%)", actualString: report, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "[capture: 2 pairs compared, 1 changed, most changed 2 px, largest mean |dY| 15.341", actualString: report, comparisonType: StringComparison.Ordinal);
        Assert.EndsWith(expectedEndString: "; 1 standing frame; 1 presentation skipped]", actualString: report, comparisonType: StringComparison.Ordinal);
        // A completed request takes nothing more.
        Assert.True(condition: request.Present(encode: Encode, height: 2, presentation: 14, rendered: 14, rgba: Pixels(width: 2, height: 2), tick: 6, width: 2));
    }
    [Fact]
    public async Task ARegionMeasuresOnlyItsPixelsAndAnExtentChangeMeasuresNothing() {
        var region = new FrameCaptureRegion(Height: 0.5, Width: 0.5, X: 0.5, Y: 0.5);
        var request = FrameCaptureRequest.Presented(path: "region.png", frames: 3, region: region);

        _ = request.Present(encode: static (_, _, _, _) => { }, height: 2, presentation: 0, rendered: 0, rgba: Pixels(width: 2, height: 2), tick: null, width: 2);
        _ = request.Present(encode: static (_, _, _, _) => { }, height: 2, presentation: 1, rendered: 1, rgba: Pixels(width: 2, height: 2, (0, 255, 0, 0), (3, 0, 10, 0)), tick: null, width: 2);
        _ = request.Present(encode: static (_, _, _, _) => { }, height: 1, presentation: 2, rendered: 2, rgba: Pixels(width: 1, height: 1), tick: null, width: 1);

        var result = await request.Completion;
        var delta = result.Presented[1].Delta!;

        Assert.Same(expected: region, actual: result.Region);
        Assert.Equal(expected: 1L, actual: delta.Changed);
        Assert.Equal(expected: 1L, actual: delta.Pixels);
        Assert.Equal(expected: 7.152, actual: delta.MeanLumaDelta, precision: 9);
        Assert.Null(@object: result.Presented[2].Delta);
        Assert.Contains(expectedSubstring: "differences over region 0.5 0.5 0.5 0.5 (1x1 px at 1,1)", actualString: PresentedFrameReport.Format(frames: result.Presented, region: region), comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: (0, 0, 1, 1), actual: new FrameCaptureRegion(Height: 0.01, Width: 0.01, X: 0, Y: 0).PixelsOf(height: 10, width: 10));
    }
    [Fact]
    public async Task AFailedEncodeOrAWithdrawalFailsTheCapture() {
        var failing = FrameCaptureRequest.Presented(path: "failing.png");

        Assert.True(condition: failing.Present(encode: static (_, _, _, _) => throw new IOException(message: "disk full"), height: 1, presentation: 0, rendered: 0, rgba: Pixels(width: 1, height: 1), tick: null, width: 1));

        var failed = await failing.Completion;

        Assert.Equal(expected: "disk full", actual: failed.Error!.Message);
        Assert.Empty(collection: failed.Presented);

        var withdrawn = FrameCaptureRequest.Presented(path: "withdrawn.png", frames: 2);

        Assert.False(condition: withdrawn.Present(encode: static (_, _, _, _) => { }, height: 1, presentation: 0, rendered: 0, rgba: Pixels(width: 1, height: 1), tick: null, width: 1));
        Assert.True(condition: withdrawn.TryFail(error: new OperationCanceledException(message: "stopped waiting")));
        Assert.True(condition: withdrawn.Present(encode: static (_, _, _, _) => Assert.Fail(message: "a withdrawn capture encodes nothing"), height: 1, presentation: 1, rendered: 1, rgba: Pixels(width: 1, height: 1), tick: null, width: 1));
        Assert.IsType<OperationCanceledException>(@object: (await withdrawn.Completion).Error);
    }
    [Fact]
    public void EachModeRefusesTheOthersServingAndASlotRefusesAPresentedCapture() {
        var settled = new FrameCaptureRequest(path: "settled.png");
        var presented = FrameCaptureRequest.Presented(path: "presented.png");

        Assert.Equal(expected: 1, actual: settled.Frames);
        Assert.Equal(expected: "settled.png", actual: settled.PathOf(index: 0));
        Assert.Equal(expected: "presented.png", actual: presented.PathOf(index: 0));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => presented.PathOf(index: 1));
        Assert.Throws<InvalidOperationException>(testCode: () => settled.Present(encode: static (_, _, _, _) => { }, height: 1, presentation: 0, rendered: 0, rgba: [0, 0, 0, 0], tick: null, width: 1));
        Assert.Throws<InvalidOperationException>(testCode: () => presented.Write(writer: static _ => { }));
        Assert.Throws<InvalidOperationException>(testCode: () => new CaptureRequestSlot().Arm(pendingPath: null, request: presented));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => FrameCaptureRequest.Presented(path: "none.png", frames: 0));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => FrameCaptureRequest.Presented(path: "many.png", frames: (FrameCaptureRequest.MaxPresentedFrames + 1)));
    }
    [Fact]
    public void ARegionLiesInsideTheFrameAndADeltaNeedsWholeFrames() {
        Assert.Null(@object: FrameCaptureRegion.RefusalOf(height: 0.5, width: 0.5, x: 0.5, y: 0.5));
        Assert.NotNull(@object: FrameCaptureRegion.RefusalOf(height: 0.5, width: 0.6, x: 0.5, y: 0));
        Assert.NotNull(@object: FrameCaptureRegion.RefusalOf(height: 0, width: 0.5, x: 0, y: 0));
        Assert.NotNull(@object: FrameCaptureRegion.RefusalOf(height: 0.5, width: 0.5, x: double.NaN, y: 0));
        Assert.Throws<ArgumentException>(testCode: () => PresentedFrameDelta.Of(current: new byte[4], height: 2, previous: new byte[16], region: null, width: 2));
        Assert.Equal(expected: "[capture: presented 0 frames]", actual: PresentedFrameReport.Format(frames: [], region: null));
        Assert.Equal(
            expected: "[capture: presented 1 frame 1x1, differences over the whole frame]\nframe 0 presentation 7 rendered 7 tick none one.png\n[capture: 0 pairs compared, 0 changed, most changed 0 px, largest mean |dY| 0; 0 standing frames; 0 presentations skipped]",
            actual: PresentedFrameReport.Format(frames: [new PresentedFrame(Delta: null, Height: 1, Index: 0, Path: "one.png", Presentation: 7, Rendered: 7, Tick: null, Width: 1)], region: null)
        );
    }
}
