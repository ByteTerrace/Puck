using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class MemberSpellingRegressionTests {
    private static JsonObject Compile(string source) {
        var result = WorldCompiler.Compile(source: source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: result.Diagnostics.HasErrors, userMessage: string.Join(separator: "\n", values: result.Diagnostics));
        return result.Json!;
    }

    [InlineData("cards-in-play")]
    [InlineData("cards in play")]
    [InlineData("null")]
    [InlineData("cards(active)")]
    [InlineData("cards[active]")]
    [InlineData("cards`{active}")]
    [Theory]
    public void DecompiledNameRetainsItsIdentity(string name) {
        var original = Compile(source: $$"""
            schema: "puck.world.definition.v1"
            state {
                world {
                    table deck { a = 1 }
                    slot hp = 0
                }
            }
            rule "r" {
                forEach: deck
                hp = 1
            }
            """);

        original["state"]!["world"]![0]!["name"] = name;
        original["rules"]![0]!["forEach"] = name;
        var printed = WorldDecompiler.Decompile(root: original);
        var restored = Compile(source: printed);

        Assert.True(condition: JsonNode.DeepEquals(node1: original, node2: restored), userMessage: printed);
    }
    [InlineData("right.hits")]
    [InlineData("$\"right.hits\"")]
    [Theory]
    public void InteractionObjectBindsPoolSides(string reference) {
        var document = Compile(source: $$"""
            schema: "puck.world.definition.v1"
            state {
                record Token { hits: Int bounds(0..99) = 0 }
                pool tokens of Token capacity(2) = [{ hits: 0 }, { hits: 0 }]
            }
            interactions {
                interactions [
                    {
                        name: "strike"
                        left: tokens
                        right: tokens
                        coOccurrence: Distance
                        range: 1
                        effects [addState(state: {{reference}}, value: 1)]
                    }
                ]
            }
            """);

        Assert.Equal("right", document["interactions"]!["interactions"]![0]!["effects"]![0]!["state"]!["binding"]!.GetValue<string>());
    }
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void InteractionsPassedThroughBindingStillBindTheirSides(bool evaluateDataFirst, bool shadowConstant) {
        var document = Compile(source: $$"""
            schema: "puck.world.definition.v1"
            state {
                record Token { hits: Int bounds(0..99) = 0 }
                pool tokens of Token capacity(2) = [{ hits: 0 }, { hits: 0 }]
            }
            let amount = 1
            let settings = {
                interactions [
                    {
                        name: "strike"
                        left: tokens
                        right: tokens
                        coOccurrence: Distance
                        range: 1
                        effects [addState(state: right.hits, expression: right.hits + amount)]
                    }
                ]
            }
            {{(evaluateDataFirst ? "let cached = { interactions: settings }\nprobe: cached" : "")}}
            {{(shadowConstant ? "template emit(amount) { interactions: settings }\nemit(7)" : "interactions: settings")}}
            """);

        Assert.Equal("right", document["interactions"]!["interactions"]![0]!["effects"]![0]!["state"]!["binding"]!.GetValue<string>());
        var expression = document["interactions"]!["interactions"]![0]!["effects"]![0]!["expression"]!;

        Assert.Equal("right", expression["instructions"]![0]!["name"]!["binding"]!.GetValue<string>());
        Assert.Equal("hits", expression["instructions"]![0]!["name"]!["field"]!.GetValue<string>());
        Assert.Equal(1m, expression["instructions"]![1]!["value"]!.GetValue<decimal>());
    }
}
