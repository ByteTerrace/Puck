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

    // A read's name and key are each a plain string or one of the reference object's three closed shapes. Other
    // objects encountered while recursively walking a state section are document structure, not references.
    private static bool IsReferenceObject(JsonObject value) => (
        (((value.Count == 1) || ((value.Count == 2) && value.ContainsKey(propertyName: "arguments"))) && value.ContainsKey(propertyName: "channel")) ||
        ((value.Count == 2) && value.ContainsKey(propertyName: "binding") && value.ContainsKey(propertyName: "field")) ||
        ((value.Count == 3) && value.ContainsKey(propertyName: "pool") && value.ContainsKey(propertyName: "slot") && value.ContainsKey(propertyName: "field"))
    );
    private static StateChannelRef? Reference(JsonNode? node) => (node switch {
        JsonValue value when value.TryGetValue<string>(value: out var text) => StateChannelRef.OfName(name: text),
        JsonObject reference when IsReferenceObject(value: reference) => StateChannelRefJsonConverter.FromNode(node: reference),
        _ => null,
    });
    private static void CollectReads(JsonNode? node, HashSet<(StateChannelRef Name, StateChannelRef? Key)> into) {
        switch (node) {
            case JsonObject map:
                foreach (var (name, key) in new[] { ("name", "key"), ("state", "key"), ("fromState", "fromKey"), ("comparandState", "comparandKey") }) {
                    if (
                        (Reference(node: map[name]) is { } reference) &&
                        ((name != "name") || (map["op"]?.ToString() == "Operand"))
                    ) {
                        _ = into.Add(item: (reference, Reference(node: map[key])));
                    }
                }
                foreach (var pair in map) {
                    CollectReads(
                        into: into,
                        node: pair.Value
                    );
                }

                break;
            case JsonArray list:
                foreach (var item in list) {
                    CollectReads(
                        into: into,
                        node: item
                    );
                }

                break;
        }
    }

    // The source dialect is a second spelling of the same read: what it prints, read back inside a rule that
    // declares the locals it names, is the instruction it was printed from.
    [Fact]
    public void EveryShippedReadPrintsInTheSourceDialectAndReadsBackAsItself() {
        var reads = new HashSet<(StateChannelRef Name, StateChannelRef? Key)>();

        foreach (var path in WorldFactsCompilerShippedWorldLawTests.Worlds().Select(selector: static row => ((string)row.Data))) {
            CollectReads(
                into: reads,
                node: JsonNode.Parse(utf8Json: ShippedWorldDocuments.Read(path: path))
            );
        }

        Assert.NotEmpty(collection: reads);

        var failures = new List<string>();

        foreach (var (name, key) in reads) {
            var program = new ExpressionProgram(Instructions: [Instruction.Operand(
                key: key,
                name: name
            )]);
            var locals = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var spelled in new[] { name.Spelling, (key?.Spelling ?? string.Empty) }) {
                foreach (System.Text.RegularExpressions.Match local in System.Text.RegularExpressions.Regex.Matches(
                    input: spelled,
                    pattern: @"\$local:([A-Za-z_][A-Za-z0-9_]*)"
                )) {
                    _ = locals.Add(item: local.Groups[1].Value);
                }
            }

            if (
                !ExpressionSpelling.TryPrintSource(
                    program: program,
                    text: out var source
                ) ||
                !ExpressionSpelling.TryParse(
                    error: out var error,
                    locals: locals,
                    program: out var back,
                    text: source
                ) ||
                (back.Instructions.Count != 1) ||
                (back.Instructions[0] != program.Instructions[0])
            ) {
                failures.Add(item: $"{name.Spelling} [{key?.Spelling}]");
            } else if (source.Contains(value: "$local:") || source.Contains(value: "$reduce:") || source.Contains(value: "$board:")) {
                failures.Add(item: $"{name.Spelling} [{key?.Spelling}] printed as {source}");
            }
        }

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
