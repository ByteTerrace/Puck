using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Composition;
using Puck.World.Transpiler.Vocabulary;
using Puck.Transpiler.Diagnostics;
using Puck.Abstractions.Machines;

namespace Puck.World.Transpiler.Validation;

/// <summary>Validates lowered world definitions against engine semantic rules and maps errors to source spans.</summary>
public static class WorldSemanticValidator {
    /// <summary>The schema identifier a document declares to state that it is a whole world rather than a
    /// fragment.</summary>
    public const string RootSchemaId = "puck.world.definition.v1";

    // The source map indexes the source's own lowered document; a refusal's path indexes the composed one, whose
    // lists a basis or an import may have lengthened or reordered, so the path is traced back into the source's own
    // document first (WorldDocumentBasis.TraceToLayer), and a node only a basis or an import contributes has no span.
    private static SourceSpan ExtractSpanFromError(string error, SourceMap? sourceMap, JsonObject composed, JsonObject? source) {
        if (sourceMap is null) {
            return SourceSpan.None;
        }

        // Error strings frequently begin with path prefix like "views.layouts[0].slots[0].pipeline: ..." or "screens[0].frame"
        var colonIdx = error.IndexOf(value: ':');
        var pathToken = ((colonIdx > 0)
            ? error[..colonIdx].Trim()
            : (error.Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: ' '
            ).FirstOrDefault() ?? "")
        );

        var composedPointer = WorldDocumentPointers.ToJsonPointer(
            path: pathToken,
            table: WorldConstructs.Table
        );
        var jsonPointer = (((source is null) || ReferenceEquals(objA: source, objB: composed))
            ? composedPointer
            : WorldDocumentBasis.TraceToLayer(
                composed: composed,
                layer: source,
                pointer: composedPointer
            ));

        if (jsonPointer is null) {
            return SourceSpan.None;
        }

        // The map registers the nodes the emitter lowered, which are rarely the leaf the engine names; walking back
        // up the pointer finds the nearest enclosing node that does carry a span.
        while (jsonPointer.Length > 1) {
            if (sourceMap.TryGetSpan(
                jsonPointer: jsonPointer,
                span: out var span
            )) {
                return span;
            }
            var lastSegment = jsonPointer.LastIndexOf(value: '/');

            if (lastSegment <= 0) {
                break;
            }
            jsonPointer = jsonPointer[..lastSegment];
        }

