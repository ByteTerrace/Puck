using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Formatter tests for embedding spaces, Vector rows, and vector transforms: formatting is idempotent and preserves lowering.</summary>
public class EmbeddingDeclarationFormatterTests {
    private const string SampleVectorBase64 = "fwAAAAAAAAA";

    private const string EmbeddingSource = """
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
                table memories : Vector space("lore") capacity(10) evicts {
                    intro = "hello world"
                }
                slot query : Vector space("lore")
                table loreLog : Text embeds(companionVectors, space: lore) {
                    entry1 = "hello world"
                }
            }
        }

        rule "process" {
            when similarity(query, memories[intro]) >= 0.5
            transform query = mix(into: "query", terms: [
                { from: "query", weight: 2 }
                { from: embed("hello world"), weight: -1 }
            ])
            transform memories = remember(from: memories, query: "query", threshold: 0.8)
        }

        """;

    private static (JsonObject Json, DiagnosticBag Diagnostics) Lower(string source, EmbeddingLock lockFile) {
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source: source,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.False(
            condition: parseResult.Diagnostics.HasErrors,
            userMessage: parseResult.Diagnostics.FormatReport(source)
        );

        var diagnostics = new DiagnosticBag();
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            document: parseResult.Value!,
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken,
            embeddings: lockFile
        );

        return (loweringResult.Value!, diagnostics);
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
    public void FormattingEmbeddingSourceTwiceIsIdempotent() {
        var pass1 = PuckFormatter.Format(EmbeddingSource);
        var pass2 = PuckFormatter.Format(pass1);

        Assert.Equal(
            actual: pass2,
            expected: pass1
        );
    }

    [Fact]
    public void FormattingPreservesLoweredJson() {
        var lockFile = CreateSampleLock();
        var (beforeJson, beforeDiagnostics) = Lower(source: EmbeddingSource, lockFile: lockFile);
        var formatted = PuckFormatter.Format(EmbeddingSource);
        var (afterJson, afterDiagnostics) = Lower(source: formatted, lockFile: lockFile);

        Assert.False(condition: beforeDiagnostics.HasErrors, userMessage: beforeDiagnostics.FormatReport(""));
        Assert.False(condition: afterDiagnostics.HasErrors, userMessage: afterDiagnostics.FormatReport(""));

        var mismatch = JsonMismatch.Find(
            actual: afterJson,
            expected: beforeJson,
            path: "$"
        );

        Assert.Null(@object: mismatch);
    }
}
