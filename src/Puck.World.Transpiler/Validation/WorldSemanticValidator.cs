using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Composition;
using Puck.Transpiler.Diagnostics;
using Puck.Abstractions.Machines;
using Puck.World.Authoring;

namespace Puck.World.Transpiler.Validation;

/// <summary>Validates lowered world definitions against engine semantic rules and maps errors to source spans.</summary>
public static class WorldSemanticValidator {
    /// <summary>Returns a value indicating whether <paramref name="loweredJson"/> is a ROOT — a whole world that
    /// stands on its own or on a basis chain — rather than a MODULE, a fragment some other root imports.</summary>
    /// <remarks>A root declares <see cref="WorldDefinition.SchemaVersion"/>, a <c>basis</c>, or both; every other document is a
    /// module. Only a root is worth validating as a world (a module reports as missing every field its importer
    /// supplies) and only a root's unresolvable names are real findings. <c>imports</c> alone does not make a root:
    /// a module may itself import sibling modules.</remarks>
    /// <param name="loweredJson">The lowered document.</param>
    /// <returns><see langword="true"/> when the document is a root.</returns>
    public static bool IsRootDocument(JsonObject loweredJson) {
        ArgumentNullException.ThrowIfNull(loweredJson);

        return (
            (loweredJson["basis"] is not null) ||
            string.Equals(
            a: loweredJson["schema"]?.ToString(),
            b: WorldDefinition.SchemaVersion,
            comparisonType: StringComparison.Ordinal
        )
        );
    }
    /// <summary>Composes <paramref name="loweredJson"/>'s <c>basis</c>/<c>imports</c> graph, rooted beside
    /// <paramref name="sourcePath"/>, through <see cref="WorldCompileSession"/> — the same composition the game
    /// boot path runs — and validates the composed document when <paramref name="loweredJson"/> is a root
    /// (<see cref="IsRootDocument"/>; validating a module as a world reports as missing every field the root that
    /// imports it supplies). A document naming neither composes to itself. This is the one compose-and-validate path:
    /// a consumer that also needs the composed document (the reference lint) reads it from the result instead of
    /// composing again. A finding against a row only a basis or an import document authors is placed on that
    /// document's line, and dropped when an <c>allow</c> line there acknowledges it (<see cref="WorldFindingLocator"/>);
    /// one the source or a library it imports acknowledges is dropped by
    /// <see cref="WorldCompilation.Acknowledgements"/>, which the caller runs over every finding it reports.</summary>
    /// <param name="loweredJson">The lowered JsonObject, not yet composed with its basis or imports.</param>
    /// <param name="sourceMap">The SourceMap linking JSON pointer paths to source AST spans.</param>
    /// <param name="diagnostics">The DiagnosticBag to report composition and semantic errors into.</param>
    /// <param name="sourcePath">The <c>.puck</c> source file's own resolved path — basis and import references
    /// resolve relative to its directory.</param>
    /// <param name="machines">The deployment's machine vocabulary, supplied without loading code from the document.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for composition under <paramref name="machines"/>.</param>
    /// <param name="sourceCompilation">The already compiled root and its input facts, when available.</param>
    /// <returns>The composed document, validated when it is a root; <see langword="null"/> when the composition was
    /// refused (reported as PUCK035). Whether the validation passed is in <paramref name="diagnostics"/>.</returns>
    public static JsonObject? ValidateComposedWorld(JsonObject loweredJson, SourceMap? sourceMap, DiagnosticBag diagnostics, string sourcePath, IMachineValidationCatalog? machines = null, string catalogFingerprint = "", WorldCompiledSource? sourceCompilation = null) {
        // The composition keeps no origins: tracking where every value was written costs a large share of a compose
        // and is read only to place a finding no layer of the source authors. Such a finding composes again with
        // origins, once, when the first one asks (WorldFindingLocator).
        if (!TryComposeWorld(
            catalogFingerprint: catalogFingerprint,
            composed: out var composed,
            diagnostics: diagnostics,
            loweredJson: loweredJson,
            machines: machines,
            sourceCompilation: sourceCompilation,
            sourceMap: sourceMap,
            sourcePath: sourcePath
        )) {
            return null;
        }

        if (IsRootDocument(loweredJson: loweredJson)) {
            _ = ValidatePlaced(
                catalogFingerprint: catalogFingerprint,
                diagnostics: diagnostics,
                loweredJson: composed,
                machines: machines,
                provenance: () => {
                    var origins = new WorldDocumentOrigins();

                    return (TryComposeWorld(
                        catalogFingerprint: catalogFingerprint,
                        composed: out var tracked,
                        diagnostics: new DiagnosticBag(),
                        loweredJson: loweredJson,
                        machines: machines,
                        origins: origins,
                        sourceCompilation: sourceCompilation,
                        sourceMap: sourceMap,
                        sourcePath: sourcePath
                    ) ? new WorldFindingProvenance(Composed: tracked, Origins: origins) : null);
                },
                source: loweredJson,
                sourceMap: sourceMap,
                sourcePath: sourcePath
            );
        }

        return composed;
    }
    /// <summary>Composes <paramref name="loweredJson"/>'s <c>basis</c>/<c>imports</c> graph, rooted beside
    /// <paramref name="sourcePath"/>, through <see cref="WorldCompileSession"/>, and reports a refused composition
    /// once, as PUCK035 at the <c>basis</c>. Every consumer of a source's composed document composes it here, so one
    /// diagnosis composes it once and reports its refusal once.</summary>
    /// <param name="loweredJson">The lowered JsonObject, not yet composed with its basis or imports.</param>
    /// <param name="sourceMap">The SourceMap linking JSON pointer paths to source AST spans.</param>
    /// <param name="diagnostics">The DiagnosticBag a refused composition is reported into.</param>
    /// <param name="sourcePath">The <c>.puck</c> source file's own resolved path — basis and import references
    /// resolve relative to its directory.</param>
    /// <param name="composed">The composed document; <paramref name="loweredJson"/> itself when it names neither a
    /// basis nor imports.</param>
    /// <param name="machines">The deployment's machine vocabulary, supplied without loading code from the document.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for composition under <paramref name="machines"/>.</param>
    /// <param name="sourceCompilation">The already compiled root and its input facts, when available.</param>
    /// <param name="origins">Collects the authored origin of every value the composition retains, so a finding
    /// against a basis's or an import's row can be placed in the source that authored it
    /// (<see cref="ValidateWorld"/>); <see langword="null"/> to keep none.</param>
    /// <returns><see langword="true"/> when the graph composed.</returns>
    public static bool TryComposeWorld(JsonObject loweredJson, SourceMap? sourceMap, DiagnosticBag diagnostics, string sourcePath, out JsonObject composed, IMachineValidationCatalog? machines = null, string catalogFingerprint = "", WorldCompiledSource? sourceCompilation = null, WorldDocumentOrigins? origins = null) {
        ArgumentNullException.ThrowIfNull(loweredJson);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        composed = loweredJson;

        if ((loweredJson["basis"] is null) && (loweredJson["imports"] is null)) {
            return true;
        }

        var rootBytes = Encoding.UTF8.GetBytes(s: loweredJson.ToJsonString());

        if (!WorldCompileSession.Shared.TryCompose(
            catalog: machines,
            catalogFingerprint: catalogFingerprint,
            composed: out var chain,
            origins: origins,
            reason: out var composeReason,
            rootBytes: rootBytes,
            sourceCompilation: sourceCompilation,
            rootResolvedPath: sourcePath
        )) {
            var span = CompositionRefusalSpan(
                loweredJson: loweredJson,
                reason: composeReason,
                sourceMap: sourceMap
            );

            diagnostics.ReportError(
                code: PuckDiagnosticCodes.CompositionRefused,
                message: $"Could not compose world: {composeReason}",
                span: span
            );
            return false;
        }

        composed = (chain ?? loweredJson);

        return true;
    }

