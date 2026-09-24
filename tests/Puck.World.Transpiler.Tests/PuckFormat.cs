using Puck.Transpiler.Formatting;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Formats world-vocabulary source the way <c>puck format</c> does, failing the test that asked when the
/// source does not parse.</summary>
public static class PuckFormat {
    /// <summary>Asserts that formatting <paramref name="source"/> is idempotent and never changes what it compiles
    /// to.</summary>
    /// <param name="source">A whole source that compiles clean.</param>
    /// <param name="embeddings">The embedding lock both compiles resolve authored text against; none when
    /// omitted.</param>
    public static void AssertStable(string source, EmbeddingLock? embeddings = null) {
        var formatted = Format(source: source);

        Assert.Equal(
            actual: Format(source: formatted),
            expected: formatted
        );

        var (before, beforeDiagnostics) = WorldSources.LowerSource(
            embeddings: embeddings,
            source: source
        );
        var (after, afterDiagnostics) = WorldSources.LowerSource(
            embeddings: embeddings,
            source: formatted
        );

        Assert.False(condition: beforeDiagnostics.HasErrors, userMessage: beforeDiagnostics.FormatReport(source));
        Assert.False(condition: afterDiagnostics.HasErrors, userMessage: afterDiagnostics.FormatReport(formatted));
        Assert.Null(@object: JsonMismatch.Find(
            actual: after,
            expected: before,
            path: "$"
        ));
    }
    /// <summary>Returns <paramref name="source"/> parsed and printed back.</summary>
    /// <param name="source">The source text to format.</param>
    /// <returns>The formatted source.</returns>
    public static string Format(string source) {
        var result = PuckPrinter.Format(
            source: source,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.True(
            condition: (result.Value is not null),
            userMessage: $"the source does not parse:{Environment.NewLine}{result.Diagnostics.FormatReport(source)}"
        );

        return result.Value!;
    }
}
