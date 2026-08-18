using System.Text;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>Exercises <see cref="FileLengthAnalyzer"/> against small compilations and hand-written ledgers.</summary>
public sealed class FileLengthAnalyzerTests {
    private const string LedgerPath = @"X:\repo\FileLengths.json";
    // Above the brand attribute source the harness embeds in every compilation, so only the subject file can trip the rule.
    private const int Ceiling = 100;

    private static string Ledger(params (string Path, int Lines)[] recorded) {
        var builder = new StringBuilder(value: "{ \"format\": 1, \"ceiling\": 100, \"recorded\": {");

        for (var index = 0; index < recorded.Length; index++) {
            builder.Append(value: ((index == 0) ? " " : ", ")).Append(value: '"').Append(value: recorded[index].Path).Append(value: "\": ").Append(value: recorded[index].Lines);
        }

        return builder.Append(value: " } }").ToString();
    }

    private static string SourceOfLines(int lines) {
        var builder = new StringBuilder(value: "namespace Subject.Assembly;\npublic static class Long {\n");

        // The two lines above plus the closing brace below are counted; the rest are comment lines.
        for (var index = 0; index < (lines - 3); index++) {
            builder.Append(value: "// line\n");
        }

        return builder.Append(value: "}").ToString();
    }

    private static AnalysisResult Run(string fileName, int lines, string? ledgerJson) {
        var compilation = Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: new SourceFile(Name: fileName, Text: SourceOfLines(lines: lines)));
        var additionalFiles = ((ledgerJson is null) ? [] : new AdditionalText[] { new HarnessAdditionalText(path: LedgerPath, text: ledgerJson) });

        return Harness.Analyze(compilation: compilation, analyzer: new FileLengthAnalyzer(), additionalFiles: additionalFiles);
    }

    [Fact]
    public void AFileAtTheCeilingIsSilent() {
        var result = Run(fileName: "Long.cs", lines: Ceiling, ledgerJson: Ledger());

        Assert.Empty(collection: result.Analyzer);
    }

    [Fact]
    public void AnUnrecordedFileOverTheCeilingReportsLen001() {
        var result = Run(fileName: "Long.cs", lines: (Ceiling + 1), ledgerJson: Ledger());

        var diagnostic = result.Single(id: "LEN001");

        Assert.Contains(expectedSubstring: "'Long.cs' is 101 lines, over the 100-line ceiling", actualString: diagnostic.GetMessage(), comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordedFileWithinItsRecordedLengthIsSilent() {
        var result = Run(fileName: "Long.cs", lines: 130, ledgerJson: Ledger(("Long.cs", 130)));

        Assert.Empty(collection: result.Analyzer);
    }

    [Fact]
    public void ARecordedFileThatGrewReportsLen002() {
        var result = Run(fileName: "Long.cs", lines: 131, ledgerJson: Ledger(("Long.cs", 130)));

        var diagnostic = result.Single(id: "LEN002");

        Assert.Contains(expectedSubstring: "'Long.cs' is 131 lines, over the 130 lines", actualString: diagnostic.GetMessage(), comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordedFileThatShrankToTheCeilingReportsLen003() {
        var result = Run(fileName: "Long.cs", lines: Ceiling, ledgerJson: Ledger(("Long.cs", 130)));

        var diagnostic = result.Single(id: "LEN003");

        Assert.Contains(expectedSubstring: "still records it at 130", actualString: diagnostic.GetMessage(), comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void AGeneratedFileOverTheCeilingIsOutsideTheRule() {
        var result = Run(fileName: "Long.g.cs", lines: (Ceiling + 50), ledgerJson: Ledger());

        Assert.Empty(collection: result.Analyzer);
    }

    [Fact]
    public void AFileUnderTheLedgerDirectoryIsKeyedRelativeToIt() {
        var result = Run(fileName: @"X:\repo\src\Thing\Long.cs", lines: 130, ledgerJson: Ledger(("src/Thing/Long.cs", 130)));

        Assert.Empty(collection: result.Analyzer);
    }

    [Fact]
    public void AMissingLedgerReportsLen004() {
        var result = Run(fileName: "Long.cs", lines: 5, ledgerJson: null);

        var diagnostic = result.Single(id: "LEN004");

        Assert.Contains(expectedSubstring: "No FileLengths.json", actualString: diagnostic.GetMessage(), comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ALedgerEntryAtOrUnderTheCeilingIsOffSchema() {
        var result = Run(fileName: "Long.cs", lines: 5, ledgerJson: Ledger(("Long.cs", Ceiling)));

        var diagnostic = result.Single(id: "LEN004");

        Assert.Contains(expectedSubstring: "must be an integer above the ceiling", actualString: diagnostic.GetMessage(), comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedLedgerJsonReportsLen004() {
        var result = Run(fileName: "Long.cs", lines: 5, ledgerJson: "{ \"format\": 1, ");

        var diagnostic = result.Single(id: "LEN004");

        Assert.Contains(expectedSubstring: "malformed", actualString: diagnostic.GetMessage(), comparisonType: StringComparison.Ordinal);
    }
}
