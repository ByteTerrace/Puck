using System.Text.Json;
using Puck.Assets.Documents;

namespace Puck.Forge.Authoring;

/// <summary>One named hit-window: a grade and how many ticks off the nearest beat still earns it.</summary>
/// <param name="Grade">The window's name (e.g. "perfect", "good"), unique within the document.</param>
/// <param name="ToleranceTicks">The non-negative tick distance from the nearest beat this window still admits.</param>
public sealed record JudgeWindowDocument(string Grade, long ToleranceTicks);
/// <summary>
/// The <c>puck.judge.v1</c> document — a named set of rhythm hit-windows, in engine ticks against a musical clock's
/// beat spacing. Referenced by any action lane or interaction opting into rhythm judgment; never a section every
/// world carries.
/// </summary>
/// <param name="Schema">The document version tag (<c>puck.judge.v1</c>).</param>
/// <param name="Name">The window set's display name (null = "windows").</param>
/// <param name="Windows">The declared windows, at least one, evaluated tightest-tolerance first for the usual
/// "perfect beats good" grading.</param>
public sealed record JudgeDocument(
    string? Schema,
    string? Name,
    IReadOnlyList<JudgeWindowDocument> Windows
) {
    /// <summary>The version tag every saved document carries.</summary>
    public const string CurrentSchema = "puck.judge.v1";

    /// <summary>Unknown members preserved across a round-trip. Null when the document carries no unknown members.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}
/// <summary>
/// THE strict validate → normalize → canonicalize boundary every <see cref="JudgeDocument"/> crosses before it is
/// trusted, persisted, or embedded — mirrors <see cref="SynthPatchCanonicalizer"/>'s shape.
/// </summary>
public static class JudgeCanonicalizer {
    private static readonly HashSet<string> KnownMemberNames = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "schema", "name", "windows",
    };

    /// <summary>Validates a document's schema and structural invariants in one pass — every violation is collected
    /// rather than throwing on the first.</summary>
    /// <param name="document">The document to validate, as deserialized — not yet normalized.</param>
    /// <returns>Every violation found; empty when the document is a valid <c>puck.judge.v1</c> value.</returns>
    public static IReadOnlyList<DocumentValidationError> Validate(JudgeDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (DocumentCanonicalizer.SchemaViolationMessage(declared: document.Schema, recognized: JudgeDocument.CurrentSchema) is { } schemaViolation) {
            return [new DocumentValidationError(Message: schemaViolation, Path: "schema")];
        }

        var errors = new List<DocumentValidationError>();

        if (document.Windows is not { Count: > 0 } windows) {
            errors.Add(item: new(Message: "at least one window is required.", Path: "windows"));
        } else {
            var grades = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var index = 0; (index < windows.Count); index++) {
                var window = windows[index];
                var path = $"windows[{index}]";

                if (window is null) {
                    errors.Add(item: new(Message: "is required.", Path: path));

                    continue;
                }

                if (string.IsNullOrWhiteSpace(value: window.Grade)) {
                    errors.Add(item: new(Message: "grade is required.", Path: $"{path}.grade"));
                } else if (!grades.Add(item: window.Grade)) {
                    errors.Add(item: new(Message: $"'{window.Grade}' is duplicated.", Path: $"{path}.grade"));
                }

                if (window.ToleranceTicks < 0L) {
                    errors.Add(item: new(Message: $"{window.ToleranceTicks} must not be negative.", Path: $"{path}.toleranceTicks"));
                }
            }
        }

        DocumentCanonicalizer.ValidateExtensions(
            addError: (path, message) => errors.Add(item: new(Message: message, Path: path)),
            extensions: document.Extensions,
            knownMemberNames: KnownMemberNames
        );

        return errors;
    }
    /// <summary>Runs <see cref="Validate"/> and throws when it finds anything.</summary>
    /// <param name="document">The document to validate.</param>
    /// <param name="source">An optional source label (a file path or asset id) for the exception message.</param>
    /// <exception cref="DocumentValidationException">The document declares an absent/foreign schema, or fails a
    /// structural invariant.</exception>
    public static void ValidateOrThrow(JudgeDocument document, string? source = null) =>
        DocumentCanonicalizer.ThrowIfInvalid(errors: Validate(document: document), source: source);
    /// <summary>Normalizes an already-schema-valid document: defaults every optional member. Idempotent. Does NOT
    /// itself validate; <see cref="Canonicalize"/> always crosses <see cref="ValidateOrThrow"/> first.</summary>
    /// <param name="document">The document to normalize.</param>
    /// <returns>The normalized document.</returns>
    public static JudgeDocument Normalize(JudgeDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return (document with {
            Name = (string.IsNullOrWhiteSpace(value: document.Name) ? "windows" : document.Name.Trim()),
            Schema = JudgeDocument.CurrentSchema,
        });
    }
    /// <summary>THE full pipeline: validates (throwing on failure), normalizes, then serializes to canonical UTF-8
    /// bytes and hashes them.</summary>
    /// <param name="document">The document to canonicalize.</param>
    /// <param name="source">An optional source label for a validation-failure message.</param>
    /// <returns>The validated, normalized document plus its canonical bytes and hash.</returns>
    public static CanonicalDocument<JudgeDocument> Canonicalize(JudgeDocument document, string? source = null) {
        ValidateOrThrow(document: document, source: source);

        return DocumentCanonicalizer.Canonicalize(document: Normalize(document: document));
    }
}
