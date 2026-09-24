using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Puck.Analyzers;

/// <summary>
/// The build-time gate over one <see cref="RatchetLedger"/>. The ledger (an <c>AdditionalFiles</c> entry every
/// project carries) is read once per compilation; every non-generated syntax tree is measured and judged against it.
/// A file not in the ledger may not exceed the ceiling (<see cref="OverCeiling"/>); a recorded file may not rise past
/// its recorded count (<see cref="Grew"/>); a recorded file at or under the ceiling is stale and must be removed
/// (<see cref="Stale"/>); a missing, duplicated or unreadable ledger is <see cref="Unusable"/>. One compilation sees
/// only its own trees, so a ledger entry whose file was deleted never reaches this gate; the ledger's
/// <c>puck</c> verb, which walks the tracked tree, catches that.
/// </summary>
public abstract class RatchetAnalyzer : DiagnosticAnalyzer {
    /// <summary>Gets the ledger file name looked up among <c>AdditionalFiles</c>.</summary>
    public abstract string LedgerFileName { get; }
    /// <summary>Gets the rule an unrecorded file over the ceiling breaks. Its message takes the file key, the
    /// measured count, and the ceiling.</summary>
    public abstract DiagnosticDescriptor OverCeiling { get; }
    /// <summary>Gets the rule a recorded file that rose past its recorded count breaks. Its message takes the file
    /// key, the measured count, and the recorded count.</summary>
    public abstract DiagnosticDescriptor Grew { get; }
    /// <summary>Gets the rule a recorded file at or under the ceiling breaks. Its message takes the file key, the
    /// measured count, the ceiling, and the recorded count.</summary>
    public abstract DiagnosticDescriptor Stale { get; }
    /// <summary>Gets the rule an unusable ledger breaks. Its message is the whole fault. It must carry
    /// <see cref="WellKnownDiagnosticTags.CompilationEnd"/>.</summary>
    public abstract DiagnosticDescriptor Unusable { get; }
    /// <inheritdoc/>
    public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
        item1: OverCeiling,
        item2: Grew,
        item3: Stale,
        item4: Unusable
    );

    private static string Spell(int value) =>
        value.ToString(provider: CultureInfo.InvariantCulture);
    private void AnalyzeCompilationStart(CompilationStartAnalysisContext context) {
        var ledgerFileName = LedgerFileName;
        var candidates = context.Options.AdditionalFiles
            .Where(predicate: file => string.Equals(
            a: Path.GetFileName(path: file.Path),
            b: ledgerFileName,
            comparisonType: StringComparison.OrdinalIgnoreCase
        ))
            .ToArray();

        if (candidates.Length != 1) {
            var message = ((candidates.Length == 0)
                ? $"No {ledgerFileName} was supplied to this compilation as an AdditionalFile, so nothing it ratchets can be checked; restore the ledger to the build."
                : $"More than one {ledgerFileName} was supplied to this compilation ({string.Join(
                    separator: ", ",
                    values: candidates.Select(selector: file => file.Path).OrderBy(
                        keySelector: path => path,
                        comparer: StringComparer.Ordinal
                    )
                )}); exactly one ledger is expected."
            );

            context.RegisterCompilationEndAction(action: end => end.ReportDiagnostic(diagnostic: Diagnostic.Create(
                descriptor: Unusable,
                location: Location.None,
                message
            )));

            return;
        }

        var ledgerFile = candidates[0];
        var text = ledgerFile.GetText(cancellationToken: context.CancellationToken)?.ToString();

        if (!RatchetLedger.TryParse(
            error: out var error,
            json: text,
            ledger: out var ledger
        )) {
            var location = Location.Create(
                filePath: ledgerFile.Path,
                textSpan: default,
                lineSpan: default
            );

            context.RegisterCompilationEndAction(action: end => end.ReportDiagnostic(diagnostic: Diagnostic.Create(
                descriptor: Unusable,
                location: location,
                $"{ledgerFileName} at '{ledgerFile.Path}' is unusable: {error}"
            )));

            return;
        }

        var ledgerDirectory = (Path.GetDirectoryName(path: ledgerFile.Path) ?? "");
        var parsed = ledger!;

        context.RegisterSyntaxTreeAction(action: treeContext => AnalyzeTree(
            context: treeContext,
            ledger: parsed,
            ledgerDirectory: ledgerDirectory
        ));
    }
    private void AnalyzeTree(SyntaxTreeAnalysisContext context, RatchetLedger ledger, string ledgerDirectory) {
        var tree = context.Tree;

        if (string.IsNullOrEmpty(value: tree.FilePath)) {
            return;
        }

        var count = Measure(
            cancellationToken: context.CancellationToken,
            tree: tree
        );
        var key = RatchetLedger.KeyFor(
            filePath: tree.FilePath,
            ledgerDirectory: ledgerDirectory
        );
        var location = Location.Create(
            syntaxTree: tree,
            textSpan: default
        );
        var recorded = ledger.TryGetRecorded(key: key);
        var diagnostic = ledger.Judge(
            count: count,
            key: key
        ) switch {
            RatchetVerdict.OverCeiling => Diagnostic.Create(
                descriptor: OverCeiling,
                location: location,
                key,
                Spell(value: count),
                Spell(value: ledger.Ceiling)
            ),
            RatchetVerdict.Grew => Diagnostic.Create(
                descriptor: Grew,
                location: location,
                key,
                Spell(value: count),
                Spell(value: recorded.GetValueOrDefault())
            ),
            RatchetVerdict.Stale => Diagnostic.Create(
                descriptor: Stale,
                location: location,
                key,
                Spell(value: count),
                Spell(value: ledger.Ceiling),
                Spell(value: recorded.GetValueOrDefault())
            ),
            _ => null,
        };

        if (diagnostic is not null) {
            context.ReportDiagnostic(diagnostic: diagnostic);
        }
    }

    /// <summary>Measures one syntax tree: the per-file count the ledger ratchets.</summary>
    /// <param name="tree">The tree to measure; never generated code.</param>
    /// <param name="cancellationToken">The compilation's cancellation token.</param>
    /// <returns>The file's count.</returns>
    protected abstract int Measure(SyntaxTree tree, CancellationToken cancellationToken);

    /// <inheritdoc/>
    public sealed override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(analysisMode: GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(action: AnalyzeCompilationStart);
    }
}
