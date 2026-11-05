using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Indexed composition keeps JSON identity equality and isolates public merge results.</summary>
public sealed class WorldCompositionMergeLawTests {
    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1e2", "100")]
    [InlineData("0", "-0")]
    [InlineData("9007199254740993", "9007199254740993.0")]
    [InlineData("\"a/b~c\"", "\"a/b~c\"")]
    public void Indexed_keys_keep_the_same_equality_as_JSON(string basisKey, string overlayKey) {
        var basis = JsonNode.Parse($$"""{"rows":[{"id":{{basisKey}},"kept":1}]}""")!.AsObject();
        var overlay = JsonNode.Parse($$"""{"rows":[{"id":{{overlayKey}},"added":2}]}""")!.AsObject();
        var equal = JsonNode.DeepEquals(basis["rows"]![0]!["id"], overlay["rows"]![0]!["id"]);

        Assert.True(WorldDocumentBasis.TryMerge(basis, overlay, out var composed, out var reason), reason);
        Assert.Equal(equal ? 1 : 2, composed!["rows"]!.AsArray().Count);
        Assert.Equal(1, composed["rows"]![0]!["kept"]!.GetValue<int>());
        Assert.Equal(2, composed["rows"]![equal ? 0 : 1]!["added"]!.GetValue<int>());
    }

    [Fact]
    public void Public_merge_and_refusal_leave_both_inputs_and_prior_results_independent() {
        var basis = JsonNode.Parse("""{"rows":[{"name":"first","nested":{"value":1}},{"name":"second"}]}""")!.AsObject();
        var overlay = JsonNode.Parse("""{"rows":[{"name":"first","nested":{"value":2},"$before":"second"}]}""")!.AsObject();
        var beforeBasis = basis.ToJsonString();
        var beforeOverlay = overlay.ToJsonString();
        Assert.True(WorldDocumentBasis.TryMerge(basis, overlay, out var first, out var reason), reason);
        Assert.True(WorldDocumentBasis.TryMerge(basis, overlay, out var second, out reason), reason);
        first!["rows"]![0]!["nested"]!["value"] = 3;
        Assert.Equal(2, second!["rows"]![0]!["nested"]!["value"]!.GetValue<int>());
        Assert.False(WorldDocumentBasis.TryMerge(basis,
            JsonNode.Parse("""{"rows":[{"name":"first","$before":"missing"}]}""")!.AsObject(), out _, out _));
        Assert.Equal(beforeBasis, basis.ToJsonString());
        Assert.Equal(beforeOverlay, overlay.ToJsonString());
    }

    [Fact]
    public void Drops_compact_survivors_once_before_appends_and_positioning() {
        var basis = JsonNode.Parse("""{"rows":[{"name":"a"},{"name":"b","value":1},{"name":"c"},{"name":"d"},{"name":"e"}]}""")!.AsObject();
        var overlay = JsonNode.Parse("""{"rows":[{"name":"a","$drop":true},{"name":"b","value":2},{"name":"c","$drop":true},{"name":"e","$drop":true},{"name":"new","$before":"b"}]}""")!.AsObject();
        Assert.True(WorldDocumentBasis.TryMerge(basis, overlay, out var composed, out var reason), reason);
        Assert.Equal(new[] { "new", "b", "d" }, composed!["rows"]!.AsArray().Select(row => row!["name"]!.GetValue<string>()));
        Assert.Equal(2, composed["rows"]![1]!["value"]!.GetValue<int>());
        Assert.Equal(5, basis["rows"]!.AsArray().Count);
    }

    [Fact]
    public void Numeric_hash_collisions_still_distinguish_exact_large_integer_keys() {
        var basis = JsonNode.Parse("""{"rows":[{"id":9007199254740992,"kept":1}]}""")!.AsObject();
        var overlay = JsonNode.Parse("""{"rows":[{"id":9007199254740993,"added":2}]}""")!.AsObject();
        Assert.True(WorldDocumentBasis.TryMerge(basis, overlay, out var composed, out var reason), reason);
        Assert.Equal(2, composed!["rows"]!.AsArray().Count);
    }

    [Fact]
    public void Numeric_restatement_keys_match_across_CLR_and_JSON_backings() {
        var first = JsonNode.Parse("""{"rows":[{"id":1,"value":"first"}]}""")!.AsObject();
        var second = JsonNode.Parse("""{"rows":[{"id":1.0,"value":"second"}]}""")!.AsObject();
        var restated = new JsonObject { ["rows"] = new JsonArray(new JsonObject { ["id"] = 1 }) };
        Assert.True(WorldDocumentBasis.TryMergeImports([("first", first), ("second", second)], restated, out _, out var reason), reason);
    }
}
