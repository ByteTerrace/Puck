using System.Text.Json.Nodes;
using Puck.State;
using Puck.World.Transpiler.Embeddings;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>LSP tests for state embeddings: completions, document symbols for spaces, keyword hover, and embedded text lock status.</summary>
public class EmbeddingDeclarationLspTests {
    private const string SampleVectorBase64 = "fwAAAAAAAAA"; // 8-byte unit vector

    [Fact]
    public async Task CompletionsIncludeAllEmbeddingKeywordsAndTransforms() {
        var labels = await LanguageServerClient.CompletionLabelsAsync(markedSource: """
            schema: "puck.world.definition.v1"

            |
            """);

        string[] expectedKeywords = [
            "spaces", "space", "Vector", "evicts", "embeds", "embed",
            "vector", "dot", "similarity", "identical", "mix", "mean",
            "nearest", "remember"
        ];

        foreach (var keyword in expectedKeywords) {
            Assert.Contains(expected: keyword, set: labels);
        }
    }
    [Fact]
    public async Task DocumentSymbolsIncludeSpacesBlockAndDefinedSpaces() {
        var source = """
            schema: "puck.world.definition.v1"

            state {
                spaces {
                    space lore {
                        model: "text-embedding-3-small"
                        revision: "1"
                        dimensions: 256
                    }
                }
            }
            """;

        var symbols = Assert.IsType<JsonArray>(@object: await LanguageServerClient.DocumentRequestAsync(
            method: "textDocument/documentSymbol",
            text: source
        ));

        Assert.NotEmpty(collection: symbols);

        var stateSymbol = symbols.FirstOrDefault(predicate: s => (s?["name"]?.ToString() == "state"));

        Assert.NotNull(@object: stateSymbol);

        var stateChildren = Assert.IsType<JsonArray>(@object: stateSymbol["children"]);
        var spacesSymbol = stateChildren.FirstOrDefault(predicate: s => (s?["name"]?.ToString() == "spaces"));

        Assert.NotNull(@object: spacesSymbol);

        var spacesChildren = Assert.IsType<JsonArray>(@object: spacesSymbol["children"]);

        Assert.Single(collection: spacesChildren);
        Assert.Equal("space lore", spacesChildren[0]?["name"]?.ToString());
    }
    [Fact]
    public async Task HoverOnKeywordReturnsDocumentationCard() {
        var card = await LanguageServerClient.HoverAsync("""
            schema: "puck.world.definition.v1"

            state {
                world {
                    slot q : |Vector
                }
            }
            """);

        Assert.NotNull(@object: card);
        Assert.Contains(actualString: card, expectedSubstring: "Vector");
        Assert.Contains(actualString: card, expectedSubstring: "embedding vector");
    }
    [Fact]
    public async Task HoverOnEmbeddedTextWithLockShowsLockStatusAndNearest() {
        var tempPuckFile = Path.Combine(path1: Path.GetTempPath(), path2: (("lsp_embed_test_" + Guid.NewGuid().ToString(format: "N")) + ".puck"));
        var tempLockFile = EmbeddingLock.DeriveLockPath(sourcePath: tempPuckFile);

        try {
            var lockFile = new EmbeddingLock();
            var space = new EmbeddingLockSpace(identity: new EmbeddingIdentity(Dimensions: 8, Model: "text-embedding-3-small", Revision: "1"));
            var h1 = EmbeddingText.Hash(text: "hello world").Hex;

            space.Entries[h1] = new EmbeddingLockEntry(Text: "hello world", Vector: SampleVectorBase64);
            var h2 = EmbeddingText.Hash(text: "peaceful morning").Hex;

            space.Entries[h2] = new EmbeddingLockEntry(Text: "peaceful morning", Vector: SampleVectorBase64);
            lockFile.Spaces["lore"] = space;
            lockFile.Write(lockPath: tempLockFile);

            var sourceWithCursor = """
                schema: "puck.world.definition.v1"

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
                rule "r" {
                    current = embed("hello| world")
                }
                """;

            var fileUri = new Uri(uriString: tempPuckFile).AbsoluteUri;
            var card = await LanguageServerClient.HoverAsync(markedSource: sourceWithCursor, uri: fileUri);

            Assert.NotNull(@object: card);
            Assert.Contains(actualString: card, expectedSubstring: "Embedded Text");
            Assert.Contains(actualString: card, expectedSubstring: "Lock status:** Locked");
            Assert.Contains(actualString: card, expectedSubstring: "Nearest locked texts:");
            Assert.Contains(actualString: card, expectedSubstring: "peaceful morning");
        } finally {
            if (File.Exists(path: tempPuckFile)) {
                File.Delete(path: tempPuckFile);
            }
            if (File.Exists(path: tempLockFile)) {
                File.Delete(path: tempLockFile);
            }
        }
    }
    [Fact]
    public async Task HoverOnUnlockedEmbeddedTextShowsNotLocked() {
        var sourceWithCursor = """
            schema: "puck.world.definition.v1"

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
            rule "r" {
                current = embed("unlocked| text")
            }
            """;

        var card = await LanguageServerClient.HoverAsync(markedSource: sourceWithCursor);

        Assert.NotNull(@object: card);
        Assert.Contains(actualString: card, expectedSubstring: "Embedded Text");
        Assert.Contains(actualString: card, expectedSubstring: "Not locked");
    }
}
