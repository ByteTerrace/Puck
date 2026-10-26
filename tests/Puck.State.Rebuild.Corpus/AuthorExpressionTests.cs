using System.Text.Json.Nodes;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.State.Rebuild.Corpus;

/// <summary>The author-expression gate: every authored <c>.puck</c> source compiles clean, decompiles, and recompiles
/// to the same document.</summary>
/// <remarks>A rebuild of the state substrate may change the lowered document freely, but not what an author wrote:
/// the invariant this asserts is that compile and decompile stay mutual inverses over the whole shipped corpus. A
/// composition, which emits several worlds, is written in source and prints back from no document; a module fragment
/// emits no world of its own and is held through the sources that import it. It runs entirely on the transpiler, so it
/// needs no booted world.</remarks>
public partial class AuthorExpressionTests {
    private static WorldCompilation Compile(string relativePath, string sourceText) => WorldCompiler.Compile(
        allowMultiple: true,
        cancellationToken: TestContext.Current.CancellationToken,
        source: sourceText,
        sourcePath: RepositoryPaths.Resolve(relativePath: relativePath)
    );
    private static string? Refusal(WorldCompilation compilation, string sourceText, string label) {
        if (compilation.Document is null) {
            return $"{label} did not parse:{Environment.NewLine}{compilation.Diagnostics.FormatReport(sourceText)}";
        }

        return ((compilation.Diagnostics.Count == 0)
            ? null
            : $"{label} reported diagnostics:{Environment.NewLine}{compilation.Diagnostics.FormatReport(sourceText)}"
        );
    }
    // The source's documents by name: the one document a single-world source lowers to, under the empty name, or each
    // world a composition emits.
    private static List<KeyValuePair<string, JsonObject>> Documents(WorldCompilation compilation) => ((compilation.Worlds.Count > 0)
        ? [.. compilation.Worlds.Select(selector: static output => KeyValuePair.Create(key: output.Name, value: output.Json))]
        : [KeyValuePair.Create(key: string.Empty, value: compilation.Json!)]
    );
    private static bool IsFragment(WorldCompilation compilation) => (
        (compilation.Worlds.Count == 0) &&
        (compilation.Json is not null) &&
        !WorldSemanticValidator.IsRootDocument(loweredJson: compilation.Json)
    );
    // Compiles, decompiles and recompiles the source, returning the first way the trip fails to close, or null.
    private static string? RoundTrip(string relativePath, out string? decompiled) {
        decompiled = null;

        var absolutePath = RepositoryPaths.Resolve(relativePath: relativePath);

        if (!File.Exists(path: absolutePath)) {
            return $"Corpus source not found: {absolutePath}";
        }

        var sourceText = File.ReadAllText(path: absolutePath);
        var compiled = Compile(relativePath: relativePath, sourceText: sourceText);

        if (Refusal(compilation: compiled, label: relativePath, sourceText: sourceText) is { } refused) {
            return refused;
        }
        if (IsFragment(compilation: compiled) || (compiled.Worlds.Count > 0)) {
            return null;
        }

        var documents = Documents(compilation: compiled);

        try {
            decompiled = WorldDecompiler.Decompile(root: documents[0].Value);
        } catch (WorldDecompileRefusedException refusal) {
            return $"{relativePath}: the decompiler refused it: {refusal.Message}";
        }

        if (string.IsNullOrWhiteSpace(value: decompiled)) {
            return $"{relativePath}: decompiled source was empty";
        }

        var recompiled = Compile(relativePath: relativePath, sourceText: decompiled);

        if (Refusal(compilation: recompiled, label: $"{relativePath} (decompiled)", sourceText: decompiled) is { } again) {
            return again;
        }

        var recompiledDocuments = Documents(compilation: recompiled).ToDictionary(comparer: StringComparer.Ordinal);

        if (recompiledDocuments.Count != documents.Count) {
            return $"{relativePath} (round trip): {documents.Count} document(s) decompiled to a source emitting {recompiledDocuments.Count}";
        }

        foreach (var (name, document) in documents) {
            var difference = (recompiledDocuments.TryGetValue(key: name, value: out var actual)
                ? CanonicalJsonComparison.FirstDifference(
                    actual: actual,
                    expected: document,
                    label: $"{relativePath}{((name.Length > 0) ? $" world '{name}'" : "")} (round trip)"
                )
                : $"{relativePath} (round trip): the decompiled source emits no world '{name}'");

            if (difference is not null) {
                return difference;
            }
        }

        return null;
    }

    public static TheoryData<string> CorpusPaths() => CorpusSources.All();
    [MemberData(nameof(CorpusPaths))]
    [Theory]
    public void SourceCompilesDecompilesAndRecompilesToTheSameDocument(string relativePath) {
        var failure = RoundTrip(decompiled: out _, relativePath: relativePath);

        Assert.True(
            condition: (failure is null),
            userMessage: failure
        );
    }
}
