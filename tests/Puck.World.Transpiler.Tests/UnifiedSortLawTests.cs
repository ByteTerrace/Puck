using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class UnifiedSortLawTests {
    [Fact]
    public void ArraySortingAndDocumentSortingResolveByTheirPosition() {
        var result = WorldCompiler.Compile("""
            schema: "puck.world.definition.v1"
            let ordered = sort([3, 1, 2])
            state { world { slot first = ordered[0] } }
            rules [{ name: "order"
                effects [transformState(transform: sort(row: scores, by: [{ row: scores }]))]
            }]
            """, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(condition: result.Diagnostics.HasErrors, userMessage: result.Diagnostics.FormatReport("sort.puck"));
        var json = result.RequireJson();
        Assert.Equal("sort", json["rules"]![0]!["effects"]![0]!["transform"]!["$type"]!.GetValue<string>());
        Assert.Equal(1L, json["state"]!["world"]![0]!["value"]!.GetValue<long>());
    }

    [Theory]
    [InlineData("scores", "scores")]
    [InlineData("hand", "rank")]
    public void OwnValuesAndAttributesUseTheSameAuthoredKeyList(string target, string key) {
        var result = WorldCompiler.Compile($$"""
            schema: "puck.world.definition.v1"
            rule order {
                transform sort(row: {{target}}, by: [{ row: {{key}}, descending: true }])
            }
            """, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(condition: result.Diagnostics.HasErrors, userMessage: result.Diagnostics.FormatReport("sort.puck"));
        var transform = result.RequireJson()["rules"]![0]!["effects"]![0]!["transform"]!;
        Assert.Equal("sort", transform["$type"]!.GetValue<string>());
        Assert.Equal(target, transform["row"]!.GetValue<string>());
        var by = Assert.Single(collection: transform["by"]!.AsArray());
        Assert.Equal(key, by!["row"]!.GetValue<string>());
        Assert.True(condition: by["descending"]!.GetValue<bool>());
    }
}
