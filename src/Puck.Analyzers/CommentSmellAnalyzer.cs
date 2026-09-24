using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Puck.Analyzers;

/// <summary>
/// Refuses a source file whose comment-smell count rises. <c>CommentSmells.json</c> is the
/// <see cref="RatchetLedger"/> this <see cref="RatchetAnalyzer"/> reads: its ceiling (zero: a file not in the ledger
/// carries no smell), and the files that carry smells, each with the count it was recorded at (SMELL001–SMELL004).
/// The count is <see cref="CommentSmellClassifier.CountSmells"/>, the same figure <c>puck comment-smells</c> writes
/// and the same classification <c>puck scan --only comment-smells</c> reports comment by comment.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CommentSmellAnalyzer : RatchetAnalyzer {
    private const string Category = "Puck.CommentSmell";

    /// <summary>The ledger file name looked up among <c>AdditionalFiles</c>.</summary>
    public const string FileName = "CommentSmells.json";

    /// <summary>SMELL001: a file not in the ledger carries more comment smells than the ceiling.</summary>
    public static readonly DiagnosticDescriptor Smell001OverCeiling = new(
        id: "SMELL001",
        title: "Source file carries comment smells",
        messageFormat: "'{0}' carries {1} comment smell(s), over the ceiling of {2} for a file CommentSmells.json does not record; rewrite or delete the comment (`puck scan --only comment-smells` names each one and its bucket)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    /// <summary>SMELL002: a ledger file's comment-smell count rose past the count it was recorded at.</summary>
    public static readonly DiagnosticDescriptor Smell002OverRecordedCount = new(
        id: "SMELL002",
        title: "Source file's comment smells rose past its recorded count",
        messageFormat: "'{0}' carries {1} comment smell(s), over the {2} CommentSmells.json records for it; a recorded count may only fall — rewrite or delete the new comment (`puck scan --only comment-smells` names each one and its bucket)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    /// <summary>SMELL003: a ledger entry names a file that no longer needs one.</summary>
    public static readonly DiagnosticDescriptor Smell003StaleEntry = new(
        id: "SMELL003",
        title: "CommentSmells.json entry is stale",
        messageFormat: "'{0}' carries {1} comment smell(s), at or under the ceiling of {2}, but CommentSmells.json still records it at {3}; remove the entry (`puck comment-smells` rewrites the ledger)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
    /// <summary>SMELL004: the ledger is missing, unreadable, or off-schema, so nothing can be checked.</summary>
    public static readonly DiagnosticDescriptor Smell004LedgerUnusable = new(
        id: "SMELL004",
        title: "CommentSmells.json is unusable",
        messageFormat: "{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd
    );

    /// <inheritdoc/>
    public override string LedgerFileName => FileName;
    /// <inheritdoc/>
    public override DiagnosticDescriptor OverCeiling => Smell001OverCeiling;
    /// <inheritdoc/>
    public override DiagnosticDescriptor Grew => Smell002OverRecordedCount;
    /// <inheritdoc/>
    public override DiagnosticDescriptor Stale => Smell003StaleEntry;
    /// <inheritdoc/>
    public override DiagnosticDescriptor Unusable => Smell004LedgerUnusable;

    /// <inheritdoc/>
    protected override int Measure(SyntaxTree tree, CancellationToken cancellationToken) =>
        CommentSmellClassifier.CountSmells(root: tree.GetRoot(cancellationToken: cancellationToken));
}