    // Locates a composition refusal on the specific `/imports/<i>` entry named in `reason`, falling back to `/basis`
    // and then `/imports/0`, so an imports-only document never lands its refusal on line 1.
    private static SourceSpan CompositionRefusalSpan(JsonObject loweredJson, string reason, SourceMap? sourceMap) {
        if (sourceMap is null) {
            return SourceSpan.None;
        }
        if (loweredJson["imports"] is JsonArray imports) {
            for (var index = 0; (index < imports.Count); index++) {
                var target = ((imports[index] as JsonObject)?["document"]?.ToString() ?? imports[index]?.ToString());

                if (
                    !string.IsNullOrEmpty(value: target) &&
                    reason.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: target) &&
                    sourceMap.TryGetSpan(jsonPointer: $"/imports/{index}", span: out var importSpan)
                ) {
                    return importSpan;
                }
            }
        }
        if (sourceMap.TryGetSpan(jsonPointer: "/basis", span: out var basisSpan)) {
            return basisSpan;
        }
        if (sourceMap.TryGetSpan(jsonPointer: "/imports/0", span: out var firstImportSpan)) {
            return firstImportSpan;
        }

        return SourceSpan.None;
    }
    /// <summary>Validates a lowered world definition JsonObject using Puck.World.Schema's engine validator. The
    /// document is validated exactly as given — a document naming a <c>basis</c> or <c>imports</c> must already be
    /// composed (see <see cref="ValidateComposedWorld"/>), or fields the basis chain would have filled in read as
    /// missing.</summary>
    /// <param name="loweredJson">The lowered JsonObject.</param>
    /// <param name="sourceMap">The SourceMap linking JSON pointer paths to source AST spans.</param>
    /// <param name="diagnostics">The DiagnosticBag to report semantic errors into.</param>
    /// <param name="machines">The deployment's machine vocabulary; unavailable provider checks are reported as errors.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for composition under <paramref name="machines"/>.</param>
    /// <param name="source">The source's own lowered document when <paramref name="loweredJson"/> is its composition,
    /// the document <paramref name="sourceMap"/> indexes; a refusal's path is traced back into it
    /// (<see cref="WorldDocumentBasis.TraceToLayer"/>), so a basis's or an import's rows cannot shift the line a
    /// refusal lands on. <see langword="null"/> when <paramref name="loweredJson"/> is the source's own document.</param>
    /// <param name="sourcePath">The root document's path, when known, so referenced assets resolve beside it.</param>
    /// <returns>True if the world passed semantic validation without errors.</returns>
    /// <remarks>A finding against a row only a basis or an import authors is reported without a location; the
    /// compose-and-validate path (<see cref="ValidateComposedWorld"/>) places it on the line that authored it.</remarks>
    public static bool ValidateWorld(JsonObject loweredJson, SourceMap? sourceMap, DiagnosticBag diagnostics, IMachineValidationCatalog? machines = null, string catalogFingerprint = "", JsonObject? source = null, string? sourcePath = null) =>
        ValidatePlaced(catalogFingerprint: catalogFingerprint, diagnostics: diagnostics, loweredJson: loweredJson, machines: machines, provenance: null, source: source, sourceMap: sourceMap, sourcePath: sourcePath);
    // `provenance` composes the world again with its authored origins when a finding no layer of the source authors asks
    // to be placed on the basis or import line that wrote it, and dropped when that source acknowledges it with an
    // `allow` line; null reports such a finding without a location.
    private static bool ValidatePlaced(JsonObject loweredJson, SourceMap? sourceMap, DiagnosticBag diagnostics, IMachineValidationCatalog? machines, string catalogFingerprint, JsonObject? source, string? sourcePath, Func<WorldFindingProvenance?>? provenance) {
        ArgumentNullException.ThrowIfNull(loweredJson);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var jsonString = loweredJson.ToJsonString();

        if (!WorldDefinitionFileSource.TryReadDocument(
            definition: out var definition,
            json: jsonString,
            reason: out var parseReason,
            refusal: out var refusal,
            sourceName: "document"
        )) {
            Report(diagnostics: diagnostics, finding: new WorldFindingLocator(composed: loweredJson, provenance: provenance, source: source, sourceMap: sourceMap, sourcePath: sourcePath).Locate(
                code: PuckDiagnosticCodes.SchemaRejected,
                message: (refusal?.Authored ?? parseReason),
                pointer: refusal?.Pointer,
                severity: DiagnosticSeverity.Error
            ));
            return false;
        }

        if (definition is null) {
            diagnostics.ReportError(
                code: PuckDiagnosticCodes.DeserializeForValidation,
                message: "Failed to deserialize lowered world definition for validation.",
                span: SourceSpan.None
            );
            return false;
        }

        if (sourcePath is not null) {
            definition = definition with { DocumentDirectory = WorldDocumentPaths.DirectoryOf(documentPath: sourcePath) };
        }

        var errors = new List<WorldValidationFinding>();
        var deferred = new List<WorldValidationFinding>();
        var undeclaredEnums = UndeclaredEnums(definition: definition);

        WorldDefinitionValidator.TryValidateLocally(
            definition,
            machines,
            errors,
            deferred: deferred,
            out _
        );

        var locator = new WorldFindingLocator(composed: loweredJson, provenance: provenance, source: source, sourceMap: sourceMap, sourcePath: sourcePath);

        foreach (var error in errors) {
            Report(diagnostics: diagnostics, finding: locator.Locate(
                code: (undeclaredEnums.Contains(item: error.Message)
                    ? PuckDiagnosticCodes.StateEnumUndeclared
                    : PuckDiagnosticCodes.SemanticValidation),
                message: error.Message,
                pointer: Vocabulary.WorldDocumentPointers.ToJsonPointer(path: error.Path, table: Vocabulary.WorldConstructs.Table),
                severity: DiagnosticSeverity.Error
            ));
        }

        // A check the validator deferred to the host that runs the world is a notice, never a finding against the
        // author: a host without that catalog cannot answer it either way.
        foreach (var notice in deferred) {
            Report(diagnostics: diagnostics, finding: locator.Locate(
                code: PuckDiagnosticCodes.SemanticValidationDeferred,
                message: notice.Message,
                pointer: Vocabulary.WorldDocumentPointers.ToJsonPointer(path: notice.Path, table: Vocabulary.WorldConstructs.Table),
                severity: DiagnosticSeverity.Information
            ));
        }

        foreach (var warning in MovingBlends(definition: definition)) {
            Report(diagnostics: diagnostics, finding: locator.Locate(
                code: PuckDiagnosticCodes.MovingBlend,
                message: warning.Message,
                pointer: Vocabulary.WorldDocumentPointers.ToJsonPointer(path: warning.Path, table: Vocabulary.WorldConstructs.Table),
                severity: DiagnosticSeverity.Warning
            ));
        }
        foreach (var warning in UngroupedBlends(definition: definition)) {
            Report(diagnostics: diagnostics, finding: locator.Locate(
                code: PuckDiagnosticCodes.UngroupedBlend,
                message: warning.Message,
                pointer: Vocabulary.WorldDocumentPointers.ToJsonPointer(path: warning.Path, table: Vocabulary.WorldConstructs.Table),
                severity: DiagnosticSeverity.Warning
            ));
        }
        foreach (var warning in UnplayedMotions(definition: definition)) {
            Report(diagnostics: diagnostics, finding: locator.Locate(
                code: PuckDiagnosticCodes.UnplayedMotion,
                message: warning.Message,
                pointer: Vocabulary.WorldDocumentPointers.ToJsonPointer(path: warning.Path, table: Vocabulary.WorldConstructs.Table),
                severity: DiagnosticSeverity.Warning
            ));
        }
        foreach (var warning in EffectorContests(definition: definition)) {
            Report(diagnostics: diagnostics, finding: locator.Locate(
                code: PuckDiagnosticCodes.EffectorContest,
                message: warning.Message,
                pointer: Vocabulary.WorldDocumentPointers.ToJsonPointer(path: warning.Path, table: Vocabulary.WorldConstructs.Table),
                severity: DiagnosticSeverity.Warning
            ));
        }

        return (errors.Count == 0);
    }
    private static void Report(DiagnosticBag diagnostics, Diagnostic? finding) {
        if (finding is not null) {
            diagnostics.Report(diagnostic: finding);
        }
    }
    /// <summary>Returns a warning for each blend of an animated creation's pooled groups that joins shapes moving
    /// independently (<see cref="CreationMotion.MovingBlends"/>) and whose group creases
    /// (<see cref="CreationMotion.Creases"/>) or which a moving cutter takes part in as an operand
    /// (<see cref="CreationMovingBlend.Operand"/>), carrying its document path as data and naming the creation, the
    /// two shapes, the blend and what the bake makes of it. A group that skins draws its seam smoothly and is not
    /// warned about.</summary>
    /// <param name="definition">The world.</param>
    /// <returns>The warnings, in prototype and shape order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldValidationFinding> MovingBlends(WorldDefinition definition) => CreationWarnings(
        definition: definition,
        warn: static document => {
            var rig = CreationRig.Of(document: document);

            return CreationMotion.MovingBlends(rig: rig).Where(predicate: blend => (blend.Operand || CreationMotion.Creases(blend: blend, rig: rig))).Select(selector: blend => ($"shapes[{blend.Shape}].blend", CreationMotion.Describe(blend: blend, rig: rig)));
        }
    );
    /// <summary>Returns a warning for each ungrouped shape of an animated creation that declares a blend other than
    /// <c>Union</c> (<see cref="CreationMotion.UngroupedBlends"/>), which the pooled bake draws as a hard union,
    /// carrying its document path as data.</summary>
    /// <param name="definition">The world.</param>
    /// <returns>The warnings, in prototype and shape order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldValidationFinding> UngroupedBlends(WorldDefinition definition) => CreationWarnings(
        definition: definition,
        warn: static document => CreationMotion.UngroupedBlends(rig: CreationRig.Of(document: document)).Select(selector: shape => ($"shapes[{shape}].blend", CreationMotion.DescribeUngrouped(document: document, shape: shape)))
    );
    /// <summary>Returns a warning for each motion a creation's driver authors on a shape its mask does not cover
    /// (<see cref="CreationRig.Unplayed"/>), which never plays, carrying its driver's document path as data.</summary>
    /// <param name="definition">The world.</param>
    /// <returns>The warnings, in prototype order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldValidationFinding> UnplayedMotions(WorldDefinition definition) => CreationWarnings(
        definition: definition,
        warn: static document => {
            var rig = CreationRig.Of(document: document);

            return rig.Unplayed.Select(selector: unplayed => ($"drivers[{unplayed.Driver}].parts", CreationMotion.DescribeUnplayed(rig: rig, unplayed: unplayed)));
        }
    );
    /// <summary>Returns a warning for each later effector of a creation that writes a shape an earlier effector's tip hangs
    /// from (<see cref="CreationRig.Contests"/>), carrying its document path as data.</summary>
    /// <param name="definition">The world.</param>
    /// <returns>The warnings, in prototype order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<WorldValidationFinding> EffectorContests(WorldDefinition definition) => CreationWarnings(
        definition: definition,
        warn: static document => {
            var rig = CreationRig.Of(document: document);

            return rig.Contests.Select(selector: contest => ($"effectors[{contest.Later}].chain", CreationMotion.DescribeContest(contest: contest, rig: rig)));
        }
    );
    // A warning for each member `warn` names in each creation, its document path carried as structured data and its
    // message written in authored terms (creation id and sentence, with no internal JSON path prefix).
    private static IReadOnlyList<WorldValidationFinding> CreationWarnings(WorldDefinition definition, Func<CreationDocument, IEnumerable<(string Path, string Text)>> warn) {
        ArgumentNullException.ThrowIfNull(definition);

        var warnings = new List<WorldValidationFinding>();
        var creations = definition.Creations;

        for (var index = 0; (index < creations.Count); index++) {
            if (creations[index]?.Document is not { } document) { continue; }
            foreach (var (path, text) in warn(arg: document)) {
                warnings.Add(item: new WorldValidationFinding(
                    Message: $"creation '{creations[index].Id}' {text}",
                    Path: $"prototypes[{index}].doc.{path}"
                ));
            }
        }

        return warnings;
    }

    // The engine words each refusal once (WorldDefinitionValidator.UndeclaredRowEnum and UndeclaredRecordFieldEnum);
    // the ones this document draws are that refusal for each authored row and each record field naming an enum the
    // composed section does not declare, and they are the refusals coded PUCK119.
    private static HashSet<string> UndeclaredEnums(WorldDefinition definition) {
        var refusals = new HashSet<string>(comparer: StringComparer.Ordinal);
        var rows = definition.AuthoredState;

        for (var index = 0; (index < rows.Count); index++) {
            if ((rows[index] is { Enum: { } name, Kind: Puck.State.CellKind.Int } row) && (definition.EnumOf(row: row) is null)) {
                _ = refusals.Add(item: WorldDefinitionValidator.UndeclaredRowEnum(
                    enumName: name.Value,
                    path: WorldDefinitionValidator.StateRowPath(index: index),
                    row: row.Name
                ));
            }
        }

        var records = (definition.StateRaw?.Records ?? []);

        for (var recordIndex = 0; (recordIndex < records.Count); recordIndex++) {
            var fields = (records[recordIndex]?.Fields ?? []);

            for (var fieldIndex = 0; (fieldIndex < fields.Count); fieldIndex++) {
                if ((fields[fieldIndex] is { Enum: { } name, Kind: Puck.State.CellKind.Int } field) && (definition.EnumOf(field: field) is null)) {
                    _ = refusals.Add(item: WorldDefinitionValidator.UndeclaredRecordFieldEnum(
                        enumName: name.Value,
                        field: field.Name.Value,
                        path: WorldDefinitionValidator.StateRecordFieldPath(field: fieldIndex, record: recordIndex),
                        record: records[recordIndex].Name.Value
                    ));
                }
            }
        }

        return refusals;
    }
}
