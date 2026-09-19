using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.State.Rebuild.Corpus;

/// <summary>The author-expression gate: every authored <c>.puck</c> source compiles clean, decompiles, and recompiles
/// to the same document.</summary>
/// <remarks>A rebuild of the state substrate may change the lowered document freely, but not what an author wrote:
/// the invariant this asserts is that compile and decompile stay mutual inverses over the whole shipped corpus. It
/// runs entirely on the transpiler, so it needs no booted world.</remarks>
public class AuthorExpressionTests {
    private static JsonObject Compile(string relativePath, string sourceText, string label) {
        var diagnostics = new DiagnosticBag();
        var absolutePath = CorpusSources.Resolve(relativePath: relativePath);
        var loweringResult = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: diagnostics,
            source: sourceText,
            sourcePath: absolutePath
        );

        Assert.True(
            condition: (loweringResult.Document is not null),
            userMessage: $"{label} did not parse:{Environment.NewLine}{diagnostics.FormatReport(sourceText)}"
        );

        Assert.True(
            condition: (diagnostics.Count == 0),
            userMessage: $"{label} reported diagnostics:{Environment.NewLine}{diagnostics.FormatReport(sourceText)}"
        );
        return loweringResult.RequireJson();
    }

    public static TheoryData<string> CorpusPaths() => CorpusSources.All();
    public static TheoryData<string> ExcludedPaths() => CorpusSources.Excluded();
    public static TheoryData<string> WorldPaths() => CorpusSources.Worlds();
    /// <summary>Holds each round-trip exclusion to the reason it was granted: the decompiled form is longer than
    /// the parser accepts. An exclusion that stops being necessary fails here instead of sitting unnoticed.</summary>
    /// <param name="relativePath">A repository-relative forward-slashed source path.</param>
    [MemberData(nameof(ExcludedPaths))]
    [Theory]
    public void ExcludedSourceDecompilesPastTheParserSourceLimit(string relativePath) {
        var absolutePath = CorpusSources.Resolve(relativePath: relativePath);
        var compiled = Compile(
            relativePath: relativePath,
            sourceText: File.ReadAllText(path: absolutePath),
            label: relativePath
        );
        var decompiled = WorldDecompiler.Decompile(root: compiled);

        Assert.True(
            condition: (decompiled.Length > DocumentEvaluationBudget.TextLimit),
            userMessage: $"{relativePath} decompiles to {decompiled.Length} characters, within the {DocumentEvaluationBudget.TextLimit}-character source limit: drop it from CorpusSources.RoundTripExclusions."
        );
    }
    [MemberData(nameof(CorpusPaths))]
    [Theory]
    public void SourceCompilesDecompilesAndRecompilesToTheSameDocument(string relativePath) {
        var absolutePath = CorpusSources.Resolve(relativePath: relativePath);

        Assert.True(
            condition: File.Exists(path: absolutePath),
            userMessage: $"Corpus source not found: {absolutePath}"
        );

        var compiled = Compile(
            relativePath: relativePath,
            sourceText: File.ReadAllText(path: absolutePath),
            label: relativePath
        );
        var decompiled = WorldDecompiler.Decompile(root: compiled);

        Assert.False(
            condition: string.IsNullOrWhiteSpace(value: decompiled),
            userMessage: $"{relativePath}: decompiled source was empty"
        );

        var recompiled = Compile(
            label: $"{relativePath} (decompiled)",
            relativePath: relativePath,
            sourceText: decompiled
        );
        var difference = CanonicalJsonComparison.FirstDifference(
            actual: recompiled,
            expected: compiled,
            label: $"{relativePath} (round trip)"
        );

        Assert.True(
            condition: (difference is null),
            userMessage: difference
        );
    }
    /// <summary>The document beside a world source is what that source compiles to, compared canonically rather than
    /// byte for byte.</summary>
    /// <param name="relativePath">A repository-relative forward-slashed world source path.</param>
    [MemberData(nameof(WorldPaths))]
    [Theory]
    public void WorldSourceCompilesToTheDocumentBesideIt(string relativePath) {
        var documentPath = Path.ChangeExtension(
            extension: ".world.json",
            path: CorpusSources.Resolve(relativePath: relativePath)
        );

        Assert.True(
            condition: File.Exists(path: documentPath),
            userMessage: $"{relativePath} has no document at {documentPath}"
        );

        var compiled = Compile(
            relativePath: relativePath,
            sourceText: File.ReadAllText(path: CorpusSources.Resolve(relativePath: relativePath)),
            label: relativePath
        );
        var difference = CanonicalJsonComparison.FirstDifferenceAgainstFile(
            actual: compiled,
            expectedPath: documentPath,
            label: relativePath
        );

        Assert.True(
            condition: (difference is null),
            userMessage: difference
        );
    }
}
