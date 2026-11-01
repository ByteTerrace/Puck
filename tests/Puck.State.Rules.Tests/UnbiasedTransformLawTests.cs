using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>Forced rejected raw words exercise gameplay writes and continuation in one journal scope.</summary>
public sealed class UnbiasedTransformLawTests {
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    [Theory]
    public void ShuffleAndTransferPersistEveryRejectedWordAndRewindTheWholeState(bool shuffle, int rejected) {
        var section = TransformFixture.Section();
        var rows = section.Rows!.ToArray();
        var site = Array.FindIndex(array: rows, match: row => (row.Name.Value == "coin"));

        rows[site] = rows[site] with {
            Draw = new Draw(Generator: new StateGenerator(Source: GeneratorSource.StreamDraw,
            Extended: new GeneratorExtended(8, Script: Enumerable.Repeat(count: rejected, element: 0L).Concat(second: [uint.MaxValue, uint.MaxValue, uint.MaxValue]).ToArray())), Timing: DrawTiming.Event),
        };
        section = section with { Rows = rows };
        var context = TransformFixture.Context(section: section);
        var host = TransformFixture.Host(context: context, section: section);
        StateTransform transform = (shuffle ? new StateTransform.Shuffle(Draw: "coin", Row: "deck")
            : new StateTransform.Transfer("deck", "hand", ZoneSelector.Random, Count: 2, Draw: "coin"));

        Assert.True(condition: RuleCompiler.TryResolveTransform(context: context, reason: out var reason, resolved: out var resolved, transform: transform), userMessage: reason);
        var before = host.Arena.ComputeHash();
        var mark = host.Arena.BeginScope();

        Assert.True(condition: host.TryTransform(resolved, ArenaTransformBinding.None, out _, out var refusal), userMessage: refusal.Reason);
        Assert.Equal((rejected + 2), host.Arena.DrawCursor(rowOrdinal: site));
        var after = host.Arena.ComputeHash();

        host.Arena.Rewind(mark: mark);
        Assert.Equal(before, host.Arena.ComputeHash());
        Assert.Equal(0, host.Arena.DrawCursor(rowOrdinal: site));
        mark = host.Arena.BeginScope();
        Assert.True(condition: host.TryTransform(resolved, ArenaTransformBinding.None, out _, out refusal), userMessage: refusal.Reason);
        Assert.Equal(after, host.Arena.ComputeHash());
        host.Arena.Commit(mark: mark);
        var deck = TransformFixture.Ordinal(context: context, name: "deck");

        Assert.Equal((shuffle ? "a=1,b=2,c=3" : "a=1"), TransformFixture.Listing(arena: host.Arena, rowOrdinal: deck));
    }
    [Fact]
    public void RefusalAfterAnEarlierTransferAndARejectedWordRewindsBoth() {
        var section = TransformFixture.Section();
        var rows = section.Rows!.ToArray();
        var site = Array.FindIndex(array: rows, match: row => (row.Name.Value == "coin"));
        // First selection succeeds, the next operation encounters only rejected words until its budget refuses.
        rows[site] = rows[site] with {
            Draw = new Draw(Generator: new StateGenerator(Source: GeneratorSource.StreamDraw,
            Extended: new GeneratorExtended(128, Script: new long[] { uint.MaxValue }.Concat(second: new long[64]).ToArray())), Timing: DrawTiming.Event),
        };
        section = section with { Rows = rows };
        var context = TransformFixture.Context(section: section);
        var host = TransformFixture.Host(context: context, section: section);
        var deck = TransformFixture.Ordinal(context: context, name: "deck");
        var before = host.Arena.ComputeHash();
        var mark = host.Arena.BeginScope();

        Assert.True(condition: host.TryTransform(new ArenaTransform.Transfer(Count: 1, DrawRowOrdinal: site, FromRowOrdinal: deck, InsertFirst: true, Key: default, Selector: ZoneSelector.Random, ToRowOrdinal: deck), ArenaTransformBinding.None, out _, out var refusal), userMessage: refusal.Reason);
        Assert.Equal("c=3,a=1,b=2", TransformFixture.Listing(arena: host.Arena, rowOrdinal: deck));
        Assert.Equal(1, host.Arena.DrawCursor(rowOrdinal: site));
        Assert.False(condition: host.TryTransform(new ArenaTransform.Shuffle(DrawRowOrdinal: site, RowOrdinal: deck), ArenaTransformBinding.None, out _, out refusal));
        Assert.Contains("budget", refusal.Reason);
        host.Arena.Rewind(mark: mark);
        Assert.Equal(before, host.Arena.ComputeHash());
        Assert.Equal(0, host.Arena.DrawCursor(rowOrdinal: site));
    }
}
