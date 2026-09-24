using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Lowering coverage for embedding declarations: spaces, Vector rows, transforms, literals, refusals PUCK077–PUCK088, and PUCK_LINT_010.</summary>
public sealed class EmbeddingDeclarationEmitterTests {
    [Fact]
    public void SpacesBlockLowersCorrectlyToJson() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            state {
                spaces {
                    space lore {
                        model: "text-embedding-3-small"
                        revision: "1"
                        dimensions: 256
                    }
                }
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        Assert.NotNull(@object: json);
        var state = Assert.IsType<JsonObject>(@object: json["state"]);
        var spaces = Assert.IsType<JsonArray>(@object: state["spaces"]);

        Assert.Single(collection: spaces);
        var space = Assert.IsType<JsonObject>(@object: spaces[0]);

        Assert.Equal("lore", space["name"]?.ToString());
        Assert.Equal("text-embedding-3-small", space["model"]?.ToString());
        Assert.Equal("1", space["revision"]?.ToString());
        Assert.Equal(256L, space["dimensions"]?.GetValue<long>());
    }
    [Fact]
    public void VectorTableAndSlotLowerCorrectlyWithLock() {
        var lockFile = WorldSources.LoreLock("text-embedding-3-small", "hello world", "calm");

        var (json, diagnostics) = WorldSources.Lower(body: """
            state {
                spaces {
                    space lore {
                        model: "text-embedding-3-small"
                        revision: "1"
                        dimensions: 8
                    }
                }
                world {
                    table memories space("lore") capacity(10) evicts {
                        intro = "hello world"
                    }
                    slot current space("lore")
                }
            }
            """, embeddings: lockFile);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        Assert.NotNull(@object: json);
        var world = Assert.IsType<JsonArray>(@object: json["state"]?["world"]);

        Assert.Equal(2, world.Count);

        var tableObj = Assert.IsType<JsonObject>(@object: world[0]);

        Assert.Equal("memories", tableObj["name"]?.ToString());
        Assert.Equal("Vector", tableObj["kind"]?.ToString());
        Assert.Equal("lore", tableObj["space"]?.ToString());
        Assert.Equal(10, tableObj["capacity"]?.GetValue<int>());
        Assert.True(condition: tableObj["evicts"]?.GetValue<bool>());

        var cells = Assert.IsType<JsonArray>(@object: tableObj["cells"]);

        Assert.Single(collection: cells);
        var cellObj = Assert.IsType<JsonObject>(@object: cells[0]);

        Assert.Equal("intro", cellObj["key"]?.ToString());
        Assert.Equal(WorldSources.SampleVector, cellObj["value"]?.ToString());

        var slotObj = Assert.IsType<JsonObject>(@object: world[1]);

        Assert.Equal("current", slotObj["name"]?.ToString());
        Assert.Equal("Vector", slotObj["kind"]?.ToString());
        Assert.Equal("lore", slotObj["space"]?.ToString());
    }
    [Fact]
    public void TextTableWithEmbedsModifierEmitsCompanionVectorTable() {
        var lockFile = WorldSources.LoreLock("text-embedding-3-small", "hello world", "calm");

        var (json, diagnostics) = WorldSources.Lower(body: """
            state {
                spaces {
                    space lore {
                        model: "text-embedding-3-small"
                        revision: "1"
                        dimensions: 8
                    }
                }
                world {
                    table loreLog embeds(companionVectors, space: lore) {
                        entry1 = "hello world"
                    }
                }
            }
            """, embeddings: lockFile);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        Assert.NotNull(@object: json);
        var world = Assert.IsType<JsonArray>(@object: json["state"]?["world"]);

        Assert.Equal(2, world.Count);

        var textTable = Assert.IsType<JsonObject>(@object: world[0]);

        Assert.Equal("loreLog", textTable["name"]?.ToString());
        Assert.Equal("Text", textTable["kind"]?.ToString());

        var vectorTable = Assert.IsType<JsonObject>(@object: world[1]);

        Assert.Equal("companionVectors", vectorTable["name"]?.ToString());
        Assert.Equal("Vector", vectorTable["kind"]?.ToString());
        Assert.Equal("lore", vectorTable["space"]?.ToString());

        var cells = Assert.IsType<JsonArray>(@object: vectorTable["cells"]);

        Assert.Single(collection: cells);
        var cell = Assert.IsType<JsonObject>(@object: cells[0]);

        Assert.Equal("entry1", cell["key"]?.ToString());
        Assert.Equal(WorldSources.SampleVector, cell["value"]?.ToString());
    }
    [Fact]
    public void NativeEmbedLiteralLowersIdenticalToVectorLiteral() {
        var lockFile = WorldSources.LoreLock("text-embedding-3-small", "hello world", "calm");
        var bodyEmbed = """
            state {
                spaces {
                    space lore {
                        model: "text-embedding-3-small"
                        revision: "1"
                        dimensions: 8
                    }
                }
                world {
                    slot current space("lore")
                }
            }
            rule "assign" {
                current = embed("hello world")
            }
            """;

        var bodyVector = $$"""
            state {
                spaces {
                    space lore {
                        model: "text-embedding-3-small"
                        revision: "1"
                        dimensions: 8
                    }
                }
                world {
                    slot current space("lore")
                }
            }
            rule "assign" {
                current = vector("{{WorldSources.SampleVector}}")
            }
            """;

        var (jsonEmbed, diagEmbed) = WorldSources.Lower(body: bodyEmbed, embeddings: lockFile);
        var (jsonVector, diagVector) = WorldSources.Lower(body: bodyVector, embeddings: lockFile);

        Assert.False(condition: diagEmbed.HasErrors, userMessage: diagEmbed.FormatReport(""));
        Assert.False(condition: diagVector.HasErrors, userMessage: diagVector.FormatReport(""));
        Assert.Equal(jsonVector?.ToJsonString(), jsonEmbed?.ToJsonString());
    }

