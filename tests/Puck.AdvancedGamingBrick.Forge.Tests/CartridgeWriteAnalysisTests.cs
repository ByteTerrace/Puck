using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

public sealed class CartridgeWriteAnalysisTests {
    private static CartridgeStatement Set(string name) => new(
        Kind: "set",
        Target: new CartridgeTarget(State: name)
    );

    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [Theory]
    public void GuardWriteIntervalIncludesBothEndpoints(int writeAt, bool exclusive) {
        var rules = Enumerable.Range(
            count: 5,
            start: 0
        ).Select(selector: index => new CartridgeRule(
            Name: $"rule{index}",
            Body: ((index == writeAt)
            ? [Set(name: "phase")]
            : [])
        )).ToArray();
        (string? Name, int Value)[] guards = [(null, 0), ("phase", 0), (null, 0), ("phase", 1), (null, 0)];
        var result = CartridgeCost.ExclusiveGuards(
            rules: rules,
            guards: guards
        );

        Assert.Equal(
            expected: exclusive,
            actual: result.Contains(item: "phase")
        );
    }
    [InlineData("load")]
    [InlineData("clock")]
    [Theory]
    public void ImplicitWritersUseDeclaredDestinationsAndRemainConservativeWithoutADocument(string kind) {
        var document = CartridgeDocuments.Create(
            target: "agb",
            title: "WRITES"
        ) with {
            Save = new CartridgeSave(
            Arrays: [],
            Variables: ["changed"],
            Version: 1
        ),
            Clock = new CartridgeClock(
            Seconds: null,
            Minutes: "changed",
            Hours: null,
            Days: null
        ),
        };
        CartridgeRule[] rules = [new(
                Name: "first",
                Body: []
            ), new(
                Name: "firstOther",
                Body: []
            ),
            new(
                Name: "write",
                Body: [new(Kind: kind)]
            ), new(
                Name: "last",
                Body: []
            ), new(
                Name: "lastOther",
                Body: []
            )];
        (string? Name, int Value)[] guards = [("changed", 0), ("kept", 0), (null, 0), ("changed", 1), ("kept", 1)];

        Assert.Equal(
            expected: new[] { "kept" },
            actual: CartridgeCost.ExclusiveGuards(
                rules,
                guards,
                document: document
            ).ToArray()
        );
        Assert.Empty(collection: CartridgeCost.ExclusiveGuards(
            rules,
            guards
        ));
        Assert.True(condition: CartridgeEffects.Writes(
            rules[2].Body,
            "changed",
            document
        ));
        Assert.False(condition: CartridgeEffects.Writes(
            rules[2].Body,
            "kept",
            document
        ));
        Assert.True(condition: CartridgeEffects.Writes(
            rules[2].Body,
            "kept"
        ));
    }
    [Fact]
    public void ManyOverlappingGuardsKeepIndependentWriteIntervals() {
        const int Count = 128;
        var rules = Enumerable.Range(
            count: (Count * 2),
            start: 0
        ).Select(selector: index => new CartridgeRule(
            Name: $"rule{index}",
            Body: ((index == Count)
            ? [Set(name: "g0"), Set(name: "g127")]
            : [Set(name: "unrelated")])
        )).ToArray();
        var guards = Enumerable.Range(
            count: (Count * 2),
            start: 0
        ).Select(selector: index => (((string?)$"g{(index % Count)}"), (index / Count))).ToArray();
        var result = CartridgeCost.ExclusiveGuards(
            rules,
            guards
        );

        Assert.Equal(
            expected: (Count - 2),
            actual: result.Count
        );
        Assert.DoesNotContain(
            expected: "g0",
            set: result
        );
        Assert.DoesNotContain(
            expected: "g127",
            set: result
        );
    }
    [Fact]
    public void NestedWritesDisqualifyOnlyTheirOwnActiveGuards() {
        CartridgeStatement[] body = [new(
                Kind: "if",
                Then: [Set(name: "left")],
                Else: [new(
                        Kind: "repeat",
                        Count: 2,
                        Index: "loop",
                        Body: [Set(name: "scene"), new(
                                Kind: "set",
                                Target: new CartridgeTarget(
                                    Key: "0",
                                    State: "array"
                                )
                            )]
                    )]
            )];
        string[] names = ["left", "loop", "scene", "array", "kept"];
        var rules = names.Select(selector: name => new CartridgeRule(
            Name: name,
            Body: Array.Empty<CartridgeStatement>()
        )).ToList();

        rules.Add(item: new CartridgeRule(
            Name: "writer",
            Body: body
        ));
        rules.AddRange(collection: names.Select(selector: name => new CartridgeRule(
            Name: (name + "End"),
            Body: Array.Empty<CartridgeStatement>()
        )));
        var guards = names.Select(selector: name => (((string?)name), 0)).Concat(second: new (string?, int)[] { (null, 0) })
            .Concat(second: names.Select(selector: name => (((string?)name), 1))).ToArray();
        var result = CartridgeCost.ExclusiveGuards(
            rules: rules.ToArray(),
            guards: guards,
            scene: "scene"
        );

        Assert.Equal(
            expected: new[] { "array", "kept", "scene" },
            actual: result.Order(comparer: StringComparer.Ordinal).ToArray()
        );
        foreach (var name in new[] { "left", "loop", "scene" }) {
            Assert.True(condition: CartridgeEffects.Writes(
            body,
            name
        ));
        }
        Assert.False(condition: CartridgeEffects.Writes(
            body,
            "array"
        ));
        Assert.False(condition: CartridgeEffects.Writes(
            body,
            "kept"
        ));
    }
}