        return SourceSpan.None;
    }

    /// <summary>Returns a value indicating whether <paramref name="loweredJson"/> is a ROOT — a whole world that
    /// stands on its own or on a basis chain — rather than a MODULE, a fragment some other root imports.</summary>
    /// <remarks>A root declares <see cref="RootSchemaId"/>, a <c>basis</c>, or both; every other document is a
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
            b: RootSchemaId,
            comparisonType: StringComparison.Ordinal
        )
        );
    }
    /// <summary>Composes <paramref name="loweredJson"/>'s <c>basis</c>/<c>imports</c> graph, rooted beside
    /// <paramref name="sourcePath"/>, through <see cref="PuckDocumentComposer"/> — the same composition the game
    /// boot path runs — and validates the composed document. A document naming neither composes to itself.</summary>
    /// <param name="loweredJson">The lowered JsonObject, not yet composed with its basis or imports.</param>
    /// <param name="sourceMap">The SourceMap linking JSON pointer paths to source AST spans. Spans for fields a
    /// basis or import supplied do not resolve (they trace no span in this source) and report at
    /// <see cref="SourceSpan.None"/>.</param>
    /// <param name="diagnostics">The DiagnosticBag to report composition and semantic errors into.</param>
    /// <param name="sourcePath">The <c>.puck</c> source file's own resolved path — basis and import references
    /// resolve relative to its directory.</param>
    /// <param name="machines">The deployment's machine vocabulary, supplied without loading code from the document.</param>
    /// <param name="catalogFingerprint">The stable metadata fingerprint for composition under <paramref name="machines"/>.</param>
    /// <returns>True if the composed world passed semantic validation without errors.</returns>
    public static bool ValidateComposedWorld(JsonObject loweredJson, SourceMap? sourceMap, DiagnosticBag diagnostics, string sourcePath, IMachineValidationCatalog? machines = null, string catalogFingerprint = "") =>
        (TryComposeWorld(
            catalogFingerprint: catalogFingerprint,
            composed: out var composed,
            diagnostics: diagnostics,
            loweredJson: loweredJson,
            machines: machines,
            sourceMap: sourceMap,
            sourcePath: sourcePath
        ) && ValidateWorld(
            catalogFingerprint: catalogFingerprint,
            diagnostics: diagnostics,
            loweredJson: composed,
            machines: machines,
            source: loweredJson,
            sourceMap: sourceMap
        ));
    /// <summary>Composes <paramref name="loweredJson"/>'s <c>basis</c>/<c>imports</c> graph, rooted beside
    /// <paramref name="sourcePath"/>, through <see cref="PuckDocumentComposer"/>, and reports a refused composition
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
    /// <returns><see langword="true"/> when the graph composed.</returns>
    public static bool TryComposeWorld(JsonObject loweredJson, SourceMap? sourceMap, DiagnosticBag diagnostics, string sourcePath, out JsonObject composed, IMachineValidationCatalog? machines = null, string catalogFingerprint = "") {
        ArgumentNullException.ThrowIfNull(loweredJson);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        composed = loweredJson;

        if ((loweredJson["basis"] is null) && (loweredJson["imports"] is null)) {
            return true;
        }

        var rootBytes = Encoding.UTF8.GetBytes(s: loweredJson.ToJsonString());

        if (!PuckDocumentComposer.TryComposeWorldDocument(
            catalog: machines,
            catalogFingerprint: catalogFingerprint,
            chainBytes: out _,
            composed: out var chain,
            reason: out var composeReason,
            rootBytes: rootBytes,
            rootResolvedPath: sourcePath
        )) {
            var span = (((sourceMap is not null) && sourceMap.TryGetSpan(
                jsonPointer: "/basis",
                span: out var basisSpan
            ))
                ? basisSpan
                : SourceSpan.None
            );

            diagnostics.ReportError(
                code: PuckDiagnosticCodes.CompositionRefused,
                message: $"Basis/import composition refused: {composeReason}",
                span: span
            );
            return false;
        }

        composed = (chain ?? loweredJson);

        return true;
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
    /// <returns>True if the world passed semantic validation without errors.</returns>
    public static bool ValidateWorld(JsonObject loweredJson, SourceMap? sourceMap, DiagnosticBag diagnostics, IMachineValidationCatalog? machines = null, string catalogFingerprint = "", JsonObject? source = null) {
        ArgumentNullException.ThrowIfNull(loweredJson);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var jsonString = loweredJson.ToJsonString();

        if (!WorldDefinitionFileSource.TryParseDocument(
            definition: out var definition,
            json: jsonString,
            reason: out var parseReason,
            sourceName: "document"
        )) {
            diagnostics.ReportError(
                code: PuckDiagnosticCodes.SchemaRejected,
                message: $"Document structure rejected by engine schema: {parseReason}",
                span: SourceSpan.None
            );
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

        var errors = new List<string>();
        var deferred = new List<string>();
        var undeclaredEnums = UndeclaredEnums(definition: definition);

        WorldDefinitionValidator.TryValidateLocally(
            definition,
            machines,
            errors,
            deferred: deferred,
            out _
        );

        foreach (var error in errors) {
            diagnostics.ReportError(
                code: (undeclaredEnums.Contains(item: error)
                    ? PuckDiagnosticCodes.StateEnumUndeclared
                    : PuckDiagnosticCodes.SemanticValidation),
                message: error,
                span: ExtractSpanFromError(
                    composed: loweredJson,
                    error: error,
                    source: source,
                    sourceMap: sourceMap
                )
            );
        }

        // A check the validator deferred to the host that runs the world is a notice, never a finding against the
        // author: a host without that catalog cannot answer it either way.
        foreach (var notice in deferred) {
            diagnostics.ReportInformation(
                code: PuckDiagnosticCodes.SemanticValidationDeferred,
                message: notice,
                span: ExtractSpanFromError(
                    composed: loweredJson,
                    error: notice,
                    source: source,
                    sourceMap: sourceMap
                )
            );
        }

        return (errors.Count == 0);
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
