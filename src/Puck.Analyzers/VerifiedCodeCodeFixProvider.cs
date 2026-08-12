using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace Puck.Analyzers;

/// <summary>
/// Offers to update a stale <c>VerifiedCode.json</c> entry's <c>sha256</c> to the hash
/// <see cref="VerifiedCodeAnalyzer.Ver001FingerprintMismatch"/> just recomputed, so re-verifying a branded change is
/// a one-click fix rather than a hand edit of the manifest file.
/// </summary>
/// <remarks>
/// The id and hash come from the diagnostic's properties, never from its message: a message is display text that an
/// unusual brand id can reshape, and a repair that misreads its own inputs writes the wrong bytes into the one file
/// that records what has been proven.
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(VerifiedCodeCodeFixProvider))]
[Shared]
public sealed class VerifiedCodeCodeFixProvider : CodeFixProvider {
    private const string EquivalenceKey = "UpdateVerifiedCodeBrand";
    private const string ManifestFileName = "VerifiedCode.json";

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(item: VerifiedCodeAnalyzer.Ver001FingerprintMismatch.Id);

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() =>
        LedgerFixAllProvider.Instance;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
        var manifestDocument = FindManifest(project: context.Document.Project);

        if (manifestDocument is null) {
            return;
        }

        var text = await manifestDocument.GetTextAsync(cancellationToken: context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var json = text.ToString();

        foreach (var diagnostic in context.Diagnostics) {
            if (!TryReadRepair(
                diagnostic: diagnostic,
                id: out var id,
                hash: out var hash
            )) {
                continue;
            }

            // An entry the ledger does not record cannot be repaired by rewriting a hash, and an action that
            // reports success having changed nothing is worse than no action at all.
            if (JsonText.FindRecordedHash(
                json: json,
                id: id
            ) is null) {
                continue;
            }

            context.RegisterCodeFix(
                action: CodeAction.Create(
                    title: $"Update verification brand for '{id}'",
                    createChangedSolution: cancellationToken => UpdateManifestAsync(
                        project: context.Document.Project,
                        repairs: [(Id: id, Hash: hash)],
                        cancellationToken: cancellationToken
                    ),
                    equivalenceKey: EquivalenceKey
                ),
                diagnostic: diagnostic
            );
        }
    }

    /// <summary>The one additional document that is the ledger, or <see langword="null"/> when there is not exactly one.</summary>
    /// <remarks>Two documents of that name make the ledger ambiguous, which the analyzer refuses; a repair must not pick one of them either.</remarks>
    private static TextDocument? FindManifest(Project project) {
        var candidates = project.AdditionalDocuments
            .Where(predicate: document => string.Equals(
            a: Path.GetFileName(path: (document.FilePath ?? document.Name)),
            b: ManifestFileName,
            comparisonType: StringComparison.OrdinalIgnoreCase
        ))
            .Take(count: 2)
            .ToArray();

        return ((candidates.Length == 1)
            ? candidates[0]
            : null);
    }

    /// <summary>Recovers a repair's inputs from the diagnostic's structured properties, refusing anything that is not a usable pair.</summary>
    private static bool TryReadRepair(Diagnostic diagnostic, out string id, out string hash) {
        id = string.Empty;
        hash = string.Empty;

        if (!string.Equals(
            a: diagnostic.Id,
            b: VerifiedCodeAnalyzer.Ver001FingerprintMismatch.Id,
            comparisonType: StringComparison.Ordinal
        )) {
            return false;
        }

        if (
            !diagnostic.Properties.TryGetValue(
            key: VerifiedCodeAnalyzer.BrandIdProperty,
            value: out var recordedId
        ) ||
            string.IsNullOrEmpty(value: recordedId)
        ) {
            return false;
        }

        if (
            !diagnostic.Properties.TryGetValue(
            key: VerifiedCodeAnalyzer.RecomputedHashProperty,
            value: out var recordedHash
        ) ||
            !IsRecordedHash(text: recordedHash)
        ) {
            return false;
        }

        id = recordedId!;
        hash = recordedHash!;

        return true;
    }

