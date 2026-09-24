using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.State.Rebuild.Corpus;

/// <summary>The author-expression gate: every authored <c>.puck</c> source compiles clean, decompiles, and recompiles
/// to the same document.</summary>
/// <remarks>A rebuild of the state substrate may change the lowered document freely, but not what an author wrote:
/// the invariant this asserts is that compile and decompile stay mutual inverses over the whole shipped corpus. A
/// source emitting several worlds decompiles as one composition; a module fragment emits no world of its own and is
/// held through the sources that import it. It runs entirely on the transpiler, so it needs no booted world.</remarks>
public partial class AuthorExpressionTests {
    [GeneratedRegex(pattern: "^\\s*import\\s+\"(?<path>[^\"]+)\"", options: RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.Multiline)]
    private static partial Regex ImportLine();
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
    // Decompiles the source's documents: one document on its own, a composition's worlds together.
    private static string Decompile(List<KeyValuePair<string, JsonObject>> documents) => (((documents.Count == 1) && (documents[0].Key.Length == 0))
        ? WorldDecompiler.Decompile(root: documents[0].Value)
        : WorldDecompiler.DecompileComposition(worlds: documents)
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
        if (IsFragment(compilation: compiled)) {
            return null;
        }

        var documents = Documents(compilation: compiled);

        try {
            decompiled = Decompile(documents: documents);
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
    public static TheoryData<string> ExemptPaths() => CorpusSources.Exempt();
    public static TheoryData<string> PackagePaths() => new(values: CorpusSources.Packages());
    /// <summary>Holds each round-trip exemption to the reason it was granted. An exemption that stops being necessary
    /// fails here instead of sitting unnoticed, so the ledger only shrinks.</summary>
    /// <param name="relativePath">A repository-relative forward-slashed source path.</param>
    [MemberData(nameof(ExemptPaths))]
    [Theory]
    public void ExemptSourceStillFailsForItsReason(string relativePath) {
        var failure = RoundTrip(decompiled: out var decompiled, relativePath: relativePath);

        Assert.True(
            condition: (failure is not null),
            userMessage: $"{relativePath} now round-trips: drop it from CorpusSources.RoundTripExemptions."
        );

        switch (CorpusSources.RoundTripExemptions[relativePath]) {
            case CorpusExemption.DecompiledPastTheSourceLimit:
                Assert.True(
                    condition: ((decompiled?.Length ?? 0) > DocumentEvaluationBudget.TextLimit),
                    userMessage: $"{relativePath} fails the round trip for another reason than its exemption names, since it decompiles to {decompiled?.Length} characters, within the {DocumentEvaluationBudget.TextLimit}-character source limit:{Environment.NewLine}{failure}"
                );

                break;
            default:
                Assert.Fail(message: $"{relativePath}: no check holds exemption '{CorpusSources.RoundTripExemptions[relativePath]}' to its reason");

                break;
        }
    }
    /// <summary>A package source that emits no world of its own is a module fragment, and it is held through a source
    /// of the same package that imports it rather than on its own.</summary>
    /// <param name="relativePath">A repository-relative forward-slashed package source path.</param>
    [MemberData(nameof(PackagePaths))]
    [Theory]
    public void EveryPackageFragmentIsImportedByASourceOfItsPackage(string relativePath) {
        var absolutePath = RepositoryPaths.Resolve(relativePath: relativePath);
        var compiled = Compile(relativePath: relativePath, sourceText: File.ReadAllText(path: absolutePath));

        if (!IsFragment(compilation: compiled)) {
            return;
        }

        var importers = CorpusSources.Packages()
            .Where(predicate: other => (other != relativePath))
            .Where(predicate: other => ImportLine().Matches(input: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: other))).Any(predicate: match => string.Equals(
                a: Path.GetFullPath(path: Path.Combine(path1: Path.GetDirectoryName(path: RepositoryPaths.Resolve(relativePath: other))!, path2: match.Groups["path"].Value)),
                b: Path.GetFullPath(path: absolutePath),
                comparisonType: StringComparison.OrdinalIgnoreCase
            )));

        Assert.True(
            condition: importers.Any(),
            userMessage: $"{relativePath} emits no world and no package source imports it, so nothing holds it to the round trip"
        );
    }
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
