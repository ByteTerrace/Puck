using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Puck.Analyzers;

/// <summary>
/// Refuses a source file longer than the repository's line ceiling. <c>FileLengths.json</c> is the
/// <see cref="RatchetLedger"/> this <see cref="RatchetAnalyzer"/> reads: its ceiling, and the files already over it,
/// each with the length it was recorded at (LEN001–LEN004). The length is <see cref="CountLines"/>, the same figure
/// <c>puck lengths</c> writes.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FileLengthAnalyzer : RatchetAnalyzer {
    private const string Category = "Puck.FileLength";

    /// <summary>The ledger file name looked up among <c>AdditionalFiles</c>.</summary>
    public const string FileName = "FileLengths.json";

    /// <summary>LEN001: a file not in the ledger is longer than the ceiling.</summary>
    public static readonly DiagnosticDescriptor Len001OverCeiling = new(
        id: "LEN001",
        title: "Source file exceeds the line ceiling",
        messageFormat: "'{0}' is {1} lines, over the {2}-line ceiling; split it — a new file may not start life over the ceiling, and the ledger in FileLengths.json only records files that were already over it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    /// <summary>LEN002: a ledger file has grown past the length it was recorded at.</summary>
    public static readonly DiagnosticDescriptor Len002OverRecordedLength = new(
        id: "LEN002",
        title: "Source file grew past its recorded length",
        messageFormat: "'{0}' is {1} lines, over the {2} lines FileLengths.json records for it; a file over the ceiling may only shrink — move the growth into another file",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    /// <summary>LEN003: a ledger entry names a file that no longer needs one.</summary>
    public static readonly DiagnosticDescriptor Len003StaleEntry = new(
        id: "LEN003",
        title: "FileLengths.json entry is stale",
        messageFormat: "'{0}' is {1} lines, at or under the {2}-line ceiling, but FileLengths.json still records it at {3}; remove the entry (`puck lengths` rewrites the ledger)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    /// <summary>LEN004: the ledger is missing, unreadable, or off-schema, so nothing can be checked.</summary>
    public static readonly DiagnosticDescriptor Len004LedgerUnusable = new(
        id: "LEN004",
        title: "FileLengths.json is unusable",
        messageFormat: "{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd
    );

    /// <inheritdoc/>
    public override string LedgerFileName => FileName;
    /// <inheritdoc/>
    public override DiagnosticDescriptor OverCeiling => Len001OverCeiling;
    /// <inheritdoc/>
    public override DiagnosticDescriptor Grew => Len002OverRecordedLength;
    /// <inheritdoc/>
    public override DiagnosticDescriptor Stale => Len003StaleEntry;
    /// <inheritdoc/>
    public override DiagnosticDescriptor Unusable => Len004LedgerUnusable;

    /// <summary>Counts a source text's lines: its line breaks plus one.</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>The file's length in lines.</returns>
    public static int CountLines(SourceText text) =>
        text.Lines.Count;

    /// <inheritdoc/>
    protected override int Measure(SyntaxTree tree, CancellationToken cancellationToken) =>
        CountLines(text: tree.GetText(cancellationToken: cancellationToken));
}