    /// <summary>Whether <paramref name="text"/> is the exact shape the analyzer computes, so it can be written into JSON without escaping.</summary>
    private static bool IsRecordedHash(string? text) {
        if (
            (text is null) ||
            (text.Length != 64)
        ) {
            return false;
        }

        foreach (var character in text) {
            var isHex = (((character >= '0') && (character <= '9')) || ((character >= 'a') && (character <= 'f')));

            if (!isHex) {
                return false;
            }
        }

        return true;
    }
    private static async Task<Solution> UpdateManifestAsync(Project project, IReadOnlyList<(string Id, string Hash)> repairs, CancellationToken cancellationToken) {
        var manifestDocument = FindManifest(project: project);

        if (manifestDocument is null) {
            return project.Solution;
        }

        var text = await manifestDocument.GetTextAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var json = text.ToString();
        var changed = false;

        foreach (var repair in repairs.OrderBy(
            keySelector: repair => repair.Id,
            comparer: StringComparer.Ordinal
        )) {
            cancellationToken.ThrowIfCancellationRequested();

            if (JsonText.FindRecordedHash(
                json: json,
                id: repair.Id
            ) is not JsonText.ValueSpan span) {
                continue;
            }

            // The replacement is the literal value, spliced in by index. Handing the hash to a substitution
            // pattern would let a leading digit be read as part of a capture-group reference.
            json = JsonText.Replace(
                json: json,
                span: span,
                replacement: $"\"{repair.Hash}\""
            );
            changed = true;
        }

        if (!changed) {
            return project.Solution;
        }

        var newText = SourceText.From(
            text: json,
            encoding: text.Encoding,
            checksumAlgorithm: text.ChecksumAlgorithm
        );

        return project.Solution.WithAdditionalDocumentText(
            documentId: manifestDocument.Id,
            text: newText
        );
    }

    /// <summary>
    /// Batches every drifted brand into one ledger rewrite.
    /// </summary>
    /// <remarks>
    /// The built-in batch fixer merges changed SOURCE documents, and this repair changes no source document at all —
    /// only an additional document — so every edit it computed would be discarded and the batch would report success
    /// having written nothing.
    /// </remarks>
    private sealed class LedgerFixAllProvider : FixAllProvider {
        public static readonly LedgerFixAllProvider Instance = new();

        // One physical VerifiedCode.json is linked into every project as its own additional document, so a
        // solution-wide batch would have to edit each project's copy in step. Keeping the file and the other
        // projects in step is the host's job, so no solution scope is advertised rather than half-performed.
        public override IEnumerable<FixAllScope> GetSupportedFixAllScopes() =>
            [FixAllScope.Document, FixAllScope.Project];
        public override async Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext) {
            var diagnostics = await GetDiagnosticsAsync(fixAllContext: fixAllContext).ConfigureAwait(continueOnCapturedContext: false);

            var repairs = diagnostics
                .Select(selector: diagnostic => (Diagnostic: diagnostic, Read: TryReadRepair(
                diagnostic: diagnostic,
                id: out var id,
                hash: out var hash
            ), Id: id, Hash: hash))
                .Where(predicate: candidate => candidate.Read)
                .GroupBy(
                keySelector: candidate => candidate.Id,
                comparer: StringComparer.Ordinal
            )
                .Select(selector: group => (Id: group.Key, group.First().Hash))
                .OrderBy(
                keySelector: repair => repair.Id,
                comparer: StringComparer.Ordinal
            )
                .ToArray();

            if (repairs.Length == 0) {
                return null;
            }

            var project = fixAllContext.Project;

            return CodeAction.Create(
                title: "Update every drifted verification brand",
                createChangedSolution: cancellationToken => UpdateManifestAsync(
                    project: project,
                    repairs: repairs,
                    cancellationToken: cancellationToken
                ),
                equivalenceKey: EquivalenceKey
            );
        }

        private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(FixAllContext fixAllContext) =>
            ((fixAllContext.Scope == FixAllScope.Document)
            ? await fixAllContext.GetDocumentDiagnosticsAsync(document: fixAllContext.Document!).ConfigureAwait(continueOnCapturedContext: false)
            : await fixAllContext.GetAllDiagnosticsAsync(project: fixAllContext.Project).ConfigureAwait(continueOnCapturedContext: false));
    }
}
