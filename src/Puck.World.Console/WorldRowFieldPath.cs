using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The shared field-path GRAMMAR and live-JSON-tree walk every document-row FIELD verb resolves through — one level
/// deeper than <see cref="WorldRowCommandModule"/>'s own section table, which resolves a whole ROW. A path is
/// dot-separated segments, each an optional trailing bracketed selector: a zero-based list INDEX (<c>shapes[3]</c>,
/// the grammar the (now-deleted) creation-document patcher used first) or a name/id-addressed SELECTOR
/// (<c>shapes[name=forearmL]</c>) that locates the one list element whose own member equals the given value — never
/// limited to <c>name</c>/<c>id</c>: any member a list's element type carries. <see cref="WorldRowFieldStepper"/>
/// (<c>world.row.step</c>'s numeric/boolean/enum DELTA) and <see cref="WorldRowCommandModule"/>'s literal
/// set/add/remove/read forms are the two consumers; this type owns the grammar and the tree walk so neither can
/// drift from the other. Every method mutates or reads <see cref="JsonNode"/> trees IN PLACE — <see cref="JsonObject"/>/
/// <see cref="JsonArray"/> containers hold live references, so a located leaf's container is set through directly.
/// </summary>
internal static class WorldRowFieldPath {
    /// <summary>One path segment: a member name, plus an optional trailing selector — a zero-based array INDEX, or a
    /// name/id-addressed <c>field=value</c> SELECTOR (mutually exclusive; a bare name carries neither).</summary>
    public readonly record struct Segment(string Name, int? Index, string? SelectorField, string? SelectorValue) {
        /// <summary>Whether this segment carries a trailing bracketed selector of either kind — the discriminator
        /// every consumer that treats "indexed" and "selected" identically (walk into the array, not the object)
        /// reads instead of testing both members separately.</summary>
        public bool HasBracket => ((Index is not null) || (SelectorField is not null));
    }

