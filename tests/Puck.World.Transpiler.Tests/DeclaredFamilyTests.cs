using System.Text;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Decompiler;
using Xunit;
using ArenaEffectHost = Puck.State.Rules.ArenaEffectHost;
using RuleEvaluator = Puck.State.Rules.RuleEvaluator;
using RuleLatch = Puck.State.Rules.RuleLatch;

namespace Puck.World.Transpiler.Tests;

/// <summary>A family that declares its members is addressed by its family index from a rule: a constant index is a
/// member row's name at lowering, a live one reaches the rule compiler as the spelling it resolves through
/// <c>state.families</c>, and an index the family does not carry names no row at all.</summary>
public class DeclaredFamilyTests {
    private const string GappedSource = """
        schema: "puck.world.definition.v1"

        state {
            world {
                slot Pile[0, 2..3] = 0
                slot idx = 2
            }
        }

        rule "constant" {
            Pile[2] = 7
        }

        rule "live" {
            local k = idx
            Pile[$local:k] += 1
        }
        """;

    private static WorldDefinition Deserialize(JsonObject document) => WorldDefinitionSerialization.Deserialize(utf8Json: Encoding.UTF8.GetBytes(s: document.ToJsonString()));
    // The validator still compiles rules through the OLD compiler, which resolves no `state.families` and refuses a
    // live family index by name; it dies with the old substrate, so a declared family is proved against the new
    // compiler alone until the server switch lands.
    private static WorldDefinition Unvalidated(JsonObject document) => System.Text.Json.JsonSerializer.Deserialize(
        json: document.ToJsonString(),
        jsonTypeInfo: WorldJsonContext.Default.WorldDefinition
    )!;