    // A state block declaring one embedding space, a `world` block holding `world`, and then `after`.
    private static string Lore(string world = "", string after = "", string name = "lore", string model = "m", int dimensions = 8) => $$"""
        state {
            spaces {
                space {{name}} {
                    model: "{{model}}"
                    revision: "1"
                    dimensions: {{dimensions}}
                }
            }
            world {
                {{world}}
            }
        }
        {{after}}
        """;

    private static readonly Dictionary<string, Refusal> Refusals = new(comparer: StringComparer.Ordinal) {
        ["PUCK077: a space's dimensions lie outside the admitted range"] = new(
            Body: Lore(dimensions: 4, name: "invalidSpace"),
            Code: PuckDiagnosticCodes.EmbeddingSpaceInvalid,
            Needle: "dimensions: 4"
        ) { Alone = true },
        ["PUCK078: a Vector row names an undeclared space"] = new(
            Body: Lore(world: "slot badSlot space(\"nonexistent\")"),
            Code: PuckDiagnosticCodes.EmbeddingSpaceUnknown,
            Needle: "slot badSlot"
        ),
        ["PUCK079: embedded text the lock does not carry"] = new(
            Body: Lore(world: "slot s space(\"lore\")", after: "rule \"r\" {\n    s = embed(\"unlocked text\")\n}"),
            Code: PuckDiagnosticCodes.EmbeddingLockMissing,
            Needle: "embed(\"unlocked text\")"
        ) { Embeddings = WorldSources.LoreLock(model: "m") },
        ["PUCK080: a lock recorded under another model"] = new(
            Body: Lore(model: "new-model", world: "slot s space(\"lore\")", after: "rule \"r\" {\n    s = embed(\"text\")\n}"),
            Code: PuckDiagnosticCodes.EmbeddingLockStale,
            Needle: "embed(\"text\")"
        ) { Embeddings = WorldSources.LoreLock("old-model", "text") },
        ["PUCK081: a vector literal that is not base64"] = new(
            Body: Lore(world: "slot s space(\"lore\")", after: "rule \"r\" {\n    s = vector(\"not-valid-base64!!!\")\n}"),
            Code: PuckDiagnosticCodes.VectorLiteralInvalid,
            Needle: "vector(\"not-valid-base64!!!\")"
        ),
        ["PUCK082: a vector literal assigned to a number"] = new(
            Body: Lore(world: "slot numberSlot = 0", after: "rule \"r\" {\n    numberSlot = vector(\"AAAA\")\n}"),
            Code: PuckDiagnosticCodes.VectorLiteralMisplaced,
            Needle: "vector(\"AAAA\")"
        ),
        // 100 * 1024 = 102,400 > the 65,536 ceiling.
        ["PUCK083: capacity times dimensions exceeds the ceiling"] = new(
            Body: Lore(dimensions: 1024, name: "largeSpace", world: "table hugeTable space(\"largeSpace\") capacity(100) { }"),
            Code: PuckDiagnosticCodes.VectorRowTooLarge,
            Needle: "table hugeTable"
        ),
        ["PUCK086: embeds on a table that is not text"] = new(
            Body: Lore(world: "table numbers embeds(comp, space: lore) {\n        val = 42\n    }"),
            Code: PuckDiagnosticCodes.EmbedsInvalid,
            Needle: "table numbers"
        ),
        ["PUCK087: a mix term weighted zero"] = new(
            Body: Lore(world: "slot s space(\"lore\")", after: "rule \"r\" {\n    transform mix(into: s, terms: [\n        { from: \"s\", weight: 0 }\n    ])\n}"),
            Code: PuckDiagnosticCodes.VectorMixInvalid,
            Needle: "weight: 0"
        ),
    };

    public static TheoryData<string> RefusalNames() => new(values: Refusals.Keys);
    [MemberData(nameof(RefusalNames))]
    [Theory]
    public void ARefusedEmbeddingDeclarationNamesItsCodeAndLine(string name) => WorldSources.AssertRefused(
        label: name,
        refusal: Refusals[name]
    );
    [Fact]
    public void PUCK_LINT_010_VectorMixStall_WarnsWhenWeightShareBelowOneSixtyFourth() {
        var source = """
            schema: "puck.world.definition.v1"

            state {
                spaces {
                    space lore {
                        model: "m"
                        revision: "1"
                        dimensions: 8
                    }
                }
                world {
                    slot s space("lore")
                }
            }
            rule "r" {
                transform mix(into: s, terms: [
                    { from: "s", weight: 100 }
                    { from: "s", weight: 1 }
                ])
            }
            """;

        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source);

        Assert.False(condition: parseResult.Diagnostics.HasErrors);

        var lintDiags = new DiagnosticBag();

        PuckLinter.Lint(parseResult.Value!, lintDiags);

        Assert.Contains(collection: lintDiags, filter: d => (d.Code == PuckDiagnosticCodes.VectorMixStall));
        var warning = lintDiags.First(predicate: d => (d.Code == PuckDiagnosticCodes.VectorMixStall));

        Assert.Contains("mean over a history table", warning.Message);
    }
}