    /// <summary>Splits a dotted/bracketed path (<c>shapes[3].scale</c>, <c>shapes[name=forearmL].rounding</c>,
    /// <c>sharpness</c>) into its segments; refuses malformed brackets/empty segments by name — the same grammar
    /// <see cref="WorldRowFieldStepper"/> has always parsed, extended with the <c>[field=value]</c> selector
    /// arm.</summary>
    /// <param name="path">The dotted/indexed/selected field path.</param>
    /// <param name="segments">The parsed segments, on success.</param>
    /// <param name="error">The refusal reason, when the method returns <see langword="false"/>.</param>
    public static bool TryParse(string path, out Segment[] segments, out string? error) {
        if (string.IsNullOrWhiteSpace(value: path)) {
            segments = [];
            error = "empty path";

            return false;
        }

        var tokens = SplitSegments(path: path);
        var result = new Segment[tokens.Count];

        for (var i = 0; (i < tokens.Count); i++) {
            var token = tokens[i];

            if (token.Length == 0) {
                segments = [];
                error = $"'{path}': empty path segment";

                return false;
            }

            var bracket = token.IndexOf(value: '[');

            if (bracket < 0) {
                result[i] = new Segment(Index: null, Name: token, SelectorField: null, SelectorValue: null);

                continue;
            }

            if (
                !token.EndsWith(value: ']') ||
                (bracket == 0)
            ) {
                segments = [];
                error = $"'{path}': malformed bracket in '{token}' — expected name[n] or name[field=value]";

                return false;
            }

            var name = token[..bracket];
            var content = token[(bracket + 1)..^1];

            if (CommandArgs.TryParseInt(
                text: content,
                value: out var index
            )) {
                if (index < 0) {
                    segments = [];
                    error = $"'{path}': malformed index in '{token}' — expected name[n] with n >= 0, or name[field=value]";

                    return false;
                }

                result[i] = new Segment(Index: index, Name: name, SelectorField: null, SelectorValue: null);

                continue;
            }

            var equals = content.IndexOf(value: '=');

            if (
                (equals <= 0) ||
                (equals == (content.Length - 1))
            ) {
                segments = [];
                error = $"'{path}': malformed index in '{token}' — expected name[n] with n >= 0, or name[field=value]";

                return false;
            }

            result[i] = new Segment(Index: null, Name: name, SelectorField: content[..equals], SelectorValue: content[(equals + 1)..]);
        }

        segments = result;
        error = null;

        return true;
    }
    // Splits on '.' outside brackets only, so a selector value carrying a dot (`palette[specular=0.05]`, a dotted
    // name) stays inside its own segment; an unterminated '[' runs to the end of the path and fails the bracket
    // check above by name.
    private static List<string> SplitSegments(string path) {
        var tokens = new List<string>();
        var start = 0;
        var inBracket = false;

        for (var i = 0; (i < path.Length); i++) {
            switch (path[i]) {
                case '[':
                    inBracket = true;

                    break;
                case ']':
                    inBracket = false;

                    break;
                case '.' when !inBracket:
                    tokens.Add(item: path[start..i]);
                    start = (i + 1);

                    break;
            }
        }

        tokens.Add(item: path[start..]);

        return tokens;
    }
    /// <summary>Parses a STANDALONE selector token (<c>world.row.add</c>'s <c>after=&lt;selector&gt;</c>,
    /// <c>world.row.remove</c>'s list-element <c>&lt;selector&gt;</c>) — a bare non-negative index, or
    /// <c>field=value</c> — into the same <see cref="Segment"/> shape <see cref="TryResolveArrayElement"/> resolves,
    /// naming <paramref name="containerName"/> (the list field's own last path component) as the segment's
    /// <see cref="Segment.Name"/> for a refusal to quote.</summary>
    /// <param name="text">The selector text, without brackets.</param>
    /// <param name="containerName">The list field's own name, for a refusal message.</param>
    /// <param name="segment">The parsed selector, on success.</param>
    /// <param name="error">The refusal reason, when the method returns <see langword="false"/>.</param>
    public static bool TryParseSelector(string text, string containerName, out Segment segment, out string? error) {
        if (CommandArgs.TryParseInt(
            text: text,
            value: out var index
        )) {
            if (index < 0) {
                segment = default;
                error = $"'{text}': expected a non-negative index, or field=value";

                return false;
            }

            segment = new Segment(Index: index, Name: containerName, SelectorField: null, SelectorValue: null);
            error = null;

            return true;
        }

        var equals = text.IndexOf(value: '=');

        if (
            (equals <= 0) ||
            (equals == (text.Length - 1))
        ) {
            segment = default;
            error = $"'{text}': expected a non-negative index, or field=value";

            return false;
        }

        segment = new Segment(Index: null, Name: containerName, SelectorField: text[..equals], SelectorValue: text[(equals + 1)..]);
        error = null;

        return true;
    }
    /// <summary>Resolves ONE array element by <paramref name="segment"/>'s own index or selector — the routine every
    /// bracketed-segment consumer (navigation, a leaf lookup, <c>world.row.add</c>'s <c>after=</c>,
    /// <c>world.row.remove</c>'s list-element form) reduces to. A selector match is refused BY NAME when it names no
    /// element or more than one, listing every element's own candidate value for the named field — never a silent
    /// pick of the first or last match.</summary>
    /// <param name="array">The list to search.</param>
    /// <param name="segment">The index or selector to resolve.</param>
    /// <param name="path">The full field path, for a refusal message.</param>
    /// <param name="index">The resolved zero-based index, on success.</param>
    /// <param name="error">The refusal reason, when the method returns <see langword="false"/>.</param>
    public static bool TryResolveArrayElement(JsonArray array, Segment segment, string path, out int index, out string? error) {
        if (segment.Index is { } literalIndex) {
            if (((uint)literalIndex) >= ((uint)array.Count)) {
                index = -1;
                error = $"'{path}': '{segment.Name}[{literalIndex}]' out of range (0..{(array.Count - 1)})";

                return false;
            }

            index = literalIndex;
            error = null;

            return true;
        }

        var field = segment.SelectorField!;
        var wanted = segment.SelectorValue!;
        var matches = new List<int>();
        var candidates = new List<string>();

        for (var i = 0; (i < array.Count); i++) {
            if (array[i] is not JsonObject element) {
                continue;
            }

            if (
                !element.TryGetPropertyValue(
                    propertyName: field,
                    jsonNode: out var value
                ) ||
                (value is null)
            ) {
                continue;
            }

            candidates.Add(item: SelectorText(node: value));

            if (SelectorMatches(
                node: value,
                wanted: wanted
            )) {
                matches.Add(item: i);
            }
        }

        index = -1;

        if (matches.Count == 1) {
            index = matches[0];
            error = null;

            return true;
        }

        if (matches.Count == 0) {
            error = ((candidates.Count == 0)
                ? $"'{path}': no element of '{segment.Name}' carries a '{field}' member"
                : $"'{path}': no element of '{segment.Name}' has {field}={wanted} — candidates: {string.Join(separator: ", ", values: candidates)}"
            );
        } else {
            error = $"'{path}': {field}={wanted} is ambiguous — {matches.Count} elements of '{segment.Name}' match (indices {string.Join(separator: ", ", values: matches)})";
        }

        return false;
    }
    // A selector's comparand text, rendered from the element's own field VALUE — a string compares its literal text,
    // a number its compact JSON text, everything else (object/array/bool) its compact JSON — the same text a refusal
    // lists as a candidate, so the printed candidates are exactly what a follow-up selector should spell.
    private static string SelectorText(JsonNode node) => (node.GetValueKind() switch {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.True or JsonValueKind.False => (node.GetValue<bool>() ? "true" : "false"),
        _ => node.ToJsonString(),
    });
    private static bool SelectorMatches(JsonNode node, string wanted) => (node.GetValueKind() switch {
        JsonValueKind.String => string.Equals(a: node.GetValue<string>(), b: wanted, comparisonType: StringComparison.Ordinal),
        JsonValueKind.Number => (decimal.TryParse(s: wanted, provider: CultureInfo.InvariantCulture, style: NumberStyles.Number, result: out var wantedNumber) &&
            node.AsValue().TryGetValue<decimal>(value: out var actualNumber) &&
            (wantedNumber == actualNumber)),
        JsonValueKind.True or JsonValueKind.False => (bool.TryParse(value: wanted, result: out var wantedBool) && (node.GetValue<bool>() == wantedBool)),
        _ => string.Equals(a: node.ToJsonString(), b: wanted, comparisonType: StringComparison.Ordinal),
    });

