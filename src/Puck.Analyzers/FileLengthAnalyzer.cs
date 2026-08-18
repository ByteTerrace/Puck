using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Puck.Analyzers;

/// <summary>
/// Refuses a source file longer than the repository's line ceiling. <c>FileLengths.json</c> (an <c>AdditionalFiles</c>
/// entry every project carries) declares the ceiling and a ledger of the files already over it, each with the length
/// it was recorded at. A file not in the ledger may not exceed the ceiling (<see cref="Len001OverCeiling"/>); a
/// ledger file may not grow past its recorded length (<see cref="Len002OverRecordedLength"/>); a ledger entry whose
/// file has dropped to the ceiling or below is stale and must be removed (<see cref="Len003StaleEntry"/>) — so the
/// ledger only ever shrinks. Generated trees are outside the rule. The length is
/// <see cref="Microsoft.CodeAnalysis.Text.SourceText.Lines"/>' count, the same figure <c>puck lengths</c> writes.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FileLengthAnalyzer : DiagnosticAnalyzer {
    /// <summary>The ledger file name looked up among <c>AdditionalFiles</c>.</summary>
    public const string LedgerFileName = "FileLengths.json";

    private const string Category = "Puck.FileLength";

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
        messageFormat: "'{0}' is {1} lines, at or under the {2}-line ceiling, but FileLengths.json still records it at {3}; remove the entry (or lower it with `puck lengths --write`)",
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
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        item1: Len001OverCeiling,
        item2: Len002OverRecordedLength,
        item3: Len003StaleEntry,
        item4: Len004LedgerUnusable
    );

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(action: AnalyzeCompilationStart);
    }

    private static void AnalyzeCompilationStart(CompilationStartAnalysisContext context) {
        var candidates = context.Options.AdditionalFiles
            .Where(predicate: file => string.Equals(
                a: Path.GetFileName(path: file.Path),
                b: LedgerFileName,
                comparisonType: StringComparison.OrdinalIgnoreCase
            ))
            .ToArray();

        if (candidates.Length != 1) {
            var message = ((candidates.Length == 0)
                ? $"No {LedgerFileName} was supplied to this compilation as an AdditionalFile, so no file length can be checked; restore the ledger to the build."
                : $"More than one {LedgerFileName} was supplied to this compilation ({string.Join(separator: ", ", values: candidates.Select(selector: file => file.Path).OrderBy(keySelector: path => path, comparer: StringComparer.Ordinal))}); exactly one ledger is expected.");

            context.RegisterCompilationEndAction(action: end => end.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Len004LedgerUnusable, location: Location.None, message)));

            return;
        }

        var ledgerFile = candidates[0];
        var text = ledgerFile.GetText(cancellationToken: context.CancellationToken)?.ToString();

        if (!FileLengthLedger.TryParse(error: out var error, json: text, ledger: out var ledger)) {
            var location = Location.Create(filePath: ledgerFile.Path, textSpan: default, lineSpan: default);

            context.RegisterCompilationEndAction(action: end => end.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Len004LedgerUnusable, location: location, $"{LedgerFileName} at '{ledgerFile.Path}' is unusable: {error}")));

            return;
        }

        var ledgerDirectory = (Path.GetDirectoryName(path: ledgerFile.Path) ?? "");
        var parsed = ledger!;

        context.RegisterSyntaxTreeAction(action: treeContext => AnalyzeTree(context: treeContext, ledger: parsed, ledgerDirectory: ledgerDirectory));
    }
    private static void AnalyzeTree(SyntaxTreeAnalysisContext context, FileLengthLedger ledger, string ledgerDirectory) {
        var tree = context.Tree;

        if (string.IsNullOrEmpty(value: tree.FilePath)) {
            return;
        }

        var lines = tree.GetText(cancellationToken: context.CancellationToken).Lines.Count;
        var key = FileLengthLedger.KeyFor(filePath: tree.FilePath, ledgerDirectory: ledgerDirectory);
        var recorded = ledger.TryGetRecordedLength(key: key);
        var location = Location.Create(syntaxTree: tree, textSpan: default);

        if (recorded is null) {
            if (lines > ledger.Ceiling) {
                context.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Len001OverCeiling, location: location, key, Format(value: lines), Format(value: ledger.Ceiling)));
            }

            return;
        }

        if (lines <= ledger.Ceiling) {
            context.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Len003StaleEntry, location: location, key, Format(value: lines), Format(value: ledger.Ceiling), Format(value: recorded.Value)));
        } else if (lines > recorded.Value) {
            context.ReportDiagnostic(diagnostic: Diagnostic.Create(descriptor: Len002OverRecordedLength, location: location, key, Format(value: lines), Format(value: recorded.Value)));
        }
    }
    private static string Format(int value) =>
        value.ToString(provider: CultureInfo.InvariantCulture);
}
