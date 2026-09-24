using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: a <see cref="StateChannelRef"/> is a plain name or a reserved channel as a call. A
/// document holds the first as a string and the second as a node, and never a channel as a string.</summary>
public sealed class StateChannelRefLawTests {
    [InlineData("hp", """
        "hp"
        """)]
    [InlineData("$tick", """
        {"channel":"tick"}
        """)]
    [InlineData("$board:cells:ray:3", """
        {"channel":"board","arguments":["cells","ray",3]}
        """)]
    [InlineData("$cell:hand:$each", """
        {"channel":"cell","arguments":["hand","$each"]}
        """)]
    [Theory]
    public void AReferenceWritesAStringForANameAndANodeForACallAndReadsBackAsItself(string spelling, string json) {
        var reference = StateChannelRef.Parse(spelling: spelling);
        var node = StateChannelRefJsonConverter.ToNode(value: reference);

        Assert.True(condition: JsonNode.DeepEquals(
            node1: node,
            node2: JsonNode.Parse(json: json)
        ));
        Assert.Equal(
            actual: StateChannelRefJsonConverter.FromNode(node: node),
            expected: reference
        );
        Assert.Equal(
            actual: reference.Spelling,
            expected: spelling
        );
    }
    [Fact]
    public void AnExpressionArgumentIsHeldAsAProgramAndAZoneAsItsIndexProgram() {
        var history = StateChannelRefJsonConverter.ToNode(value: StateChannelRef.Parse(spelling: "$history:moves:age + 1"));
        var zone = StateChannelRefJsonConverter.ToNode(value: StateChannelRef.Parse(spelling: "$zones[game[from]]"));

        Assert.NotNull(@object: history["arguments"]![1]!["expression"]!["instructions"]);
        Assert.Equal(
            actual: zone["channel"]!.GetValue<string>(),
            expected: "zones"
        );
        Assert.NotNull(@object: zone["arguments"]![0]!["zone"]!["instructions"]);
        Assert.Equal(
            actual: StateChannelRefJsonConverter.FromNode(node: zone).Spelling,
            expected: "$zones[game[from]]"
        );
    }
    [InlineData("\"$tick\"")]
    [InlineData("\"$board:cells:ray:3\"")]
    [InlineData("\"$zones[game[from]]\"")]
    [Theory]
    public void AChannelHeldAsAStringIsRefused(string json) => Assert.Throws<JsonException>(testCode: () => StateChannelRefJsonConverter.FromNode(node: JsonNode.Parse(json: json)));
    [InlineData("""{"channel":"tick","arguments":null}""")]
    [InlineData("""{"channel":"tick","arguments":"ignored"}""")]
    [InlineData("""{"channel":"tick","arguments":{}}""")]
    [InlineData("""{"channel":"board","arguments":[1e100]}""")]
    [InlineData("""{"channel":"history","arguments":[{"expression":{"instructions":[]},"extra":1}]}""")]
    [InlineData("""{"channel":"zones","arguments":[{"zone":{"instructions":[]},"expression":{"instructions":[]}}]}""")]
    [Theory]
    public void MalformedArgumentsAreRefusedWithoutDiscardingMembersOrLeakingNumericExceptions(string json) =>
        Assert.Throws<JsonException>(testCode: () => StateChannelRefJsonConverter.FromNode(node: JsonNode.Parse(json: json)));
}
