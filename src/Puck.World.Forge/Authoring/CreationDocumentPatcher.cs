using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Assets.Documents;

namespace Puck.Forge.Authoring;

/// <summary>
/// The ONE generic document-member-path walker a <c>puck.creation.v1</c> document is edited through — the
/// creation-scoped twin of <c>world.row.set</c>/<c>world.row.remove</c> (<c>Puck.World.WorldRowCommandModule</c>),
/// except addressed by a full dotted-plus-bracket JSON PATH rather than a fixed section table, since a creation's
/// shapes/frames/chains/palette are themselves addressable arrays. Round-trips the document through
/// <see cref="JsonSerializer"/>'s node tree (<see cref="DocumentJsonOptions.Shared"/> — the same camelCase,
/// strict shape every document uses, vectors included as arrays) so a new <see cref="CreationDocument"/> member is
/// path-editable the day it exists, with zero editor code.
/// </summary>
/// <remarks>
/// <para><b>Path grammar</b>: dot-separated segments, each an optional trailing <c>[n]</c> array index —
/// <c>shapes[3].scale</c>, <c>palette[0].color</c>, <c>name</c>, <c>textRuns[0].text</c>. The index is the array's
/// POSITION (document order), never a row's own <c>id</c>/<c>name</c> — a caller resolving "the shape with id 7"
/// first finds its current position.</para>
/// <para><b>Bare list paths</b> (no trailing index, e.g. <c>shapes</c>): a JSON ARRAY payload replaces the whole
/// list; a JSON OBJECT payload upserts one row — by <c>id</c> for <c>shapes</c>/<c>chains</c>, by <c>name</c> for
/// <c>frames</c>, else appended (no natural key: <c>textRuns</c>, <c>cameras</c>, <c>parts</c>, <c>palette</c>'s
/// individual slots, which are addressed by index instead).</para>
/// <para><b>Removal</b> always addresses one array element by index — <c>shapes[3]</c> — never a bare list (which
/// row would that remove?) and never a scalar field (nothing to remove a name TO).</para>
/// <para>This type performs NO document-level validation of its own — same doctrine as <c>world.row.set</c>: a
/// malformed path or JSON payload refuses inline; the caller (<see cref="SculptModel"/>) runs
/// <see cref="CreationCanonicalizer.Validate"/> on the candidate result before accepting it.</para>
/// <para>A payload that is not valid JSON on its own retries once as a JSON string literal
/// (<see cref="TryParseJsonLenient"/>) — the console's own argument parser strips the surrounding quotes off a
/// WHOLE-token quoted argument (<c>"#FF00FF"</c> arrives as <c>#FF00FF</c>), so a bare word is otherwise
/// indistinguishable from an intentional JSON string for a string-typed field.</para>
/// </remarks>
public static class CreationDocumentPatcher {
    /// <summary>One patch attempt's outcome: the candidate document on success, or a refusal message.</summary>
    /// <param name="Document">The patched (not yet validated) document, when <see cref="Error"/> is null.</param>
    /// <param name="Error">The refusal message, when the patch itself (path/JSON) failed.</param>
    public readonly record struct PatchResult(CreationDocument? Document, string? Error) {
        /// <summary>Whether the patch itself succeeded (document validation is the caller's separate concern).</summary>
        public bool Ok => (Error is null);
    }

    private readonly record struct Segment(string Name, int? Index);

