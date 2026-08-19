using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.World;

/// <summary>
/// The generic FIELD-path walker <c>world.row.step</c> addresses a numeric/boolean/enum leaf through — one level
/// deeper than <see cref="WorldRowCommandModule"/>'s own section table, which resolves a whole ROW; this walks INTO
/// it, to one member, and applies a DELTA rather than assigning a literal. Mutates <c>root</c> (a row's live JSON
/// node, from <see cref="WorldRowCommandModule"/>'s per-section reader) IN PLACE — <see cref="JsonObject"/>/
/// <see cref="JsonArray"/> containers hold live references, so a located leaf's container is set through directly,
/// with no rebuild-the-tree step.
/// </summary>
/// <remarks>
/// <para><b>Path grammar</b>: dot-separated segments, each an optional trailing <c>[n]</c> array index —
/// <c>shapes[3].scale</c>, <c>sharpness</c> — the same grammar the (now-deleted) creation-document patcher used, so
/// a document member is addressable the day it exists, with zero stepper code.</para>
/// <para><b>Field-type semantics</b>: a JSON number ADDS <c>delta</c> (staying an integer literal when the current
/// value already is one); a JSON boolean TOGGLES on any nonzero delta; a JSON string whose declared CLR type (walked
/// by plain reflection off the row's own <c>Type</c> — a row's own field types, never a JsonTypeInfo, since the leaf
/// needs an <see cref="Type.IsEnum"/> answer no JSON metadata carries) is an enum CYCLES forward/backward by
/// <c>delta</c>'s sign, wrapping, spelled exactly as the row's own C# member (the wire's one enum spelling — see
/// <see cref="Puck.Assets.Documents.DocumentJsonOptions.Shared"/>'s remarks). Anything else (an array/object leaf —
/// every <c>Vector2</c>/<c>Vector3</c>/<c>Quaternion</c> included — or a non-enum string) refuses by name.</para>
/// </remarks>
public static class WorldRowFieldStepper {
    private readonly record struct Segment(string Name, int? Index);

