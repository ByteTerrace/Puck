using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>Exercises <see cref="CommentSmellAnalyzer"/> and <see cref="CommentSmellClassifier"/> against small
/// compilations and hand-written ledgers.</summary>
public sealed class CommentSmellAnalyzerTests {
    private const string LedgerPath = @"X:\repo\CommentSmells.json";

    private static string Ledger(params (string Path, int Count)[] recorded) =>
        RatchetLedger.Render(
            ceiling: 0,
            recorded: recorded.Select(selector: static row => new KeyValuePair<string, int>(
                key: row.Path,
                value: row.Count
            ))
        );
    private static AnalysisResult Run(string fileName, string source, string ledgerJson) {
        var compilation = Harness.Compile(
            assemblyName: Harness.DefaultAssemblyName,
            sources: new SourceFile(
                Name: fileName,
                Text: source
            )
        );

        return Harness.Analyze(
            compilation: compilation,
            analyzer: new CommentSmellAnalyzer(),
            additionalFiles: new HarnessAdditionalText(
                path: LedgerPath,
                text: ledgerJson
            )
        );
    }
    private static string SourceWith(params string[] comments) =>
        $"namespace Subject.Assembly;\npublic static class Commented {{\n{string.Concat(values: comments.Select(selector: static comment => $"    {comment}\n"))}    public static int Value => 1;\n}}\n";

    [Fact]
    public void ACleanFileIsSilent() {
        var result = Run(
            fileName: "Commented.cs",
            ledgerJson: Ledger(),
            source: SourceWith("// The value is the multiplicative identity, so a product starts from it.")
        );

        Assert.Empty(collection: result.Analyzer);
    }
    [Fact]
    public void AnUnrecordedFileWithASmellReportsSmell001() {
        var result = Run(
            fileName: "Commented.cs",
            ledgerJson: Ledger(),
            source: SourceWith("// TODO: derive this from the document.")
        );

        var diagnostic = result.Single(id: "SMELL001");

        Assert.Contains(
            expectedSubstring: "'Commented.cs' carries 1 comment smell(s), over the ceiling of 0",
            actualString: diagnostic.GetMessage(),
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void ARecordedFileWhoseSmellsRoseReportsSmell002() {
        var result = Run(
            fileName: "Commented.cs",
            ledgerJson: Ledger(("Commented.cs", 1)),
            source: SourceWith("// TODO: derive this from the document.", "// -----------------")
        );

        var diagnostic = result.Single(id: "SMELL002");

        Assert.Contains(
            expectedSubstring: "carries 2 comment smell(s), over the 1 CommentSmells.json records",
            actualString: diagnostic.GetMessage(),
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void ARecordedFileAtItsCountIsSilent() {
        var result = Run(
            fileName: "Commented.cs",
            ledgerJson: Ledger(("Commented.cs", 2)),
            source: SourceWith("// TODO: derive this from the document.", "// -----------------")
        );

        Assert.Empty(collection: result.Analyzer);
    }
    [Fact]
    public void ARecordedFileWithNoSmellLeftReportsSmell003() {
        var result = Run(
            fileName: "Commented.cs",
            ledgerJson: Ledger(("Commented.cs", 1)),
            source: SourceWith()
        );

        var diagnostic = result.Single(id: "SMELL003");

        Assert.Contains(
            expectedSubstring: "still records it at 1",
            actualString: diagnostic.GetMessage(),
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void AMissingLedgerReportsSmell004() {
        var compilation = Harness.Compile(
            assemblyName: Harness.DefaultAssemblyName,
            sources: new SourceFile(
                Name: "Commented.cs",
                Text: SourceWith()
            )
        );
        var result = Harness.Analyze(
            compilation: compilation,
            analyzer: new CommentSmellAnalyzer()
        );

        var diagnostic = result.Single(id: "SMELL004");

        Assert.Contains(
            expectedSubstring: "No CommentSmells.json",
            actualString: diagnostic.GetMessage(),
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void DocumentationCommentsAreNeverCounted() {
        var result = Run(
            fileName: "Commented.cs",
            ledgerJson: Ledger(),
            source: "namespace Subject.Assembly;\n/// <summary>TODO-shaped words in a summary: TODO, FIXME, ----.</summary>\npublic static class Documented {\n}\n"
        );

        Assert.Empty(collection: result.Analyzer);
    }
    [InlineData("// Keep in sync with sdf-march.hlsl.", CommentSmellClassifier.SyncCoupling)]
    [InlineData("// TODO: derive this from the document.", CommentSmellClassifier.DebtMarker)]
    [InlineData("// ==========", CommentSmellClassifier.BannerDivider)]
    [InlineData("// return value + 1;", CommentSmellClassifier.CommentedOutCode)]
    [InlineData("// This used to be clamped at the edge.", CommentSmellClassifier.NarrativeHistory)]
    [InlineData("// See adversarial-review G1.", CommentSmellClassifier.DeadProvenance)]
    [InlineData("/* The sign flips because the frame is left-handed. */", CommentSmellClassifier.Unclassified)]
    [Theory]
    public void EachBucketClassifiesItsExemplar(string comment, string bucket) {
        Assert.Equal(
            actual: CommentSmellClassifier.Classify(body: CommentSmellClassifier.Body(text: comment)),
            expected: bucket
        );
    }
    [Fact]
    public void TheCountIsEveryInlineCommentOutsideTheUnclassifiedBucket() {
        var root = CSharpSyntaxTree.ParseText(
            cancellationToken: TestContext.Current.CancellationToken,
            text: SourceWith(
                "// TODO: derive this from the document.",
                "/* ---------------- */",
                "// The value is the multiplicative identity.",
                "// return value + 1;"
            )
        ).GetRoot(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            actual: CommentSmellClassifier.CountSmells(root: root),
            expected: 3
        );
    }
}
