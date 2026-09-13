using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Composition;
using Puck.Transpiler.Diagnostics;
using Puck.Abstractions.Machines;

namespace Puck.World.Transpiler.Validation;

/// <summary>Validates lowered world definitions against engine semantic rules and maps errors to source spans.</summary>
public static class WorldSemanticValidator {
    /// <summary>The schema identifier a document declares to state that it is a whole world rather than a
    /// fragment.</summary>
    public const string RootSchemaId = "puck.world.definition.v1";

    private static string ConvertTojsonPointer(string path) {
        if (string.IsNullOrEmpty(value: path)) {
            return "";
        }

        // Replace '[0]' with '/0' and '.' with '/'
        var sb = new System.Text.StringBuilder();

        sb.Append(value: '/');
        foreach (var c in path) {
            if (c == '.') {
                sb.Append(value: '/');
            } else if (c == '[') {
                sb.Append(value: '/');
            } else if (c != ']') {
                sb.Append(value: c);
            }
        }
        return sb.ToString();
    }
    private static SourceSpan ExtractSpanFromError(string error, SourceMap? sourceMap) {
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

        // Convert dot notation "views.layouts[0]" to JSON pointer "/views/layouts/0"
        var jsonPointer = ConvertTojsonPointer(path: pathToken);

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
    public static bool ValidateComposedWorld(JsonObject loweredJson, SourceMap? sourceMap, DiagnosticBag diagnostics, string sourcePath, IMachineValidationCatalog? machines = null, string catalogFingerprint = "") {
        ArgumentNullException.ThrowIfNull(loweredJson);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var rootBytes = Encoding.UTF8.GetBytes(s: loweredJson.ToJsonString());

        if (!PuckDocumentComposer.TryComposeWorldDocument(
            catalog: machines,
            catalogFingerprint: catalogFingerprint,
            chainBytes: out _,
            composed: out var composed,
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

        return ValidateWorld(
            catalogFingerprint: catalogFingerprint,
            diagnostics: diagnostics,
            loweredJson: (composed ?? loweredJson),
            machines: machines,
            sourceMap: sourceMap
        );
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
    /// <returns>True if the world passed semantic validation without errors.</returns>
    public static bool ValidateWorld(JsonObject loweredJson, SourceMap? sourceMap, DiagnosticBag diagnostics, IMachineValidationCatalog? machines = null, string catalogFingerprint = "") {
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

        WorldDefinitionValidator.TryValidateLocally(
            definition,
            machines,
            errors,
            deferred: errors,
            out _
        );

        foreach (var error in errors) {
            var span = ExtractSpanFromError(
                error: error,
                sourceMap: sourceMap
            );

            diagnostics.ReportError(
                code: PuckDiagnosticCodes.SemanticValidation,
                message: error,
                span: span
            );
        }

        return (errors.Count == 0);
    }
}
