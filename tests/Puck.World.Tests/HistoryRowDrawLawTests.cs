using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Overlays;
using Puck.Text;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: the scrubber row reaches the editor overlay for a seat that builds, and only for one. With the history on
/// and a window held, a building seat's view gets the row's records — the bar, every keyframe it publishes, each kept
/// branch's fork, the cursor and its label — and a seat that plays gets none; a steady row allocates nothing per frame.
/// </summary>
public sealed class HistoryRowDrawLawTests {
    private static OverlayFrameBuilder Builder() {
        var glyphs = OverlayGlyphSdfPack.TryCreate(monoFont: new FontAtlas(
            kind: FontAtlasKind.Sdf, imagePath: "history-row.png", size: 1f, distanceRange: 1f, width: 1, height: 1,
            metrics: default,
            glyphs: [.. "0123456789".Select(selector: static digit => new FontAtlasGlyph(unicode: digit, advance: 1f, planeBounds: null,
                atlasBounds: new FontAtlasBounds(Bottom: 1f, Left: 0f, Right: 1f, Top: 0f)))],
            kerningPairs: [], imageData: new FontAtlasImageData(height: 1, rgbaPixels: [255, 255, 255, 255], width: 1)
        ));

        Assert.NotNull(@object: glyphs);

        return new OverlayFrameBuilder(glyphs: glyphs, height: 200, width: 400, theme: OverlayThemeValues.Zero,
            leases: new OverlayChannelLeases(capacity: new OverlayCapacity(
                BindingBarMaxBanks: 0, BindingBarMaxModifiers: 0, BindingBarMaxSlotsPerBank: 0,
                HudElementsPerPanel: 0, HudElementsPerSeatPanel: 0, HudPanels: 0, HudSeatPanelsPerSeat: 0,
                MarkerMaxChipsPerSeat: 0, Seats: 1, WheelMaxRings: 0, WheelMaxSectorsPerRing: 0
            )));
    }
    private static int Draw(OverlayFrameBuilder builder, HistoryRowWriter writer) {
        builder.BeginFrame();
        builder.BeginChannel(channel: OverlayChannel.Editor);
        writer.Emit(builder: builder);
        builder.EndChannel();

        return builder.ElementCount;
    }

    [Fact]
    public void ABuildingSeatsViewDrawsTheRowAndAPlayingSeatsDrawsNone() {
        using var harness = new WorldHistoryHarness(seats: 1);
        var bindings = new WorldSeatBindings(definition: harness.Fixture.Server.Definition);
        var viewports = new WorldSeatViewports();
        var row = new WorldHistoryRow(bindings: bindings, history: harness.History, viewports: viewports);
        var writer = new HistoryRowWriter(source: row, theme: new OverlayThemeStore());
        var builder = Builder();

        harness.Steps(count: 60);
        Assert.True(condition: harness.History.TrySeek(documentPath: null, refusal: out var refusal, report: out _, target: 30UL), userMessage: refusal);
        Assert.True(condition: harness.History.TryArmBranch(name: "kept", refusal: out refusal), userMessage: refusal);
        harness.Steps(count: 5);
        viewports.Publish(camera: default(CameraSnapshot), height: 200, region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f), slot: 0, width: 400);

        // The red leg: a seat that plays draws nothing.
        Assert.Equal(expected: 0, actual: Draw(builder: builder, writer: writer));

        bindings.SetContextState(family: WorldContextFamilies.Editor, slot: 0, state: WorldContextFamilies.EditorBuild);
        Span<ulong> keyframes = stackalloc ulong[WorldHistory.RowKeyframes];
        Span<WorldHistoryRowFork> forks = stackalloc WorldHistoryRowFork[WorldHistory.RowForks];

        Assert.True(condition: harness.History.TryReadRow(forkCount: out var forkCount, forks: forks, keyframeCount: out var keyframeCount, keyframes: keyframes, window: out _));
        Assert.Equal(actual: forkCount, expected: 1);

        // The bar, each keyframe, each fork and the cursor are one record apiece; the label writes at least one more.
        var drawn = Draw(builder: builder, writer: writer);

        Assert.True(condition: (drawn > (((1 + keyframeCount) + forkCount) + 1)), userMessage: $"drew {drawn} records for {keyframeCount} keyframe(s) and {forkCount} fork(s)");

        for (var warm = 0; (warm < 10); warm++) {
            _ = Draw(builder: builder, writer: writer);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var frame = 0; (frame < 30); frame++) {
            _ = Draw(builder: builder, writer: writer);
        }

        Assert.Equal(expected: 0L, actual: (GC.GetAllocatedBytesForCurrentThread() - before));
    }
}