    private static Type? ElementTypeOf(Type collectionType) {
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
    // Walks segment NAME/INDEX pairs through plain reflection off rowType to the FINAL segment's declared CLR type
    // (Nullable<T> unwrapped at every step) — the JsonNode tree carries no type info of its own, and only an enum
    // leaf needs one (to read its member vocabulary).
    private static bool TryResolveClrType(Type rowType, ReadOnlySpan<Segment> segments, out Type? leafType, out string? error) {
        var current = rowType;

        foreach (var segment in segments) {
            current = (Nullable.GetUnderlyingType(nullableType: current) ?? current);

            var property = current.GetProperty(
                bindingAttr: (BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase),
                name: segment.Name
            );

            if (property is null) {
                leafType = null;
                error = $"unknown member '{segment.Name}'";

                return false;
            }

            current = property.PropertyType;

            if (segment.Index is not null) {
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
    // Locates the leaf's own container (a JsonObject for a property name, a JsonArray for a trailing index) plus the
    // leaf node itself, so the caller can both read the current value and assign a replacement in place.
    private static bool TryGetLeaf(JsonNode container, Segment last, string path, out JsonNode? leaf, out string? error) {
        if (last.Index is { } index) {
            if (container is not JsonArray array) {
                leaf = null;
                error = $"'{path}': '{last.Name}' is not a list";

                return false;
            }

            if (((uint)index) >= ((uint)array.Count)) {
                leaf = null;
                error = $"'{path}': '{last.Name}[{index}]' out of range (0..{(array.Count - 1)})";

                return false;
            }

            leaf = array[index];
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
    // Splits "shapes[3].scale" into [(shapes,3), (scale,null)]; refuses malformed brackets/empty segments by name —
    // the same grammar the (now-deleted) creation-document patcher used.
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
                result[i] = new Segment(Index: null, Name: token);

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
    // Walks every segment EXCEPT the trailing one's own key/index, leaving `container` as the JsonObject/JsonArray
    // the final leaf lives in.
    private static bool TryNavigate(JsonNode root, ReadOnlySpan<Segment> segments, string path, out JsonNode? container, out string? error) {
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

            if (segment.Index is { } index) {
                if (next is not JsonArray array) {
                    container = null;
                    error = $"'{path}': '{segment.Name}' is not a list";

                    return false;
                }

                if (((uint)index) >= ((uint)array.Count)) {
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

    /// <summary>Steps the field at <paramref name="fieldPath"/> inside <paramref name="root"/> (a row's own live
    /// JSON node) by <paramref name="delta"/>, mutating the node in place.</summary>
    /// <param name="root">The row's JSON node (the whole row, for a keyed section; the whole section, for a keyless
    /// one) — mutated in place on success.</param>
    /// <param name="rowType">The row's own CLR type — the reflection root an enum leaf's vocabulary resolves
    /// through.</param>
    /// <param name="fieldPath">The dotted/indexed path to the field, relative to <paramref name="root"/>.</param>
    /// <param name="delta">The step. Added to a number; toggles a boolean on any nonzero value; cycles an enum by its
    /// sign.</param>
    /// <param name="oldText">The field's value before the step, on success.</param>
    /// <param name="newText">The field's value after the step, on success.</param>
    /// <param name="error">The refusal reason, when the method returns <see langword="false"/>.</param>
    public static bool TryStep(JsonNode root, Type rowType, string fieldPath, float delta, out string oldText, out string newText, out string? error) {
        ArgumentNullException.ThrowIfNull(argument: root);
        ArgumentNullException.ThrowIfNull(argument: rowType);

        oldText = string.Empty;
        newText = string.Empty;

        if (!float.IsFinite(f: delta)) {
            error = "delta must be a finite number";

            return false;
        }

        if (!TryParsePath(
            error: out error,
            path: fieldPath,
            segments: out var segments
        )) {
            return false;
        }

        var last = segments[^1];

        if (!TryNavigate(
            container: out var container,
            error: out error,
            path: fieldPath,
            root: root,
            segments: segments.AsSpan(start: 0, length: (segments.Length - 1))
        )) {
            return false;
        }

        if (!TryGetLeaf(
            container: container!,
            error: out error,
            last: last,
            leaf: out var leaf,
            path: fieldPath
        )) {
            return false;
        }

        JsonNode replacement;

        switch (leaf!.GetValueKind()) {
            case JsonValueKind.True or JsonValueKind.False: {
                var current = leaf.GetValue<bool>();
                var applied = ((delta != 0f) ? !current : current);

                oldText = (current ? "true" : "false");
                newText = (applied ? "true" : "false");
                replacement = JsonValue.Create(value: applied)!;

                break;
            }
            case JsonValueKind.Number when leaf.AsValue().TryGetValue<long>(value: out var currentLong): {
                var appliedLong = checked((long)Math.Round(value: (currentLong + delta), mode: MidpointRounding.AwayFromZero));

                oldText = currentLong.ToString(provider: CultureInfo.InvariantCulture);
                newText = appliedLong.ToString(provider: CultureInfo.InvariantCulture);
                replacement = JsonValue.Create(value: appliedLong)!;

                break;
            }
            case JsonValueKind.Number when leaf.AsValue().TryGetValue<double>(value: out var currentDouble): {
                var appliedDouble = (currentDouble + delta);

                oldText = currentDouble.ToString(format: "0.####", provider: CultureInfo.InvariantCulture);
                newText = appliedDouble.ToString(format: "0.####", provider: CultureInfo.InvariantCulture);
                replacement = JsonValue.Create(value: appliedDouble)!;

                break;
            }
            case JsonValueKind.String: {
                if (
                    !TryResolveClrType(
                        error: out error,
                        leafType: out var leafType,
                        rowType: rowType,
                        segments: segments
                    ) ||
                    (leafType is not { IsEnum: true })
                ) {
                    error ??= $"'{fieldPath}': not a steppable field (number, boolean, or named enum only)";

                    return false;
                }

                var names = Enum.GetNames(enumType: leafType);
                var current = leaf.GetValue<string>();
                var index = Array.IndexOf(
                    array: names,
                    value: current
                );

                if (index < 0) {
                    error = $"'{fieldPath}': current value '{current}' is not a recognized {leafType.Name} member";

                    return false;
                }

                var direction = MathF.Sign(x: delta);

                if (direction == 0) {
                    error = $"'{fieldPath}': delta must be nonzero to cycle an enum";

                    return false;
                }

                var appliedIndex = ((((index + direction) % names.Length) + names.Length) % names.Length);

                oldText = current;
                newText = names[appliedIndex];
                replacement = JsonValue.Create(value: names[appliedIndex])!;

                break;
            }
            default:
                error = $"'{fieldPath}': not a steppable field (number, boolean, or named enum only)";

                return false;
        }

        if (last.Index is { } lastIndex) {
            ((JsonArray)container!)[lastIndex] = replacement;
        } else {
            ((JsonObject)container!)[last.Name] = replacement;
        }

        error = null;

        return true;
    }
}
