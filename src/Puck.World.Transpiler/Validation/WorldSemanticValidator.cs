using System.Text.Json.Nodes;
using Puck.World.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Validation;

/// <summary>Validates lowered world definitions against engine semantic rules and maps errors to source spans.</summary>
public static class WorldSemanticValidator {
    /// <summary>Validates a lowered world definition JsonObject using Puck.World.Schema's engine validator.</summary>
    /// <param name="loweredJson">The lowered JsonObject.</param>
    /// <param name="sourceMap">The SourceMap linking JSON pointer paths to source AST spans.</param>
    /// <param name="diagnostics">The DiagnosticBag to report semantic errors into.</param>
    /// <returns>True if the world passed semantic validation without errors.</returns>
    public static bool ValidateWorld(JsonObject loweredJson, SourceMap? sourceMap, DiagnosticBag diagnostics) {
        ArgumentNullException.ThrowIfNull(loweredJson);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var jsonString = loweredJson.ToJsonString();
        if (!WorldDefinitionFileSource.TryParseDocument(jsonString, "document", out var definition, out var parseReason)) {
            diagnostics.ReportError("PUCK031", $"Document structure rejected by engine schema: {parseReason}", SourceSpan.None);
            return false;
        }

        if (definition is null) {
            diagnostics.ReportError("PUCK032", "Failed to deserialize lowered world definition for validation.", SourceSpan.None);
            return false;
        }

        var errors = new List<string>();
        WorldDefinitionValidator.TryValidateLocally(definition, errors, out _);

        foreach (var error in errors) {
            var span = ExtractSpanFromError(error, sourceMap);
            diagnostics.ReportError("PUCK030", error, span);
        }

        return errors.Count == 0;
    }

    private static SourceSpan ExtractSpanFromError(string error, SourceMap? sourceMap) {
        if (sourceMap is null) {
            return SourceSpan.None;
        }

        // Error strings frequently begin with path prefix like "views.layouts[0].slots[0].study: ..." or "screens[0].frame"
        var colonIdx = error.IndexOf(':');
        var pathToken = colonIdx > 0 ? error[..colonIdx].Trim() : error.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        // Convert dot notation "views.layouts[0]" to JSON pointer "/views/layouts/0"
        var jsonPointer = ConvertTojsonPointer(pathToken);
        if (sourceMap.TryGetSpan(jsonPointer, out var span)) {
            return span;
        }

        return SourceSpan.None;
    }

    private static string ConvertTojsonPointer(string path) {
        if (string.IsNullOrEmpty(path)) {
            return "";
        }

        // Replace '[0]' with '/0' and '.' with '/'
        var sb = new System.Text.StringBuilder();
        sb.Append('/');
        foreach (var c in path) {
            if (c == '.') {
                sb.Append('/');
            } else if (c == '[') {
                sb.Append('/');
            } else if (c != ']') {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