    /// <summary>Walks EVERY given segment, resolving a trailing bracket at each step against the array it names —
    /// the whole-path form <c>world.row.add</c>/<c>world.row.remove</c> use to land ON a list field itself (the last
    /// segment is a bare name, so the loop ends with <c>container</c> holding the array), and the partial form
    /// (<paramref name="segments"/> excluding the caller's own trailing field) <c>world.row.set</c>'s literal form,
    /// <c>world.row.step</c>, and <c>world.row</c> use to land ONE level short of their own leaf.</summary>
    /// <param name="root">The row's JSON node to walk from.</param>
    /// <param name="segments">The segments to walk, in order.</param>
    /// <param name="path">The full field path, for a refusal message.</param>
    /// <param name="container">The node every segment landed on, on success.</param>
    /// <param name="error">The refusal reason, when the method returns <see langword="false"/>.</param>
    public static bool TryNavigate(JsonNode root, ReadOnlySpan<Segment> segments, string path, out JsonNode? container, out string? error) {
        var current = ((JsonNode?)root);

        foreach (var segment in segments) {
            if (current is not JsonObject obj) {
                container = null;
                error = $"'{path}': '{segment.Name}' has no parent object to walk into";

                return false;
            }

            if (
                !obj.TryGetPropertyValue(
                    propertyName: segment.Name,
                    jsonNode: out var next
                ) ||
                (next is null)
            ) {
                container = null;
                error = $"'{path}': unknown or empty member '{segment.Name}'";

                return false;
            }

            if (segment.HasBracket) {
                if (next is not JsonArray array) {
                    container = null;
                    error = $"'{path}': '{segment.Name}' is not a list";

                    return false;
                }

                if (!TryResolveArrayElement(
                    array: array,
                    error: out error,
                    index: out var index,
                    path: path,
                    segment: segment
                )) {
                    container = null;

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
    // A bracketed FINAL segment still names an array member of `container` (last.Name) — container is the object
    // that OWNS the array, never the array itself, exactly like an intermediate bracketed segment's own container
    // before TryNavigate indexes into it. TryGetLeaf/TrySetLeaf share this lookup rather than assuming container IS
    // already the array.
    private static bool TryResolveBracketedArray(JsonNode container, Segment last, string path, out JsonArray? array, out string? error) {
        array = null;

        if (container is not JsonObject obj) {
            error = $"'{path}': not an object";

            return false;
        }

        if (
            !obj.TryGetPropertyValue(
                propertyName: last.Name,
                jsonNode: out var next
            ) ||
            (next is null)
        ) {
            error = $"'{path}': unknown or empty member '{last.Name}'";

            return false;
        }

        if (next is not JsonArray resolved) {
            error = $"'{path}': '{last.Name}' is not a list";

            return false;
        }

        array = resolved;
        error = null;

        return true;
    }
    /// <summary>Locates the leaf's own container plus the leaf node itself — a <see cref="JsonObject"/> for a NAME
    /// segment, a <see cref="JsonArray"/> for a trailing INDEX or SELECTOR — so a caller can both READ the current
    /// value (<see cref="WorldRowFieldStepper"/>'s delta, <c>world.row</c>'s read-back) and assign a replacement in
    /// place. The leaf must already carry a value: this is the READ path, never the CREATE path — see
    /// <see cref="TrySetLeaf"/> for that.</summary>
    /// <param name="container">The leaf's container, from <see cref="TryNavigate"/>.</param>
    /// <param name="last">The leaf's own segment.</param>
    /// <param name="path">The full field path, for a refusal message.</param>
    /// <param name="leaf">The leaf node, on success.</param>
    /// <param name="error">The refusal reason, when the method returns <see langword="false"/>.</param>
    public static bool TryGetLeaf(JsonNode container, Segment last, string path, out JsonNode? leaf, out string? error) {
        if (last.HasBracket) {
            // A bracket on the FINAL segment still names an array PROPERTY of `container` (last.Name) — container
            // itself is never the array; TryNavigate only lands directly on one for an INTERMEDIATE bracketed
            // segment, which it resolves in the same step as walking past it.
            if (!TryResolveBracketedArray(
                array: out var array,
                container: container,
                error: out error,
                last: last,
                path: path
            )) {
                leaf = null;

                return false;
            }

            if (!TryResolveArrayElement(
                array: array!,
                error: out error,
                index: out var index,
                path: path,
                segment: last
            )) {
                leaf = null;

                return false;
            }

            leaf = array![index];
        } else {
            if (container is not JsonObject obj) {
                leaf = null;
                error = $"'{path}': not an object";

                return false;
            }

            if (
                !obj.TryGetPropertyValue(
                    propertyName: last.Name,
                    jsonNode: out leaf
                ) ||
                (leaf is null)
            ) {
                error = $"'{path}': unknown or empty member '{last.Name}'";

                return false;
            }
        }

        error = null;

        return true;
    }
    /// <summary>Assigns <paramref name="replacement"/> (or JSON <see langword="null"/>, to clear a nullable field) at
    /// <paramref name="last"/> inside <paramref name="container"/> — <c>world.row.set</c>'s literal form. A NAME
    /// segment CREATES the member when it was absent (an optional field the current row omits, since
    /// <c>[JsonIgnore(Condition = WhenWritingNull)]</c> drops it from the tree); an INDEX or SELECTOR segment
    /// addresses an element that must already exist — this never inserts one (<c>world.row.add</c> does
    /// that).</summary>
    /// <param name="container">The leaf's container, from <see cref="TryNavigate"/>.</param>
    /// <param name="last">The leaf's own segment.</param>
    /// <param name="path">The full field path, for a refusal message.</param>
    /// <param name="replacement">The new value, or <see langword="null"/> for JSON null.</param>
    /// <param name="error">The refusal reason, when the method returns <see langword="false"/>.</param>
    public static bool TrySetLeaf(JsonNode container, Segment last, string path, JsonNode? replacement, out string? error) {
        if (last.HasBracket) {
            if (!TryResolveBracketedArray(
                array: out var array,
                container: container,
                error: out error,
                last: last,
                path: path
            )) {
                return false;
            }

            if (!TryResolveArrayElement(
                array: array!,
                error: out error,
                index: out var index,
                path: path,
                segment: last
            )) {
                return false;
            }

            array![index] = replacement;
        } else {
            if (container is not JsonObject obj) {
                error = $"'{path}': not an object";

                return false;
            }

            obj[last.Name] = replacement;
        }

        error = null;

        return true;
    }
    /// <summary>Walks <paramref name="rowType"/>'s own declared CLR properties through <paramref name="segments"/> to
    /// the FINAL segment's type (<see cref="Nullable{T}"/> unwrapped at every step) — the JSON tree carries no type
    /// info of its own, and only <see cref="WorldRowFieldStepper"/>'s enum/numeric typing needs one.</summary>
    /// <param name="rowType">The row's own CLR type.</param>
    /// <param name="segments">The segments to walk.</param>
    /// <param name="leafType">The final segment's declared CLR type, on success.</param>
    /// <param name="error">The refusal reason, when the method returns <see langword="false"/>.</param>
    public static bool TryResolveClrType(Type rowType, ReadOnlySpan<Segment> segments, out Type? leafType, out string? error) {
        var current = rowType;

        foreach (var segment in segments) {
            current = (Nullable.GetUnderlyingType(nullableType: current) ?? current);

            var property = current.GetProperty(
                bindingAttr: (System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase),
                name: segment.Name
            );

            if (property is null) {
                leafType = null;
                error = $"unknown member '{segment.Name}'";

                return false;
            }

            current = property.PropertyType;

            if (segment.HasBracket) {
                if (ElementTypeOf(collectionType: current) is not { } elementType) {
                    leafType = null;
                    error = $"'{segment.Name}' is not a list";

                    return false;
                }

                current = elementType;
            }
        }

        leafType = (Nullable.GetUnderlyingType(nullableType: current) ?? current);
        error = null;

        return true;
    }
    /// <summary>The element type of a list-shaped CLR member — an array, or anything implementing
    /// <see cref="IReadOnlyList{T}"/>.</summary>
    /// <param name="collectionType">The candidate list type.</param>
    public static Type? ElementTypeOf(Type collectionType) {
        if (collectionType.IsArray) {
            return collectionType.GetElementType();
        }

        if (
            collectionType.IsGenericType &&
            (collectionType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        ) {
            return collectionType.GetGenericArguments()[0];
        }

        foreach (var candidate in collectionType.GetInterfaces()) {
            if (
                candidate.IsGenericType &&
                (candidate.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            ) {
                return candidate.GetGenericArguments()[0];
            }
        }

        return null;
    }
}
