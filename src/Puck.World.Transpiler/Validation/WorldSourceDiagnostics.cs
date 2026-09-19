using Puck.Abstractions.Machines;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Validation;

/// <summary>Everything an author is told about one <c>.puck</c> source: what the parse, the lowering, the lint, the
/// engine's own validation of the composed world, and the reference lint each report. <c>puck lint</c> and the
/// language server both publish exactly this, so a refusal one reports is a refusal the other reports.</summary>
public static class WorldSourceDiagnostics {
    /// <summary>Diagnoses <paramref name="source"/>.</summary>
    /// <param name="source">The source text.</param>
    /// <param name="sourcePath">The file the text came from, or <see langword="null"/> for a buffer that has none.
    /// A source with no path cannot compose a <c>basis</c> or an import, so a document naming either is validated
    /// no further than its lowering.</param>
    /// <param name="vocabularies">Resolves the vocabulary the source is written in from its declared schema; the
    /// world vocabulary alone when omitted.</param>
    /// <param name="foreign">Diagnoses a document written in another vocabulary, returning
    /// <see langword="true"/> when it supplied that document's diagnostics.</param>
    /// <param name="machines">The deployment's machine vocabulary.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for composition under
    /// <paramref name="machines"/>.</param>
    /// <param name="cancellationToken">Cancels evaluation and expansion.</param>
    /// <returns>Every diagnostic, in the order the stages ran.</returns>
    public static DiagnosticBag Diagnose(
        string source,
        string? sourcePath = null,
        DocumentVocabularyResolver? vocabularies = null,
        Func<DocumentNode, string?, DiagnosticBag, bool>? foreign = null,
        IMachineValidationCatalog? machines = null,
        string catalogFingerprint = "",
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(argument: source);

        var diagnostics = new DiagnosticBag();
        var vocabulary = (vocabularies?.Resolve(source: source) ?? WorldDocumentVocabulary.Instance);

        if (vocabulary is not WorldDocumentVocabulary worldVocabulary) {
            var parsed = PuckParser.ParseDocumentWithDiagnostics(
                diagnostics: diagnostics,
                source: source,
                vocabulary: vocabulary
            ).Value;

            if (parsed is not null) {
                _ = foreign?.Invoke(
                    arg1: parsed,
                    arg2: sourcePath,
                    arg3: diagnostics
                );
            }

            return diagnostics;
        }

        // The reference lint composes the import graph itself, so the compile does not walk it a second time.
        var sourceMap = new SourceMap();
        var compilation = WorldCompiler.Compile(
            cancellationToken: cancellationToken,
            diagnostics: diagnostics,
            imports: ImportHandling.Ignore,
            source: source,
            sourceMap: sourceMap,
            sourcePath: sourcePath,
            vocabulary: worldVocabulary
        );

        if (compilation.Document is null) {
            return diagnostics;
        }

        PuckLinter.Lint(
            diagnostics: diagnostics,
            document: compilation.Document
        );

        if ((compilation.Json is not { } json) || diagnostics.HasErrors) {
            return diagnostics;
        }

        var composes = ((json["basis"] is not null) || (json["imports"] is not null));

        if (sourcePath is null) {
            if (!composes && WorldSemanticValidator.IsRootDocument(loweredJson: json)) {
                _ = WorldSemanticValidator.ValidateWorld(
                    catalogFingerprint: catalogFingerprint,
                    diagnostics: diagnostics,
                    loweredJson: json,
                    machines: machines,
                    sourceMap: sourceMap
                );
            }

            return diagnostics;
        }

        // Only a root composes to a whole world; validating a module as one reports as missing every field
        // whichever root imports it supplies.
        if (WorldSemanticValidator.IsRootDocument(loweredJson: json)) {
            _ = WorldSemanticValidator.ValidateComposedWorld(
                catalogFingerprint: catalogFingerprint,
                diagnostics: diagnostics,
                loweredJson: json,
                machines: machines,
                sourceMap: sourceMap,
                sourcePath: sourcePath
            );
        }

        PuckLinter.LintReferences(
            catalogFingerprint: catalogFingerprint,
            diagnostics: diagnostics,
            document: json,
            machines: machines,
            sourceMap: sourceMap,
            sourcePath: sourcePath
        );

        return diagnostics;
    }
}
