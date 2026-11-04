using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Puck.World.Transpiler.Composition;
using Puck.Transpiler.Diagnostics;
using Puck.Abstractions.Machines;
using Puck.World.Authoring;
using Puck.World.Transpiler.Vocabulary;

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
    /// <param name="sourceCompilation">The already compiled root and its input facts, when available.</param>
    /// <returns>True if the composed world passed semantic validation without errors.</returns>
    public static bool ValidateComposedWorld(JsonObject loweredJson, SourceMap? sourceMap, DiagnosticBag diagnostics, string sourcePath, IMachineValidationCatalog? machines = null, string catalogFingerprint = "", WorldCompiledSource? sourceCompilation = null) =>
        (TryComposeWorld(
            catalogFingerprint: catalogFingerprint,
            composed: out var composed,
            diagnostics: diagnostics,
            loweredJson: loweredJson,
            machines: machines,
            sourceCompilation: sourceCompilation,
            sourceMap: sourceMap,
            sourcePath: sourcePath
        ) && ValidateWorld(
            catalogFingerprint: catalogFingerprint,
            diagnostics: diagnostics,
            loweredJson: composed,
            machines: machines,
            source: loweredJson,
            sourceMap: sourceMap,
            sourcePath: sourcePath
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

        if (!PuckDocumentComposer.TryComposeWorldDocument(
            catalog: machines,
            catalogFingerprint: catalogFingerprint,
            chainBytes: out _,
            composed: out var chain,
            origins: origins,
            reason: out var composeReason,
            rootBytes: rootBytes,
            sourceCompilation: sourceCompilation,
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
    /// <param name="sourcePath">The root document's path, when known, so referenced assets resolve beside it.</param>
    /// <param name="origins">The authored origins <see cref="TryComposeWorld"/> collected for
    /// <paramref name="loweredJson"/>, so a finding against a row only a basis or an import authors is placed on that
    /// source's line, and dropped when that source acknowledges it with an <c>allow</c> line; <see langword="null"/>
    /// reports such a finding without a location.</param>
    /// <returns>True if the world passed semantic validation without errors.</returns>
    public static bool ValidateWorld(JsonObject loweredJson, SourceMap? sourceMap, DiagnosticBag diagnostics, IMachineValidationCatalog? machines = null, string catalogFingerprint = "", JsonObject? source = null, string? sourcePath = null, WorldDocumentOrigins? origins = null) {
        ArgumentNullException.ThrowIfNull(loweredJson);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var jsonString = loweredJson.ToJsonString();

        if (!WorldDefinitionFileSource.TryParseDocument(
            definition: out var definition,
            json: jsonString,
            reason: out var parseReason,
            sourceName: "document"
        )) {
            var (path, refusal) = AuthoredRefusal(document: loweredJson, reason: parseReason);

            Report(diagnostics: diagnostics, finding: new WorldFindingLocator(composed: loweredJson, origins: origins, source: source, sourceMap: sourceMap, sourcePath: sourcePath).Locate(
                code: PuckDiagnosticCodes.SchemaRejected,
                message: ((path is null) ? refusal : $"{path}: {refusal}"),
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

        var locator = new WorldFindingLocator(composed: loweredJson, origins: origins, source: source, sourceMap: sourceMap, sourcePath: sourcePath);

        foreach (var error in errors) {
            Report(diagnostics: diagnostics, finding: locator.Locate(
                code: (undeclaredEnums.Contains(item: error)
                    ? PuckDiagnosticCodes.StateEnumUndeclared
                    : PuckDiagnosticCodes.SemanticValidation),
                message: error,
                severity: DiagnosticSeverity.Error
            ));
        }

        // A check the validator deferred to the host that runs the world is a notice, never a finding against the
        // author: a host without that catalog cannot answer it either way.
        foreach (var notice in deferred) {
            Report(diagnostics: diagnostics, finding: locator.Locate(code: PuckDiagnosticCodes.SemanticValidationDeferred, message: notice, severity: DiagnosticSeverity.Information));
        }

        foreach (var warning in MovingBlends(definition: definition)) {
            Report(diagnostics: diagnostics, finding: locator.Locate(code: PuckDiagnosticCodes.MovingBlend, message: warning, severity: DiagnosticSeverity.Warning));
        }

        return (errors.Count == 0);
    }
    private static readonly Regex PathInReason = new(pattern: @"\s*Path: \$\.?(?<path>[^\s|]*)(?:\s*\|[^.]*\.)?", options: RegexOptions.CultureInvariant);
    private static readonly Regex ConvertedTypeInReason = new(pattern: @"could not be converted to (?<type>\S+?)\.?$", options: RegexOptions.CultureInvariant);

    // The deserializer's refusal in the author's words: the document path it names (`documentId`, `shapes[1].blend`),
    // which the locator places on its authored line, and what the schema expected against what the document holds,
    // with neither a CLR type name nor the payload dump the reason carries.
    private static (string? Path, string Refusal) AuthoredRefusal(JsonObject document, string reason) {
        var dump = reason.IndexOf(value: " (payload: ", comparisonType: StringComparison.Ordinal);
        var message = ((dump < 0) ? reason : reason[..dump]);
        var pathMatch = PathInReason.Match(input: message);

        if (!pathMatch.Success) {
            return (null, message);
        }

        var path = pathMatch.Groups["path"].Value;
        var cause = message[..pathMatch.Index];
        var typeMatch = ConvertedTypeInReason.Match(input: cause);

        if (!typeMatch.Success) {
            var lead = cause.IndexOf(value: "document: ", comparisonType: StringComparison.Ordinal);

            return ((path.Length == 0) ? null : path, ((lead < 0) ? cause : cause[(lead + "document: ".Length)..]));
        }

        var expected = typeMatch.Groups["type"].Value switch {
            "System.String" => "text",
            "System.Boolean" => "true or false",
            "System.Single" or "System.Double" or "System.Decimal" or "System.Half" => "a number",
            "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" => "a whole number",
            var type when (type.Contains(value: "List`1", comparisonType: StringComparison.Ordinal) || type.EndsWith(value: "[]", comparisonType: StringComparison.Ordinal)) => "a list",
            _ => "a different kind of value",
        };
        var pointer = WorldDocumentPointers.ToJsonPointer(path: path, table: WorldConstructs.Table);
        var held = (((pointer.Length > 1) ? WorldSourceEdits.At(pointer: pointer, root: document) : null) switch {
            JsonArray => "a list",
            JsonObject => "a block",
            JsonValue value when (value.GetValueKind() == JsonValueKind.String) => "text",
            JsonValue value when (value.GetValueKind() is JsonValueKind.True or JsonValueKind.False) => "true or false",
            JsonValue => "a number",
            _ => null,
        });

        return ((path.Length == 0) ? null : path, ((held is null) ? $"expected {expected}" : $"expected {expected}, found {held}"));
    }
    private static void Report(DiagnosticBag diagnostics, Diagnostic? finding) {
        if (finding is not null) {
            diagnostics.Report(diagnostic: finding);
        }
    }
    /// <summary>Returns a warning for each blend of an animated creation's pooled groups that joins shapes moving
    /// independently (<see cref="CreationMotion.MovingBlends"/>), naming the creation, the two shapes, what moves each
    /// and the blend, its path first as a refusal's is.</summary>
    /// <param name="definition">The world.</param>
    /// <returns>The warnings, in prototype and shape order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<string> MovingBlends(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var warnings = new List<string>();
        var creations = definition.Creations;

        for (var index = 0; (index < creations.Count); index++) {
            if (creations[index]?.Document is not { } document) { continue; }
            foreach (var blend in CreationMotion.MovingBlends(document: document)) {
                warnings.Add(item: $"prototypes[{index}].doc.shapes[{blend.Shape}].blend: creation '{creations[index].Id}' {CreationMotion.Describe(blend: blend, document: document)}");
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
