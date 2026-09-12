using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

public sealed class CartridgeWriteAnalysisTests {
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void GuardWriteIntervalIncludesBothEndpoints(int writeAt, bool exclusive) {
        var rules = Enumerable.Range(0, 5).Select(index => new CartridgeRule(Name: $"rule{index}",
            Body: index == writeAt ? [Set("phase")] : [])).ToArray();
        (string? Name, int Value)[] guards = [(null, 0), ("phase", 0), (null, 0), ("phase", 1), (null, 0)];
        var result = CartridgeCost.ExclusiveGuards(rules: rules, guards: guards);
        Assert.Equal(expected: exclusive, actual: result.Contains("phase"));
    }

    [Fact]
    public void NestedWritesDisqualifyOnlyTheirOwnActiveGuards() {
        CartridgeStatement[] body = [new(Kind: "if", Then: [Set("left")], Else: [new(Kind: "repeat",
            Count: 2, Index: "loop", Body: [Set("scene"), new(Kind: "set", Target: new CartridgeTarget(State: "array", Key: "0"))])])];
        string[] names = ["left", "loop", "scene", "array", "kept"];
        var rules = names.Select(name => new CartridgeRule(Name: name, Body: Array.Empty<CartridgeStatement>())).ToList();
        rules.Add(new CartridgeRule(Name: "writer", Body: body));
        rules.AddRange(names.Select(name => new CartridgeRule(Name: name + "End", Body: Array.Empty<CartridgeStatement>())));
        var guards = names.Select(name => ((string?)name, 0)).Concat(new (string?, int)[] { (null, 0) })
            .Concat(names.Select(name => ((string?)name, 1))).ToArray();
        var result = CartridgeCost.ExclusiveGuards(rules: rules.ToArray(), guards: guards, scene: "scene");
        Assert.Equal(expected: new[] { "array", "kept", "scene" }, actual: result.Order(StringComparer.Ordinal).ToArray());
        foreach (var name in new[] { "left", "loop", "scene" }) { Assert.True(CartridgeEffects.Writes(body, name)); }
        Assert.False(CartridgeEffects.Writes(body, "array"));
        Assert.False(CartridgeEffects.Writes(body, "kept"));
    }

    [Theory]
    [InlineData("load")]
    [InlineData("clock")]
    public void ImplicitWritersUseDeclaredDestinationsAndRemainConservativeWithoutADocument(string kind) {
        var document = CartridgeDocuments.Create(target: "agb", title: "WRITES") with {
            Save = new CartridgeSave(Version: 1, Variables: ["changed"], Arrays: []),
            Clock = new CartridgeClock(Seconds: null, Minutes: "changed", Hours: null, Days: null),
        };
        CartridgeRule[] rules = [new(Name: "first", Body: []), new(Name: "firstOther", Body: []),
            new(Name: "write", Body: [new(Kind: kind)]), new(Name: "last", Body: []), new(Name: "lastOther", Body: [])];
        (string? Name, int Value)[] guards = [("changed", 0), ("kept", 0), (null, 0), ("changed", 1), ("kept", 1)];
        Assert.Equal(expected: new[] { "kept" }, actual: CartridgeCost.ExclusiveGuards(rules, guards, document: document).ToArray());
        Assert.Empty(CartridgeCost.ExclusiveGuards(rules, guards));
        Assert.True(CartridgeEffects.Writes(rules[2].Body, "changed", document));
        Assert.False(CartridgeEffects.Writes(rules[2].Body, "kept", document));
        Assert.True(CartridgeEffects.Writes(rules[2].Body, "kept"));
    }

    [Fact]
    public void ManyOverlappingGuardsKeepIndependentWriteIntervals() {
        const int Count = 128;
        var rules = Enumerable.Range(0, Count * 2).Select(index => new CartridgeRule(Name: $"rule{index}",
            Body: index == Count ? [Set("g0"), Set("g127")] : [Set("unrelated")])).ToArray();
        var guards = Enumerable.Range(0, Count * 2).Select(index => ((string?)$"g{index % Count}", index / Count)).ToArray();
        var result = CartridgeCost.ExclusiveGuards(rules, guards);
        Assert.Equal(expected: Count - 2, actual: result.Count);
        Assert.DoesNotContain("g0", result);
        Assert.DoesNotContain("g127", result);
    }

    private static CartridgeStatement Set(string name) => new(Kind: "set", Target: new CartridgeTarget(State: name));
}
