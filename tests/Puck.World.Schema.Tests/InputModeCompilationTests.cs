using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class InputModeCompilationTests {
    [Fact]
    public void DormantVocabularyDoesNotSpendLiveStorage() {
        var channels = Enumerable.Range(count: 120, start: 0).Select(selector: i => new WorldChannel($"action{i:D3}", ChannelShape.Binary, Composition: true)).ToArray();
        var modes = new WorldInputModes([], [new WorldInputModeFamily("focus", new(Kind: WorldInputModeSelectorKind.SeatState, Name: "focus"), "first", [
            new("first", channels.Take(count: 60).Select(selector: static row => row.Name).ToArray()),
            new("second", channels.Skip(count: 60).Select(selector: static row => row.Name).ToArray()),
        ])]);
        var catalog = WorldInputCatalog.Compile(declarations: channels, modes: modes);

        Assert.Equal(120, catalog.Channels.Length);
        Assert.Equal(2, catalog.Mappings.Length);
        Assert.Equal(60, catalog.Mappings[0].Channels.ChannelCount);
        Assert.Equal(60, catalog.Mappings[1].Channels.ChannelCount);
        Assert.True(condition: catalog.TryGetLogicalId(id: out var first, name: "action000"));
        Assert.True(condition: catalog.TryGetLogicalId(id: out var second, name: "action060"));
        Assert.Equal(catalog.Mappings[0].Address(logicalId: first), catalog.Mappings[1].Address(logicalId: second));
        Assert.Equal(-1, catalog.Mappings[1].Address(logicalId: first));
    }
    [Fact]
    public void CapacityRefusalNamesTheTupleBankAndEveryAction() {
        var channels = Enumerable.Range(count: 33, start: 0).Select(selector: i => new WorldChannel($"axis{i:D2}", ChannelShape.Bipolar, Composition: true)).ToArray();
        var modes = new WorldInputModes([], [new("focus", new(Kind: WorldInputModeSelectorKind.SeatState, Name: "focus"), "all", [new("all", channels.Select(selector: static row => row.Name).ToArray())])]);
        var error = Assert.Throws<ArgumentException>(testCode: () => WorldInputCatalog.Compile(declarations: channels, modes: modes));

        Assert.Contains("focus=all", error.Message);
        Assert.Contains("numeric bank requires 33; maximum 32", error.Message);
        foreach (var channel in channels) { Assert.Contains(channel.Name, error.Message); }
    }
    [Fact]
    public void TupleFallbackFitsWhenPreferredColoringExceedsTheBank() {
        // 62 globals plus a five-cycle: every tuple has two other buttons, but a
        // single global graph coloring needs three. Each tuple must still fit.
        var globals = Enumerable.Range(count: 62, start: 0).Select(selector: i => $"global{i:D2}").ToArray();
        var channels = globals.Concat(second: ["a", "b", "c", "d", "e"]).Select(selector: name => new WorldChannel(name, ChannelShape.Binary, Composition: true)).ToArray();
        var modes = new WorldInputModes(globals, [new("pair", new(Kind: WorldInputModeSelectorKind.SeatState, Name: "pair"), "ab", [
            new("ab", ["a", "b"]), new("bc", ["b", "c"]), new("cd", ["c", "d"]), new("de", ["d", "e"]), new("ea", ["e", "a"]),
        ])]);
        var catalog = WorldInputCatalog.Compile(declarations: channels, modes: modes);

        foreach (var mapping in catalog.Mappings) {
            Assert.Equal(64, mapping.Live.Length);
            var slots = mapping.Live.ToArray().Select(selector: id => mapping.Address(logicalId: id)).ToArray();

            Assert.Equal(64, slots.Distinct().Count());
            Assert.All(slots, slot => Assert.InRange(actual: slot, high: 95, low: 32));
        }
    }
    [Fact]
    public void ContinuationIsDirectedAndIntersectsChangedFamilies() {
        WorldChannel[] channels = [new("hold", ChannelShape.Binary, Composition: true)];
        var modes = new WorldInputModes(["hold"], [
            new("form", new(WorldInputModeSelectorKind.ActiveKit), "a", [new("a", []), new("b", [])], Transitions: [new("a", "b", ["hold"])]),
            new("focus", new(WorldInputModeSelectorKind.Engagement), "a", [new("a", []), new("b", [])]),
        ]);
        var catalog = WorldInputCatalog.Compile(declarations: channels, modes: modes);

        Assert.True(condition: catalog.TryGetMapping(mapping: out var start, states: [0, 0]));
        Assert.True(condition: catalog.TryGetMapping(mapping: out var kept, states: [0, 1]));
        Assert.True(condition: catalog.TryGetMapping(mapping: out var released, states: [1, 1]));
        Assert.True(condition: catalog.Keeps(from: start!, logicalId: 0, to: kept!));
        Assert.False(condition: catalog.Keeps(from: kept!, logicalId: 0, to: start!));
        Assert.False(condition: catalog.Keeps(from: start!, logicalId: 0, to: released!));
    }
    [Fact]
    public void RelativeAndInactiveContinuityRefuseByName() {
        var modes = new WorldInputModes([], [new("form", new(WorldInputModeSelectorKind.ActiveKit), "a", [new("a", ["look"]), new("b", ["look"])], Transitions: [new("a", "b", ["look"])])]);
        var error = Assert.Throws<ArgumentException>(testCode: () => WorldInputCatalog.Compile(declarations: [new("look", ChannelShape.Relative, Composition: true, Relative: new(MaximumPerStep: 4, Sensitivity: 1, Unit: "radians"))], modes: modes));

        Assert.Contains("form a -> b: keep look: Relative", error.Message);
        modes = modes with { Families = [modes.Families[0] with { States = [new("a", ["look"]), new("b", [])] }] };
        error = Assert.Throws<ArgumentException>(testCode: () => WorldInputCatalog.Compile(declarations: [new("look", ChannelShape.Binary, Composition: true)], modes: modes));
        Assert.Contains("keep look: no eligible tuple pair", error.Message);
    }
}