    [Fact]
    public void AConstantIndexOnADeclaredFamilyNamesItsMemberRowAndALiveIndexReachesTheCompiler() {
        var (document, diagnostics) = WorldSources.LowerSource(source: GappedSource);

        Assert.DoesNotContain(collection: diagnostics, filter: d => (d.Severity == DiagnosticSeverity.Error));

        var rules = Assert.IsType<JsonArray>(@object: document["rules"]);
        var constant = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: rules[0]?["effects"])[0]);

        Assert.Equal("Pile2", constant["state"]?.ToString());
        Assert.Null(@object: constant["key"]);

        var live = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: rules[1]?["effects"])[0]);

        Assert.Equal("Pile[$local:k]", live["state"]?.ToString());
        Assert.Null(@object: live["key"]);

        // Both spellings reach the arena: the constant one as a member row, the live one through state.families.
        var definition = Unvalidated(document: document);

        var (compiled, _) = WorldFactsCompiler.CompileDocument(definition: definition);
        var arena = new StateArena(
            catalog: definition.StateCatalog,
            options: null,
            section: definition.StateRaw,
            time: ArenaTime.Origin
        );
        var host = new ArenaEffectHost(arena: arena);

        host.Advance(
            engineTick: 1UL,
            tick: 1UL
        );

        _ = new RuleEvaluator(host: host).Evaluate(
            latch: new RuleLatch(),
            rules: compiled,
            stepTicks: 1UL
        );

        // `Pile[2] = 7` then `Pile[idx] += 1` with idx == 2 both address Pile2.
        Assert.Equal(
            actual: Cell(
                host: host,
                row: "Pile2"
            ),
            expected: 8L
        );
        Assert.Equal(
            actual: Cell(
                host: host,
                row: "Pile0"
            ),
            expected: 0L
        );
    }

    // Each refused declaration draws exactly one error.
    private static readonly Dictionary<string, Refusal> Refusals = new(comparer: StringComparer.Ordinal) {
        ["PUCK090: an index at a family gap names no member"] = new(
            Body: "state {\n    world {\n        slot Pile[0, 2..3] = 0\n    }\n}\n\nrule \"gap\" {\n    Pile[1] = 1\n}\n",
            Code: PuckDiagnosticCodes.FamilyIndexOutOfBounds,
            Needle: "Pile[1] = 1"
        ) { Alone = true },
        ["PUCK090: a removal at a family gap names no member"] = new(
            Body: "state {\n    world {\n        slot Pile[0, 2..3] = 0\n    }\n}\n\nrule \"gap\" {\n    remove Pile[1]\n}\n",
            Code: PuckDiagnosticCodes.FamilyIndexOutOfBounds,
            Needle: "remove Pile[1]"
        ) { Alone = true },
        ["PUCK099: a workflow step named twice"] = new(
            Body: "state {\n    world {\n        slot board = 1\n    }\n}\n\nworkflow turn {\n    step act {\n        board = 0\n    }\n    step act {\n        board = 1\n    }\n}\n",
            Code: PuckDiagnosticCodes.RuleGroupShapeInadmissible,
            Needle: "step act"
        ) { Alone = true, Mentions = "twice" },
        ["PUCK099: maxPasses above the group ceiling"] = new(
            Body: "state {\n    world {\n        slot board = 1\n    }\n}\n\nstabilize settle maxPasses(999) {\n    rule \"collapse\" {\n        board = 0\n    }\n}\n",
            Code: PuckDiagnosticCodes.RuleGroupShapeInadmissible,
            Needle: "stabilize settle maxPasses(999)"
        ) { Alone = true, Mentions = "maxPasses" },
    };

    public static TheoryData<string> RefusalNames() => new(values: Refusals.Keys);
    [MemberData(nameof(RefusalNames))]
    [Theory]
    public void ARefusedFamilyOrGroupNamesItsCodeAndLine(string name) => WorldSources.AssertRefused(
        label: name,
        refusal: Refusals[name]
    );
    [Fact]
    public void AContiguousMemberListLowersIdenticallyToTheCountThatSpellsIt() {
        var (ranged, _) = WorldSources.LowerSource(source: """
            schema: "puck.world.definition.v1"

            state {
                world {
                    slot Pile[0..2] = 0
                }
            }

            rule "write" {
                Pile[1] = 1
            }
            """);
        var (counted, _) = WorldSources.LowerSource(source: """
            schema: "puck.world.definition.v1"

            state {
                world {
                    slot Pile[3] = 0
                }
            }

            rule "write" {
                Pile[1] = 1
            }
            """);

        Assert.Equal(
            actual: ranged.ToJsonString(),
            expected: counted.ToJsonString()
        );
    }
    [Fact]
    public void AnInterpolatedStabilizeMemberNamesItsStepByTheNameItResolvesTo() {
        var (document, diagnostics) = WorldSources.LowerSource(source: """
            schema: "puck.world.definition.v1"

            let n = 3

            state {
                world {
                    slot board = 1
                }
            }

            stabilize settle {
                rule $"collapse-{n}" {
                    board = 0
                }
            }
            """);

        Assert.DoesNotContain(collection: diagnostics, filter: d => (d.Severity == DiagnosticSeverity.Error));

        var group = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: document["ruleGroups"])));
        var step = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: group["steps"])));

        Assert.Equal("settle$collapse-3", step["rule"]?.ToString());

        var definition = Deserialize(document: document);

        var (compiled, groups) = WorldFactsCompiler.CompileDocument(definition: definition);

        Assert.Equal(
            actual: compiled[0].Name,
            expected: "settle$collapse-3"
        );
        Assert.Equal(
            actual: Assert.Single(collection: groups).Members,
            expected: [0]
        );
    }
    [Fact]
    public void AGroupNameNoHeaderCanCarryBareIsQuotedAndRoundTrips() {
        var (document, diagnostics) = WorldSources.LowerSource(source: """
            schema: "puck.world.definition.v1"

            state {
                world {
                    slot board = 1
                }
            }

            stabilize "settle it" {
                rule "collapse" {
                    board = 0
                }
            }

            set "a set": zone(board, 1..1)
            """);

        Assert.DoesNotContain(collection: diagnostics, filter: d => (d.Severity == DiagnosticSeverity.Error));

        var decompiled = WorldDecompiler.Decompile(root: document);

        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "stabilize \"settle it\" {"
        );
        Assert.Contains(
            actualString: decompiled,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "set \"a set\": zone(board, 1..1)"
        );

        var (again, _) = WorldSources.LowerSource(source: decompiled);

        Assert.Equal(
            actual: again.ToJsonString(),
            expected: document.ToJsonString()
        );
    }
    [Fact]
    public void ADeclaredSetNamingNoDeclaredRowIsRefusedByTheValidator() {
        var (document, diagnostics) = WorldSources.LowerSource(source: """
            schema: "puck.world.definition.v1"

            state {
                world {
                    slot board = 1
                }
            }

            set missing: zone(absent, 1..1)
            """);

        Assert.DoesNotContain(collection: diagnostics, filter: d => (d.Severity == DiagnosticSeverity.Error));

        var refusal = Assert.Throws<InvalidDataException>(testCode: () => Deserialize(document: document));

        Assert.Contains("absent", refusal.Message, StringComparison.Ordinal);
    }

    private static long Cell(ArenaEffectHost host, string row) {
        Assert.True(condition: host.Arena.Catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: row
        ));
        Assert.True(condition: host.Arena.TryReadRawAt(
            position: 0,
            raw: out var raw,
            rowOrdinal: handle.Ordinal
        ));

        return raw;
    }
}