    // The small keyed-upsert table for bare-list SET payloads that are a JSON OBJECT: which property identifies an
    // existing row to replace rather than append. Anything absent here (textRuns, cameras, parts, palette) has no
    // natural key, so a bare-list object payload always appends.
    private static readonly IReadOnlyDictionary<string, string> s_upsertKeyProperty = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
        ["shapes"] = "id",
        ["chains"] = "id",
        ["frames"] = "name",
    };

    /// <summary>Removes one array element by index at <paramref name="path"/> (e.g. <c>shapes[3]</c>).</summary>
    /// <param name="document">The document to patch (unmodified on failure).</param>
    /// <param name="path">The element's path — MUST end in a <c>[n]</c> index.</param>
    /// <returns>The patched candidate, or a refusal message.</returns>
    public static PatchResult TryRemove(CreationDocument document, string path) {
        ArgumentNullException.ThrowIfNull(document);

        if (!TryParsePath(
            error: out var pathError,
            path: path,
            segments: out var segments
        )) {
            return new PatchResult(Document: null, Error: pathError);
        }

        var last = segments[^1];

        if (last.Index is not { } index) {
            return new PatchResult(Document: null, Error: $"'{path}': remove needs an array index — e.g. shapes[3], not a bare list or scalar field");
        }

        var root = ToNode(document: document);

        if (!TryNavigate(
            container: out var container,
            error: out var navError,
            key: out _,
            path: path,
            root: root,
            segments: segments.AsSpan(start: 0, length: (segments.Length - 1)),
            trailingIndex: last.Name
        )) {
            return new PatchResult(Document: null, Error: navError);
        }

        if (container is not JsonArray array) {
            return new PatchResult(Document: null, Error: $"'{path}': '{last.Name}' is not a list");
        }

        if ((index < 0) || (index >= array.Count)) {
            return new PatchResult(Document: null, Error: $"'{path}': index {index} out of range (0..{(array.Count - 1)})");
        }

        array.RemoveAt(index: index);

        return FromNode(
            path: path,
            root: root
        );
    }
    /// <summary>Sets a field or upserts a row at <paramref name="path"/> to <paramref name="json"/>.</summary>
    /// <param name="document">The document to patch (unmodified on failure).</param>
    /// <param name="path">The target path — a scalar field (<c>name</c>), an array element
    /// (<c>shapes[3].scale</c>, whole-element when the path ends exactly at the index), or a bare list
    /// (<c>shapes</c>, <c>palette</c>) for a whole-list replace (JSON array payload) or upsert/append (JSON object
    /// payload).</param>
    /// <param name="json">The payload, in the document's own wire shape (<c>puck schema</c>).</param>
    /// <returns>The patched candidate, or a refusal message.</returns>
    public static PatchResult TrySet(CreationDocument document, string path, string json) {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrWhiteSpace(value: json)) {
            return new PatchResult(Document: null, Error: $"'{path}': expected a JSON payload");
        }

        if (!TryParseJsonLenient(
            error: out var jsonError,
            json: json,
            payload: out var payload
        )) {
            return new PatchResult(Document: null, Error: $"'{path}': {jsonError}");
        }

        if (!TryParsePath(
            error: out var pathError,
            path: path,
            segments: out var segments
        )) {
            return new PatchResult(Document: null, Error: pathError);
        }

        var last = segments[^1];
        var root = ToNode(document: document);

        if (!TryNavigate(
            container: out var container,
            error: out var navError,
            key: out _,
            path: path,
            root: root,
            segments: segments.AsSpan(start: 0, length: (segments.Length - 1)),
            trailingIndex: last.Name
        )) {
            return new PatchResult(Document: null, Error: navError);
        }

        if (last.Index is { } index) {
            if (container is not JsonArray array) {
                return new PatchResult(Document: null, Error: $"'{path}': '{last.Name}' is not a list");
            }

            if ((index < 0) || (index >= array.Count)) {
                return new PatchResult(Document: null, Error: $"'{path}': index {index} out of range (0..{(array.Count - 1)})");
            }

            array[index] = payload;
        } else {
            if (container is not JsonObject obj) {
                return new PatchResult(Document: null, Error: $"'{path}': not an object");
            }

            var existing = obj[last.Name];

            if ((existing is JsonArray list) && (payload is JsonObject row)) {
                UpsertRow(
                    array: list,
                    listName: last.Name,
                    row: row
                );
            } else {
                obj[last.Name] = payload;
            }
        }

        return FromNode(
            path: path,
            root: root
        );
    }
    // Deserializes back to a CreationDocument, surfacing a JsonException as a refusal (the "does not fit the
    // document's own shape" case — an unmapped member, a wrong-typed value, a missing required member).
    private static PatchResult FromNode(JsonNode root, string path) {
        try {
            var candidate = JsonSerializer.Deserialize<CreationDocument>(
                node: root,
                options: DocumentJsonOptions.Shared
            );

            return ((candidate is null)
                ? new PatchResult(Document: null, Error: $"'{path}': patched document parsed to null")
                : new PatchResult(Document: candidate, Error: null)
            );
        } catch (JsonException exception) {
            return new PatchResult(Document: null, Error: $"'{path}': {exception.Message.ReplaceLineEndings(replacementText: " ")}");
        }
    }
    private static JsonNode ToNode(CreationDocument document) =>
        (JsonSerializer.SerializeToNode(
            value: document,
            options: DocumentJsonOptions.Shared
        ) ?? throw new InvalidOperationException(message: "a CreationDocument serialized to a null node"));
    // Walks every segment EXCEPT the trailing one's own key/index — leaving `container` as the JsonObject/JsonArray
    // the final assignment (or removal) applies to, and `key` as the final segment's own name (for callers that
    // want it; `trailingIndex`'s NAME is threaded through so error messages can name the final segment even though
    // this walk stops one short of consuming it).
    private static bool TryNavigate(string path, JsonNode root, ReadOnlySpan<Segment> segments, string trailingIndex, out JsonNode? container, out string? key, out string? error) {
        var current = ((JsonNode?)root);
        key = trailingIndex;

        foreach (var segment in segments) {
            if (current is not JsonObject obj) {
                container = null;
                error = $"'{path}': '{segment.Name}' has no parent object to walk into";

                return false;
            }

            if (!obj.TryGetPropertyValue(
                propertyName: segment.Name,
                jsonNode: out var next
            ) || (next is null)) {
                container = null;
                error = $"'{path}': unknown or empty member '{segment.Name}'";

                return false;
            }

            if (segment.Index is { } index) {
                if (next is not JsonArray array) {
                    container = null;
                    error = $"'{path}': '{segment.Name}' is not a list";

                    return false;
                }

                if ((index < 0) || (index >= array.Count)) {
                    container = null;
                    error = $"'{path}': '{segment.Name}[{index}]' out of range (0..{(array.Count - 1)})";

                    return false;
                }

                current = array[index];
            } else {
                current = next;
            }
        }

        container = current;
        error = null;

        return true;
    }
    /// <summary>Parses a payload as JSON, retrying once as a JSON string literal when it is not valid JSON on its
    /// own — the console's argument parser strips the surrounding quotes off a whole-token quoted argument
    /// (<c>"#FF00FF"</c> arrives as <c>#FF00FF</c>), so a bare word is otherwise indistinguishable from an
    /// intentional JSON string. Shared by <see cref="SculptModel"/>'s brush-field patch, which does not route
    /// through <see cref="TrySet"/>.</summary>
    /// <param name="json">The raw payload text.</param>
    /// <param name="payload">The parsed node on success.</param>
    /// <param name="error">The refusal message on failure.</param>
    /// <returns>Whether either parse attempt succeeded.</returns>
    public static bool TryParseJsonLenient(string json, out JsonNode? payload, out string? error) {
        try {
            payload = JsonNode.Parse(json: json);
            error = null;

            return true;
        } catch (JsonException) {
            try {
                payload = JsonNode.Parse(json: JsonSerializer.Serialize(value: json));
                error = null;

                return true;
            } catch (JsonException exception) {
                payload = null;
                error = exception.Message.ReplaceLineEndings(replacementText: " ");

                return false;
            }
        }
    }
    // Splits "shapes[3].scale" into [(shapes,3), (scale,null)]; refuses malformed brackets/empty segments by name.
    private static bool TryParsePath(string path, out Segment[] segments, out string? error) {
        if (string.IsNullOrWhiteSpace(value: path)) {
            segments = [];
            error = "empty path";

            return false;
        }

        var tokens = path.Split(separator: '.');
        var result = new Segment[tokens.Length];

        for (var i = 0; (i < tokens.Length); i++) {
            var token = tokens[i];

            if (token.Length == 0) {
                segments = [];
                error = $"'{path}': empty path segment";

                return false;
            }

            var bracket = token.IndexOf(value: '[');

            if (bracket < 0) {
                result[i] = new Segment(Name: token, Index: null);

                continue;
            }

            if (
                !token.EndsWith(value: ']') ||
                !int.TryParse(
                    s: token[(bracket + 1)..^1],
                    result: out var index
                ) ||
                (index < 0)
            ) {
                segments = [];
                error = $"'{path}': malformed index in '{token}' — expected name[n] with n >= 0";

                return false;
            }

            result[i] = new Segment(Name: token[..bracket], Index: index);
        }

        segments = result;
        error = null;

        return true;
    }
    // The bare-list SET-with-object-payload rule: replace an existing row sharing the list's key property's value,
    // else append. Lists with no key property in s_upsertKeyProperty always append (textRuns, cameras, parts).
    private static void UpsertRow(JsonArray array, string listName, JsonObject row) {
        if (
            s_upsertKeyProperty.TryGetValue(
                key: listName,
                value: out var keyProperty
            ) &&
            row.TryGetPropertyValue(
                propertyName: keyProperty,
                jsonNode: out var keyValue
            ) &&
            (keyValue is not null)
        ) {
            for (var i = 0; (i < array.Count); i++) {
                if (
                    (array[i] is JsonObject existing) &&
                    existing.TryGetPropertyValue(
                        propertyName: keyProperty,
                        jsonNode: out var existingKey
                    ) &&
                    JsonNode.DeepEquals(
                        node1: existingKey,
                        node2: keyValue
                    )
                ) {
                    array[i] = row;

                    return;
                }
            }
        }

        array.Add(value: row);
    }
}
