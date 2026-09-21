using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Lowering coverage for embedding declarations: spaces, Vector rows, transforms, literals, refusals PUCK077–PUCK088, and PUCK_LINT_010.</summary>
public sealed class EmbeddingDeclarationEmitterTests {
    private const string SampleVectorBase64 = "fwAAAAAAAAA"; // 8-byte unit vector (127, 0, 0, 0, 0, 0, 0, 0)

    private static (JsonObject? Json, DiagnosticBag Diagnostics) LowerWithLock(string body, EmbeddingLock? lockFile = null) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            embeddings: lockFile,
            source: source
        );

        return (compilation.Json, compilation.Diagnostics);
    }
    private static EmbeddingLock CreateSampleLock() {
        var lockFile = new EmbeddingLock();
        var space = new EmbeddingLockSpace(
            dimensions: 8,
            model: "text-embedding-3-small",
            revision: "1"
        );
        var hash = EmbeddingLock.ComputeTextHash(text: "hello world");

        space.Entries[hash] = new EmbeddingLockEntry(Text: "hello world", Vector: SampleVectorBase64);
        var hash2 = EmbeddingLock.ComputeTextHash(text: "calm");

        space.Entries[hash2] = new EmbeddingLockEntry(Text: "calm", Vector: SampleVectorBase64);
        lockFile.Spaces["lore"] = space;
        return lockFile;
    }

    [Fact]
    public void SpacesBlockLowersCorrectlyToJson() {
        var (json, diagnostics) = LowerWithLock(body: """
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
        var lockFile = CreateSampleLock();

        var (json, diagnostics) = LowerWithLock(body: """
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
            """, lockFile: lockFile);

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
        Assert.Equal(SampleVectorBase64, cellObj["value"]?.ToString());

        var slotObj = Assert.IsType<JsonObject>(@object: world[1]);

        Assert.Equal("current", slotObj["name"]?.ToString());
        Assert.Equal("Vector", slotObj["kind"]?.ToString());
        Assert.Equal("lore", slotObj["space"]?.ToString());
    }
    [Fact]
    public void TextTableWithEmbedsModifierEmitsCompanionVectorTable() {
        var lockFile = CreateSampleLock();

        var (json, diagnostics) = LowerWithLock(body: """
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
            """, lockFile: lockFile);

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
        Assert.Equal(SampleVectorBase64, cell["value"]?.ToString());
    }
    [Fact]
    public void NativeEmbedLiteralLowersIdenticalToVectorLiteral() {
        var lockFile = CreateSampleLock();
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
                current = vector("{{SampleVectorBase64}}")
            }
            """;

        var (jsonEmbed, diagEmbed) = LowerWithLock(body: bodyEmbed, lockFile: lockFile);
        var (jsonVector, diagVector) = LowerWithLock(body: bodyVector, lockFile: lockFile);

        Assert.False(condition: diagEmbed.HasErrors, userMessage: diagEmbed.FormatReport(""));
        Assert.False(condition: diagVector.HasErrors, userMessage: diagVector.FormatReport(""));
        Assert.Equal(jsonVector?.ToJsonString(), jsonEmbed?.ToJsonString());
    }
    [Fact]
    public void PUCK077_EmbeddingSpaceInvalid_WhenDimensionsOutOfRange() {
        var (_, diagnostics) = LowerWithLock(body: """
            state {
                spaces {
                    space invalidSpace {
                        model: "model"
                        revision: "1"
                        dimensions: 4
                    }
                }
            }
            """);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(collection: diagnostics, filter: d => (d.Code == PuckDiagnosticCodes.EmbeddingSpaceInvalid));
    }
    [Fact]
    public void PUCK078_EmbeddingSpaceUnknown_WhenVectorRowNamesUndeclaredSpace() {
        var (_, diagnostics) = LowerWithLock(body: """
            state {
                spaces {
                    space lore {
                        model: "model"
                        revision: "1"
                        dimensions: 8
                    }
                }
                world {
                    slot badSlot space("nonexistent")
                }
            }
            """);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(collection: diagnostics, filter: d => (d.Code == PuckDiagnosticCodes.EmbeddingSpaceUnknown));
    }
    [Fact]
    public void PUCK079_EmbeddingLockMissing_WhenTextNotLocked() {
        var lockFile = new EmbeddingLock();

        lockFile.Spaces["lore"] = new EmbeddingLockSpace(dimensions: 8, model: "m", revision: "1");

        var (_, diagnostics) = LowerWithLock(body: """
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
                s = embed("unlocked text")
            }
            """, lockFile: lockFile);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(collection: diagnostics, filter: d => (d.Code == PuckDiagnosticCodes.EmbeddingLockMissing));
    }
    [Fact]
    public void PUCK080_EmbeddingLockStale_WhenLockDiffersFromDoc() {
        var lockFile = new EmbeddingLock();

        lockFile.Spaces["lore"] = new EmbeddingLockSpace(dimensions: 8, model: "old-model", revision: "1");
        lockFile.Spaces["lore"].Entries[EmbeddingLock.ComputeTextHash(text: "text")] = new EmbeddingLockEntry(Text: "text", Vector: SampleVectorBase64);

        var (_, diagnostics) = LowerWithLock(body: """
            state {
                spaces {
                    space lore {
                        model: "new-model"
                        revision: "1"
                        dimensions: 8
                    }
                }
                world {
                    slot s space("lore")
                }
            }
            rule "r" {
                s = embed("text")
            }
            """, lockFile: lockFile);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(collection: diagnostics, filter: d => (d.Code == PuckDiagnosticCodes.EmbeddingLockStale));
    }
    [Fact]
    public void PUCK081_VectorLiteralInvalid_WhenMalformed() {
        var (_, diagnostics) = LowerWithLock(body: """
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
                s = vector("not-valid-base64!!!")
            }
            """);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(collection: diagnostics, filter: d => (d.Code == PuckDiagnosticCodes.VectorLiteralInvalid));
    }
    [Fact]
    public void PUCK082_VectorLiteralMisplaced_WhenAssignedToNonVector() {
        var (_, diagnostics) = LowerWithLock(body: """
            state {
                spaces {
                    space lore {
                        model: "m"
                        revision: "1"
                        dimensions: 8
                    }
                }
                world {
                    slot numberSlot = 0
                }
            }
            rule "r" {
                numberSlot = vector("AAAA")
            }
            """);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(collection: diagnostics, filter: d => (d.Code == PuckDiagnosticCodes.VectorLiteralMisplaced));
    }
    [Fact]
    public void PUCK083_VectorRowTooLarge_WhenCapacityMultipliedByDimensionsExceedsCeiling() {
        var (_, diagnostics) = LowerWithLock(body: """
            state {
                spaces {
                    space largeSpace {
                        model: "m"
                        revision: "1"
                        dimensions: 1024
                    }
                }
                world {
                    table hugeTable space("largeSpace") capacity(100) { }
                }
            }
            """);

        // 100 * 1024 = 102,400 > 65,536 ceiling
        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(collection: diagnostics, filter: d => (d.Code == PuckDiagnosticCodes.VectorRowTooLarge));
    }
    [Fact]
    public void PUCK086_EmbedsInvalid_WhenOnNonTextTable() {
        var (_, diagnostics) = LowerWithLock(body: """
            state {
                spaces {
                    space lore {
                        model: "m"
                        revision: "1"
                        dimensions: 8
                    }
                }
                world {
                    table numbers embeds(comp, space: lore) {
                        val = 42
                    }
                }
            }
            """);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(collection: diagnostics, filter: d => (d.Code == PuckDiagnosticCodes.EmbedsInvalid));
    }
    [Fact]
    public void PUCK087_VectorMixInvalid_WhenTermWeightIsZero() {
        var (_, diagnostics) = LowerWithLock(body: """
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
                    { from: "s", weight: 0 }
                ])
            }
            """);

        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(collection: diagnostics, filter: d => (d.Code == PuckDiagnosticCodes.VectorMixInvalid));
    }
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
