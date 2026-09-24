using Puck.Abstractions.Machines;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Validation;

/// <summary>The source tier of one <c>.puck</c> source's diagnosis: what its compile reported, and what the semantic
/// tier needs to go on.</summary>
/// <param name="Diagnostics">What the parse, the import walk, the lowering and the lint reported.</param>
/// <param name="Document">The parsed tree (with its imported module declarations, for a world source), or
/// <see langword="null"/> when the source did not parse.</param>
/// <param name="Compilation">The world compilation, or <see langword="null"/> for a source in another vocabulary or
/// one that did not parse.</param>
/// <param name="SourcePath">The file the text came from, or <see langword="null"/>.</param>
public sealed record WorldSourceDiagnosis(DiagnosticBag Diagnostics, DocumentNode? Document, WorldCompilation? Compilation, string? SourcePath) {
    /// <summary>Gets whether the semantic tier has anything to check: a world source that compiled without an error
    /// and lowered to at least one document.</summary>
    public bool HasSemanticTier => (
        (Compilation is { } compilation) &&
        !Diagnostics.HasErrors &&
        ((compilation.Worlds.Count > 0) || (compilation.Json is not null))
    );
}
/// <summary>Everything an author is told about one <c>.puck</c> source: what the parse, the lowering, the lint, the
/// engine's own validation of the composed world, and the reference lint each report. <c>puck lint</c> and the
/// language server both publish exactly this, so a refusal one reports is a refusal the other reports.</summary>
/// <remarks>The diagnosis runs in two tiers. The source tier (<see cref="DiagnoseSource"/>) is the compile: parse,
/// import walk, lowering and the syntax lint, which read the source and the modules it imports. The semantic tier
/// (<see cref="DiagnoseSemantic"/>) composes the world through its basis and runtime imports and runs the engine's
/// validation and the reference lint over it; it runs only when the source tier reported no error, and costs as much
/// as the composed world is large. <see cref="Diagnose"/> is both.</remarks>
public static class WorldSourceDiagnostics {
    /// <summary>Diagnoses <paramref name="source"/> through both tiers.</summary>
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
        var diagnosis = DiagnoseSource(
            cancellationToken: cancellationToken,
            foreign: foreign,
            source: source,
            sourcePath: sourcePath,
            vocabularies: vocabularies
        );

        diagnosis.Diagnostics.AddRange(diagnostics: DiagnoseSemantic(
            cancellationToken: cancellationToken,
            catalogFingerprint: catalogFingerprint,
            diagnosis: diagnosis,
            machines: machines
        ));

