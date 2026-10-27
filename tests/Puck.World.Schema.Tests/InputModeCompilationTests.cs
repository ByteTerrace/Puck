using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class InputModeCompilationTests {
    [Fact]
    public void DormantVocabularyDoesNotSpendLiveStorage() {
        var channels = Enumerable.Range(0, 120).Select(i => new WorldChannel($"action{i:D3}", ChannelShape.Binary, Composition: true)).ToArray();
        var modes = new WorldInputModes([], [new WorldInputModeFamily("focus", new(WorldInputModeSelectorKind.SeatState, "focus"), "first", [
            new("first", channels.Take(60).Select(static row => row.Name).ToArray()),
            new("second", channels.Skip(60).Select(static row => row.Name).ToArray()),
        ])]);
        var catalog = WorldInputCatalog.Compile(channels, modes);
        Assert.Equal(120, catalog.Channels.Length);
        Assert.Equal(2, catalog.Mappings.Length);
        Assert.Equal(60, catalog.Mappings[0].Channels.ChannelCount);
        Assert.Equal(60, catalog.Mappings[1].Channels.ChannelCount);
        Assert.True(catalog.TryGetLogicalId("action000", out var first));
        Assert.True(catalog.TryGetLogicalId("action060", out var second));
        Assert.Equal(catalog.Mappings[0].Address(first), catalog.Mappings[1].Address(second));
        Assert.Equal(-1, catalog.Mappings[1].Address(first));
    }

    [Fact]
    public void CapacityRefusalNamesTheTupleBankAndEveryAction() {
        var channels = Enumerable.Range(0, 33).Select(i => new WorldChannel($"axis{i:D2}", ChannelShape.Bipolar, Composition: true)).ToArray();
        var modes = new WorldInputModes([], [new("focus", new(WorldInputModeSelectorKind.SeatState, "focus"), "all", [new("all", channels.Select(static row => row.Name).ToArray())])]);
        var error = Assert.Throws<ArgumentException>(() => WorldInputCatalog.Compile(channels, modes));
        Assert.Contains("focus=all", error.Message);
        Assert.Contains("numeric bank requires 33; maximum 32", error.Message);
        foreach (var channel in channels) { Assert.Contains(channel.Name, error.Message); }
    }

    [Fact]
    public void TupleFallbackFitsWhenPreferredColoringExceedsTheBank() {
        // 62 globals plus a five-cycle: every tuple has two other buttons, but a
        // single global graph coloring needs three. Each tuple must still fit.
        var globals = Enumerable.Range(0, 62).Select(i => $"global{i:D2}").ToArray();
        var channels = globals.Concat(["a", "b", "c", "d", "e"]).Select(name => new WorldChannel(name, ChannelShape.Binary, Composition: true)).ToArray();
        var modes = new WorldInputModes(globals, [new("pair", new(WorldInputModeSelectorKind.SeatState, "pair"), "ab", [
            new("ab", ["a", "b"]), new("bc", ["b", "c"]), new("cd", ["c", "d"]), new("de", ["d", "e"]), new("ea", ["e", "a"]),
        ])]);
        var catalog = WorldInputCatalog.Compile(channels, modes);
        foreach (var mapping in catalog.Mappings) {
            Assert.Equal(64, mapping.Live.Length);
            var slots = mapping.Live.ToArray().Select(id => mapping.Address(id)).ToArray();
            Assert.Equal(64, slots.Distinct().Count());
            Assert.All(slots, slot => Assert.InRange(slot, 32, 95));
        }
    }

    [Fact]
    public void ContinuationIsDirectedAndIntersectsChangedFamilies() {
        WorldChannel[] channels = [new("hold", ChannelShape.Binary, Composition: true)];
        var modes = new WorldInputModes(["hold"], [
            new("form", new(WorldInputModeSelectorKind.ActiveKit), "a", [new("a", []), new("b", [])], Transitions: [new("a", "b", ["hold"])]),
            new("focus", new(WorldInputModeSelectorKind.Engagement), "a", [new("a", []), new("b", [])]),
        ]);
        var catalog = WorldInputCatalog.Compile(channels, modes);
        Assert.True(catalog.TryGetMapping([0, 0], out var start));
        Assert.True(catalog.TryGetMapping([0, 1], out var kept));
        Assert.True(catalog.TryGetMapping([1, 1], out var released));
        Assert.True(catalog.Keeps(start!, kept!, 0));
        Assert.False(catalog.Keeps(kept!, start!, 0));
        Assert.False(catalog.Keeps(start!, released!, 0));
    }

    [Fact]
    public void RelativeAndInactiveContinuityRefuseByName() {
        var modes = new WorldInputModes([], [new("form", new(WorldInputModeSelectorKind.ActiveKit), "a", [new("a", ["look"]), new("b", ["look"])], Transitions: [new("a", "b", ["look"])])]);
        var error = Assert.Throws<ArgumentException>(() => WorldInputCatalog.Compile([new("look", ChannelShape.Relative, Composition: true, Relative: new("radians", 1, 4))], modes));
        Assert.Contains("form a -> b: keep look: Relative", error.Message);
        modes = modes with { Families = [modes.Families[0] with { States = [new("a", ["look"]), new("b", [])] }] };
        error = Assert.Throws<ArgumentException>(() => WorldInputCatalog.Compile([new("look", ChannelShape.Binary, Composition: true)], modes));
        Assert.Contains("keep look: no eligible tuple pair", error.Message);
    }
}
