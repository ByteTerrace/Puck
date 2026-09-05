using System.Text.Json;

using Puck.Assets.Documents;

namespace Puck.World.Authoring;

/// <summary>
/// The <c>puck.table.v1</c> document — a static lookup table a world references by a <c>tables</c> row and a rule
/// reads through <c>$table:&lt;name&gt;:&lt;key&gt;</c>. Never simulation state: nothing writes it, it is not hashed
/// into the tick, and its size is bounded only by the file. Keys are integers; values are read in
/// <see cref="Kind"/>.
/// </summary>
/// <param name="Schema">The document version tag (<c>puck.table.v1</c>).</param>
/// <param name="Kind">The value kind, <c>int</c> or <c>fixed</c>.</param>
/// <param name="Entries">The entries; keys are unique.</param>
/// <param name="Columns">The column names when every entry carries <see cref="TableEntryDocument.Values"/>, one per
/// column; absent for a single-value table whose entries carry <see cref="TableEntryDocument.Value"/>.</param>
public sealed record TableDocument(
    string? Schema,
    string Kind,
    IReadOnlyList<TableEntryDocument> Entries,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Columns = null
) {
    /// <summary>The version tag every saved document carries.</summary>
    public const string CurrentSchema = "puck.table.v1";
    /// <summary>The integer value kind.</summary>
    public const string IntKind = "int";
    /// <summary>The fixed-point value kind.</summary>
    public const string FixedKind = "fixed";
    /// <summary>Unknown members preserved across a round-trip. Null when the document carries no unknown members.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }
}

/// <summary>One table entry.</summary>
/// <param name="Key">The integer key.</param>
/// <param name="Value">The value of a single-value table, an integer for an <c>int</c> table or an exact decimal for
/// a <c>fixed</c> one.</param>
/// <param name="Values">The values of a column table, one per declared column, in column order.</param>
public sealed record TableEntryDocument(
    long Key,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? Value = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<decimal>? Values = null
);

/// <summary>The strict validate → normalize → canonicalize boundary every <see cref="TableDocument"/> crosses before
/// it is trusted: schema tag, value kind, unique keys, and a value representable in the declared kind.</summary>
public static class TableCanonicalizer {
    private static readonly HashSet<string> KnownMemberNames = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "schema", "kind", "entries", "columns",
    };

    /// <summary>Validates a document's schema and structural invariants in one pass.</summary>
    /// <param name="document">The document to validate, as deserialized.</param>
    /// <returns>Every violation found; empty when the document is a valid <c>puck.table.v1</c> value.</returns>
    public static IReadOnlyList<DocumentValidationError> Validate(TableDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        if (DocumentCanonicalizer.SchemaViolationMessage(declared: document.Schema, recognized: TableDocument.CurrentSchema) is { } schemaViolation) {
            return [new DocumentValidationError(Message: schemaViolation, Path: "schema")];
        }
        var errors = new List<DocumentValidationError>();
        var isFixed = string.Equals(a: document.Kind, b: TableDocument.FixedKind, comparisonType: StringComparison.Ordinal);
        if (!isFixed && !string.Equals(a: document.Kind, b: TableDocument.IntKind, comparisonType: StringComparison.Ordinal)) {
            errors.Add(item: new(Message: $"'{document.Kind}' is not '{TableDocument.IntKind}' or '{TableDocument.FixedKind}'.", Path: "kind"));
        }
        var columns = (document.Columns ?? []);
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        for (var index = 0; index < columns.Count; index++) {
            if (string.IsNullOrWhiteSpace(value: columns[index])) {
                errors.Add(item: new(Message: "a column name is required.", Path: $"columns[{index}]"));
            } else if (!names.Add(item: columns[index])) {
                errors.Add(item: new(Message: $"column '{columns[index]}' is declared twice.", Path: $"columns[{index}]"));
            }
        }
        if (document.Entries is not { Count: > 0 } entries) {
            errors.Add(item: new(Message: "at least one entry is required.", Path: "entries"));
        } else {
            var keys = new HashSet<long>(capacity: entries.Count);
            for (var index = 0; index < entries.Count; index++) {
                var entry = entries[index];
                if (entry is null) {
                    errors.Add(item: new(Message: "an entry is required.", Path: $"entries[{index}]"));
                    continue;
                }
                if (!keys.Add(item: entry.Key)) {
                    errors.Add(item: new(Message: $"key {entry.Key} is declared twice.", Path: $"entries[{index}].key"));
                }
                if (columns.Count == 0) {
                    if (entry.Value is not { } value) {
                        errors.Add(item: new(Message: "a single-value table entry carries 'value'.", Path: $"entries[{index}].value"));
                    } else if (!isFixed && (value != decimal.Truncate(d: value))) {
                        errors.Add(item: new(Message: $"{value} is not an integer.", Path: $"entries[{index}].value"));
                    }
                    if (entry.Values is not null) {
                        errors.Add(item: new(Message: "a table without columns carries no 'values'.", Path: $"entries[{index}].values"));
                    }
                } else {
                    if (entry.Value is not null) {
                        errors.Add(item: new(Message: "a column table entry carries 'values', not 'value'.", Path: $"entries[{index}].value"));
                    }
                    if (entry.Values is not { } values || values.Count != columns.Count) {
                        errors.Add(item: new(Message: $"expected {columns.Count} values, one per column.", Path: $"entries[{index}].values"));
                    } else if (!isFixed) {
                        for (var column = 0; column < values.Count; column++) {
                            if (values[column] != decimal.Truncate(d: values[column])) {
                                errors.Add(item: new(Message: $"{values[column]} is not an integer.", Path: $"entries[{index}].values[{column}]"));
                            }
                        }
                    }
                }
            }
        }
        DocumentCanonicalizer.ValidateExtensions(
            extensions: document.Extensions,
            knownMemberNames: KnownMemberNames,
            addError: (path, message) => errors.Add(item: new(Message: message, Path: path))
        );
        return errors;
    }

    /// <summary>Validates, throwing a formatted exception on the first violation set.</summary>
    /// <param name="document">The document to validate.</param>
    /// <param name="source">An optional source label for the failure message.</param>
    public static void ValidateOrThrow(TableDocument document, string? source = null) =>
        DocumentCanonicalizer.ThrowIfInvalid(errors: Validate(document: document), source: source);

    /// <summary>Normalizes an already-valid document: stamps the schema tag and sorts entries by key. Idempotent.</summary>
    /// <param name="document">The document to normalize.</param>
    /// <returns>The normalized document.</returns>
    public static TableDocument Normalize(TableDocument document) {
        ArgumentNullException.ThrowIfNull(document);
        return (document with {
            Schema = TableDocument.CurrentSchema,
            Entries = [.. document.Entries.OrderBy(keySelector: static entry => entry.Key)],
        });
    }

    /// <summary>Validates (throwing on failure), normalizes, then serializes to canonical UTF-8 bytes and hashes them.</summary>
    /// <param name="document">The document to canonicalize.</param>
    /// <param name="source">An optional source label for a validation-failure message.</param>
    /// <returns>The validated, normalized document plus its canonical bytes and hash.</returns>
    public static CanonicalDocument<TableDocument> Canonicalize(TableDocument document, string? source = null) {
        ValidateOrThrow(document: document, source: source);
        return DocumentCanonicalizer.Canonicalize(document: Normalize(document: document));
    }
}