        return diagnosis.Diagnostics;
    }
    /// <summary>Runs the source tier: parse, import walk, lowering and syntax lint. A source in another vocabulary is
    /// parsed with that vocabulary and handed to <paramref name="foreign"/>, whose diagnostics are its whole
    /// diagnosis.</summary>
    /// <param name="source">The source text.</param>
    /// <param name="sourcePath">The file the text came from, or <see langword="null"/>.</param>
    /// <param name="vocabularies">Resolves the source's vocabulary; the world vocabulary alone when omitted.</param>
    /// <param name="foreign">Diagnoses a document written in another vocabulary.</param>
    /// <param name="cancellationToken">Cancels evaluation and expansion.</param>
    /// <returns>The source tier's diagnosis, carrying the compilation the semantic tier reads.</returns>
    public static WorldSourceDiagnosis DiagnoseSource(
        string source,
        string? sourcePath = null,
        DocumentVocabularyResolver? vocabularies = null,
        Func<DocumentNode, string?, DiagnosticBag, bool>? foreign = null,
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

            return new WorldSourceDiagnosis(
                Compilation: null,
                Diagnostics: diagnostics,
                Document: parsed,
                SourcePath: sourcePath
            );
        }

        // Source modules must be present before world instances are expanded. Reference lint also composes
        // runtime basis/import documents, which are distinct from these compile-time module declarations.
        var compilation = WorldCompiler.Compile(
            cancellationToken: cancellationToken,
            diagnostics: diagnostics,
            imports: ImportHandling.Validate,
            source: source,
            sourceMap: new SourceMap(),
            sourcePath: sourcePath,
            vocabulary: worldVocabulary,
            allowMultiple: true
        );

        if (compilation.Document is not null) {
            PuckLinter.Lint(
                diagnostics: diagnostics,
                document: compilation.Document
            );
        }

        return new WorldSourceDiagnosis(
            Compilation: ((compilation.Document is null)
                ? null
                : compilation
            ),
            Diagnostics: diagnostics,
            Document: compilation.Document,
            SourcePath: sourcePath
        );
    }
    /// <summary>Runs the semantic tier over a source tier's compilation: the engine's validation of the composed world
    /// and the reference lint, each composing the world through its basis and runtime imports. Nothing runs when the
    /// source tier reported an error (<see cref="WorldSourceDiagnosis.HasSemanticTier"/>).</summary>
    /// <param name="diagnosis">The source tier's diagnosis.</param>
    /// <param name="machines">The deployment's machine vocabulary.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for composition under
    /// <paramref name="machines"/>.</param>
    /// <param name="cancellationToken">Cancels the tier between its stages.</param>
    /// <returns>The semantic tier's diagnostics alone; the source tier's are left as they were.</returns>
    public static DiagnosticBag DiagnoseSemantic(
        WorldSourceDiagnosis diagnosis,
        IMachineValidationCatalog? machines = null,
        string catalogFingerprint = "",
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(argument: diagnosis);

        var diagnostics = new DiagnosticBag();

        if (!diagnosis.HasSemanticTier) {
            return diagnostics;
        }

        var compilation = diagnosis.Compilation!;
        var sourcePath = diagnosis.SourcePath;

        if (compilation.Worlds.Count > 0) {
            foreach (var output in compilation.Worlds) {
                cancellationToken.ThrowIfCancellationRequested();

                if (sourcePath is not null) {
                    if (WorldSemanticValidator.TryComposeWorld(output.Json, output.SourceMap, diagnostics, sourcePath, out var composedWorld, machines: machines, catalogFingerprint: catalogFingerprint)) {
                        WorldSemanticValidator.ValidateWorld(composedWorld, output.SourceMap, diagnostics, machines: machines, catalogFingerprint: catalogFingerprint, source: output.Json);
                        PuckLinter.LintReferences(catalogFingerprint: catalogFingerprint, composed: composedWorld, diagnostics: diagnostics, document: output.Json, machines: machines, sourceMap: output.SourceMap, sourcePath: sourcePath);
                    }
                } else if ((output.Json["basis"] is null) && (output.Json["imports"] is null)) {
                    WorldSemanticValidator.ValidateWorld(output.Json, output.SourceMap, diagnostics, machines: machines, catalogFingerprint: catalogFingerprint);
                }
            }
            return diagnostics;
        }

        var json = compilation.Json!;
        var sourceMap = compilation.SourceMap;
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

        // The source composes once, and a refused composition is reported once (PUCK035); the validation and the
        // reference lint both read the one composed document.
        if (!WorldSemanticValidator.TryComposeWorld(
            catalogFingerprint: catalogFingerprint,
            composed: out var composed,
            diagnostics: diagnostics,
            loweredJson: json,
            machines: machines,
            sourceMap: sourceMap,
            sourcePath: sourcePath
        )) {
            return diagnostics;
        }

        // Only a root composes to a whole world; validating a module as one reports as missing every field
        // whichever root imports it supplies.
        if (WorldSemanticValidator.IsRootDocument(loweredJson: json)) {
            _ = WorldSemanticValidator.ValidateWorld(
                catalogFingerprint: catalogFingerprint,
                diagnostics: diagnostics,
                loweredJson: composed,
                machines: machines,
                source: json,
                sourceMap: sourceMap
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        PuckLinter.LintReferences(
            catalogFingerprint: catalogFingerprint,
            composed: composed,
            diagnostics: diagnostics,
            document: json,
            machines: machines,
            sourceMap: sourceMap,
            sourcePath: sourcePath
        );

        return diagnostics;
    }
}
