using Puck.Abstractions.Counting;

namespace Puck.Hosting.Tests;

public sealed class ConsoleTapeTests {
    [Fact]
    public void DeferredHistoryKeepsEchoOrderAndOlderSnapshotsAcrossWrapAndVisibilityChanges() {
        var store = new ConsoleTapeStore();
        var tape = new ConsoleTape(store: store);

        tape.RecordEcho("first", refused: true);
        Assert.True(condition: store.TrySnapshot(out var first));
        Assert.Equal(new ConsoleTapeLine(Refused: true, Text: "first"), Assert.Single(collection: first.Lines));

        for (var index = 0; (index < 70); index++) { tape.RecordEcho(index.ToString(), refused: false); }
        Assert.True(condition: store.TrySnapshot(out var hidden, includeHiddenHistory: false));
        Assert.False(condition: hidden.Visible);
        tape.SetVisible(visible: true);
        Assert.True(condition: store.TrySnapshot(out var shown));
        Assert.Equal(64, shown.Lines.Count);
        Assert.Equal("6", shown.Lines[0].Text);
        Assert.Equal("69", shown.Lines[^1].Text);
        Assert.Equal(new ConsoleTapeLine(Refused: true, Text: "first"), Assert.Single(collection: first.Lines));
        Assert.True(condition: store.TrySnapshot(out var unchanged));
        Assert.Same(shown.Lines, unchanged.Lines);

        tape.SetVisible(visible: false);
        tape.RecordEcho("last", refused: true);
        Assert.True(condition: store.TrySnapshot(out var diagnostic));
        Assert.False(condition: diagnostic.Visible);
        Assert.Equal(new ConsoleTapeLine(Refused: true, Text: "last"), diagnostic.Lines[^1]);
        Assert.Equal("69", shown.Lines[^1].Text);
    }
    [Fact]
    public void EchoesAndHiddenRenderingDoNotAllocateSnapshots() {
        var store = new ConsoleTapeStore();
        var tape = new ConsoleTape(store: store);
        Action record = () => {
            for (var index = 0; (index < 128); index++) {
                tape.RecordEcho("echo", refused: false);
                _ = store.TrySnapshot(out _, includeHiddenHistory: false);
            }
        };

        record();
        Assert.Equal(0, AllocationWindow.Least(record));
        Assert.True(condition: store.TrySnapshot(out var frame));
        Assert.Equal(64, frame.Lines.Count);
    }
}
