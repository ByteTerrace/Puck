using System.Text.Json.Nodes;
using Xunit;
using Puck.Testing;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: a shipped document holds a reserved channel as a call node under every member the
/// model types as a <see cref="StateChannelRef"/>, never as a string with a grammar of its own, and
/// <see cref="ChannelSpelling"/> reads and writes the colon spelling of each one without loss.</summary>
public sealed class ChannelSpellingShippedWorldLawTests {
    private static bool Holds(Type memberType) => ((Nullable.GetUnderlyingType(nullableType: memberType) ?? memberType) == typeof(StateChannelRef));

    [Fact]
    public void EveryShippedReferenceIsAPlainNameOrACallNodeThatReadsBackAsItself() {
        var calls = 0;
        var failures = new List<string>();

        foreach (var path in WorldFactsCompilerShippedWorldLawTests.Worlds().Select(selector: static row => ((string)row.Data))) {
            WorldModuleNamespace.Visit(
                node: JsonNode.Parse(utf8Json: ShippedWorldDocuments.Read(path: path)),
                type: typeof(WorldDefinition),
                visitor: (_, jsonName, value, _, memberType) => {
                    if (!Holds(memberType: memberType)) {
                        return;
                    }
                    if (
                        (value is JsonValue leaf) &&
                        leaf.TryGetValue<string>(value: out var text)
                    ) {
                        if (StateChannelRef.Parse(spelling: text).Call is not null) {
                            failures.Add(item: $"{Path.GetFileName(path: path)} {jsonName}: '{text}' is a channel held as a string");
                        }

                        return;
                    }

                    var reference = StateChannelRefJsonConverter.FromNode(node: value);

                    calls++;
                    if (
                        !JsonNode.DeepEquals(
                            node1: StateChannelRefJsonConverter.ToNode(value: reference),
                            node2: value
                        ) ||
                        ((reference.PoolField is null) && (StateChannelRef.Parse(spelling: reference.Spelling) != reference))
                    ) {
                        failures.Add(item: $"{Path.GetFileName(path: path)} {jsonName}: '{reference.Spelling}' does not read back as itself");
                    }
                }
            );
        }

        Assert.NotEqual(
            actual: calls,
            expected: 0
        );
        Assert.Empty(collection: failures);
    }

    [InlineData("$tick", "tick", 0)]
    [InlineData("$reduce:count:hand:where:live:between:-1:3.5", "reduce", 7)]
    [InlineData("$match:run:$zones[game[from]]:prefix", "match", 3)]
    [InlineData("$history:moves:age > 0 ? age : 0", "history", 2)]
    [InlineData("$expr:from + (turn == 0 ? 8 : -8)", "expr", 1)]
    [Theory]
    public void AChannelSplitsOnItsOwnColonsAndNoOthers(string spelling, string channel, int arguments) {
        Assert.True(condition: ChannelSpelling.TryParse(
            call: out var call,
            text: spelling
        ));
        Assert.Equal(
            actual: call.Channel,
            expected: channel
        );
        Assert.Equal(
            actual: call.Count,
            expected: arguments
        );
        Assert.Equal(
            actual: ChannelSpelling.Print(call: call),
            expected: spelling
        );
    }
    [InlineData("hp")]
    [InlineData("$zones[game[from]]")]
    [InlineData(null)]
    [Theory]
    public void ARowNameAndALiveZoneAreNotChannels(string? spelling) => Assert.False(condition: ChannelSpelling.TryParse(
        call: out _,
        text: spelling
    ));
}
