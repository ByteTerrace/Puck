using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Embeddings;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Decompiler coverage for embedding declarations: spaces, Vector rows, embeds pairs, transforms, and lock-aware literals.</summary>
public class EmbeddingDeclarationDecompilerTests {
    private const string SampleVectorBase64 = "fwAAAAAAAAA"; // 8-byte unit vector

    private static (JsonObject Json, DiagnosticBag Diagnostics) Recompile(string puckSource, EmbeddingLock? lockFile = null) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            embeddings: lockFile,
            source: puckSource
        );

        Assert.NotNull(@object: compilation.Json);

        return (compilation.Json, compilation.Diagnostics);
    }

    private static void AssertRoundTrips(JsonObject original, EmbeddingLock? lockFile = null) {
        var decompiled = WorldDecompiler.Decompile(root: original, embeddings: lockFile);
        var (recompiled, diagnostics) = Recompile(puckSource: decompiled, lockFile: lockFile);

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: $"{diagnostics.FormatReport(decompiled)}\n---\n{decompiled}"
        );

        var mismatch = JsonMismatch.Find(
            actual: recompiled,
            expected: original,
            path: "$"
        );

        Assert.True(
            condition: (mismatch is null),
            userMessage: $"{mismatch}\n---\n{decompiled}"
        );
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
        lockFile.Spaces["lore"] = space;
        return lockFile;
    }

    [Fact]
    public void SpacesBlockDecompilesAndRecompilesToSameJson() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "spaces": [
                        {
                            "name": "lore",
                            "model": "text-embedding-3-small",
                            "revision": "1",
                            "dimensions": 256
                        }
                    ],
                    "world": [
                        {
                            "name": "s",
                            "kind": "Vector",
                            "space": "lore"
                        }
                    ]
                }
            }
            """));

        AssertRoundTrips(original: original);
    }

    [Fact]
    public void VectorRowWithoutLockDecompilesToVectorLiteral() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse($$"""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "spaces": [
                        {
                            "name": "lore",
                            "model": "text-embedding-3-small",
                            "revision": "1",
                            "dimensions": 8
                        }
                    ],
                    "world": [
                        {
                            "name": "memories",
                            "kind": "Vector",
                            "space": "lore",
                            "cells": [
                                { "key": "k1", "value": "{{SampleVectorBase64}}" }
                            ]
                        }
                    ]
                }
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, embeddings: null);
        Assert.Contains("k1 = vector(\"fwAAAAAAAAA\")", decompiled);
        AssertRoundTrips(original: original, lockFile: null);
    }

    [Fact]
    public void VectorRowWithLockDecompilesToEmbedLiteral() {
        var lockFile = CreateSampleLock();
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse($$"""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "spaces": [
                        {
                            "name": "lore",
                            "model": "text-embedding-3-small",
                            "revision": "1",
                            "dimensions": 8
                        }
                    ],
                    "world": [
                        {
                            "name": "memories",
                            "kind": "Vector",
                            "space": "lore",
                            "cells": [
                                { "key": "k1", "value": "{{SampleVectorBase64}}" }
                            ]
                        }
                    ]
                }
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, embeddings: lockFile);
        Assert.Contains("embed(\"hello world\")", decompiled);
        AssertRoundTrips(original: original, lockFile: lockFile);
    }

    [Fact]
    public void EmbedsPairReconstructionDecompilesToTextTableWithEmbedsModifier() {
        var lockFile = CreateSampleLock();
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse($$"""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "spaces": [
                        {
                            "name": "lore",
                            "model": "text-embedding-3-small",
                            "revision": "1",
                            "dimensions": 8
                        }
                    ],
                    "world": [
                        {
                            "name": "loreLog",
                            "kind": "Text",
                            "cells": [
                                { "key": "entry1", "value": "hello world" }
                            ]
                        },
                        {
                            "name": "companionVectors",
                            "kind": "Vector",
                            "space": "lore",
                            "cells": [
                                { "key": "entry1", "value": "{{SampleVectorBase64}}" }
                            ]
                        }
                    ]
                }
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original, embeddings: lockFile);
        Assert.Contains("table loreLog : Text embeds(companionVectors)", decompiled);
        AssertRoundTrips(original: original, lockFile: lockFile);
    }
}
