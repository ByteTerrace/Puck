using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Formatter tests for embedding spaces, Vector rows, and vector transforms: formatting is idempotent and preserves lowering.</summary>
public class EmbeddingDeclarationFormatterTests {
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
                table memories space("lore") capacity(10) evicts {
                    intro = "hello world"
                }
                slot query space("lore")
                table loreLog embeds(companionVectors, space: lore) {
                    entry1 = "hello world"
                }
            }
        }

        rule "process" {
            when similarity(query, memories[intro]) >= 0.5
            transform mix(into: query, terms: [
                { from: "query", weight: 2 }
                { from: embed("hello world"), weight: -1 }
            ])
            transform remember(from: memories, query: query, threshold: 0.8)
        }

        """;

    [Fact]
    public void FormattingEmbeddingSourceIsStable() => PuckFormat.AssertStable(
        embeddings: WorldSources.LoreLock("text-embedding-3-small", "hello world"),
        source: EmbeddingSource
    );
}
