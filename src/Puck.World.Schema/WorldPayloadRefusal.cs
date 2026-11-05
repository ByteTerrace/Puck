using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>A payload the schema refused, structured where it was refused: the document member that failed, what the
/// schema expected there and what the document holds. Every wording of the refusal comes from these fields
/// (<see cref="Authored"/>), never from the serializer's English.</summary>
/// <param name="Path">The refused member as an author reads it (<c>shapes[1].blend</c>); empty when the payload as a
/// whole is refused.</param>
/// <param name="Pointer">The JSON pointer of the refused member; empty for the payload as a whole.</param>
/// <param name="Expected">What the schema reads at <paramref name="Path"/>, or <see langword="null"/> when its type
/// carries its own reader or shape.</param>
/// <param name="Found">What the document holds at <paramref name="Path"/>, or <see langword="null"/> when it holds
/// nothing there.</param>
/// <param name="Detail">The refusal's own words, on one line, for a refusal that is not one value of the wrong kind.</param>
/// <param name="Payload">The head of the payload the refusal quotes, or <see langword="null"/> when there is none to
/// quote.</param>
public sealed record WorldPayloadRefusal(string Path, string Pointer, DocumentValueKind? Expected, DocumentValueKind? Found, string Detail, string? Payload) {
    private readonly record struct Step(string Text, bool IsIndex, bool IsQuoted);

    /// <summary>Gets whether the refusal is one value of the wrong kind: the schema expects one kind at
    /// <see cref="Path"/> and the document holds another.</summary>
    public bool IsWrongKind => ((Expected is { } expected) && (Found is { } found) && (expected != found));
    /// <summary>Gets the refusal in the author's words: the member, then what the schema expected against what the
    /// document holds.</summary>
    public string Authored {
        get {
            var said = (IsWrongKind ? $"expected {Expected!.Value.Words()}, found {Found!.Value.Words()}" : Detail);

            return ((Path.Length == 0) ? said : $"{Path}: {said}");
        }
    }
    /// <summary>Gets the refusal for a console line: its own words, the member it names and the payload head it quotes.</summary>
    public string Described {
        get {
            var where = ((Path.Length == 0) ? string.Empty : $" (at {Path})");

            return ((Payload is null) ? $"{Detail}{where}" : $"{Detail}{where} (payload: {Payload})");
        }
    }

    // The members a JSON path names, in order: `$.shapes[1].blend` is `shapes`, `1`, `blend`.
    private static List<Step> Steps(string path) {
        var steps = new List<Step>();
        var index = (path.StartsWith(value: '$') ? 1 : 0);

        while (index < path.Length) {
            if (path[index] == '[') {
                var quoted = (((index + 1) < path.Length) && (path[(index + 1)] == '\''));
                var close = path.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: (index + 1), value: (quoted ? "']" : "]"));

                if (close < 0) {
                    break;
                }

                var start = (index + (quoted ? 2 : 1));

                steps.Add(item: new Step(IsIndex: !quoted, IsQuoted: quoted, Text: path[start..close]));
                index = (close + (quoted ? 2 : 1));
            } else {
                var start = ((path[index] == '.') ? (index + 1) : index);
                var end = path.IndexOfAny(anyOf: ['.', '['], startIndex: start);

                if (end < 0) {
                    end = path.Length;
                }

                steps.Add(item: new Step(IsIndex: false, IsQuoted: false, Text: path[start..end]));
                index = end;
            }
        }

        return steps;
    }
    private static string Spell(List<Step> steps) {
        var text = new StringBuilder();

        foreach (var step in steps) {
            _ = (step.IsIndex
                ? text.Append(value: '[').Append(value: step.Text).Append(value: ']')
                : step.IsQuoted
                    ? text.Append(value: "['").Append(value: step.Text).Append(value: "']")
                    : ((text.Length > 0) ? text.Append(value: '.') : text).Append(value: step.Text));
        }

        return text.ToString();
    }
    private static Type? ElementOf(Type type) {
        if (type.IsArray) {
            return type.GetElementType();
        }
        if (type.IsGenericType && (DocumentValueKinds.Of(type: type) == DocumentValueKind.List)) {
            return type.GetGenericArguments()[0];
        }

        return ((WorldModelShape.Of(type: type) is { Kind: JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary, ElementType: { } element }) ? element : null);
    }
    // The arm of a polymorphic `shape` whose discriminator the object `node` carries; null when `node` names none.
    private static Type? ArmOf(WorldModelType shape, Type type, JsonNode? node) =>
        (((shape.Arms.Count > 0) && ((node as JsonObject)?["$type"] is JsonValue discriminator) && discriminator.TryGetValue(value: out string? name) &&
            (shape.Arms.FirstOrDefault(predicate: arm => (arm.Discriminator == name)) is { } arm) && (arm.Type != type))
            ? arm.Type
            : null);
    // The member `step` names under a value of `type`, whose document node is `node`; null when the model does not say.
    private static Type? Descend(Type type, Step step, JsonNode? node) {
        type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);

        if (ElementOf(type: type) is { } element) {
            return element;
        }
        if (WorldModelShape.Of(type: type) is not { } shape) {
            return null;
        }
        if (ArmOf(node: node, shape: shape, type: type) is { } armType) {
            return Descend(node: node, step: step, type: armType);
        }

        var members = (shape.Described ? shape.Members : shape.Properties);

        return members.FirstOrDefault(predicate: member => (member.Name == step.Text))?.Type;
    }
    // The model type the document reads at the end of `steps`, found by following the model's own member and element
    // types; null when the model does not say.
    private static Type? TypeAt(Type root, List<Step> steps, JsonNode? document) {
        var type = (Type?)root;
        var node = document;

        foreach (var step in steps) {
            type = ((type is null) ? null : Descend(node: node, step: step, type: type));
            node = DocumentJsonPointer.At(root: node, pointer: DocumentJsonPointer.Child(name: step.Text, pointer: string.Empty));
            if (type is null) {
                return null;
            }
        }

        return type;
    }
    // The required members of the object the document holds at the end of `steps` that it leaves out: the serializer
    // refuses such an object by naming a CLR type, so the refusal names the members from the model's own shape.
    private static List<string> MissingAt(Type root, List<Step> steps, JsonNode? document) {
        if ((TypeAt(document: document, root: root, steps: steps) is not { } declared) ||
            (DocumentJsonPointer.At(root: document, pointer: PointerOf(steps: steps)) is not JsonObject node)) {
            return [];
        }

        var type = (Nullable.GetUnderlyingType(nullableType: declared) ?? declared);

        if (WorldModelShape.Of(type: type) is not { } shape) {
            return [];
        }

        type = (ArmOf(node: node, shape: shape, type: type) ?? type);

        var members = (WorldModelShape.Of(type: type) ?? shape).Members;

        // The serializer meets an unmapped member before it checks the required ones, so such an object was refused
        // for the unmapped member and its words stand.
        if (!members.Any(predicate: static member => ((member.Access & WorldModelAccess.ExtensionData) != 0)) &&
            node.Any(predicate: pair => !members.Any(predicate: member => (member.Name == pair.Key)))) {
            return [];
        }

        return [.. members
            .Where(predicate: member => (((member.Access & WorldModelAccess.Required) != 0) && !node.ContainsKey(propertyName: member.Name)))
            .Select(selector: static member => member.Name)];
    }
    // The refusal of a member the object at the end of `steps` does not have: the serializer names a CLR type for it, so
    // the refusal is read from the model's own shape instead (the last step is the unmapped member, its parent the
    // object that refused it). The words name the closest members the object does have, or all of them when none is
    // close; null when the last step is a member of its parent (or the parent keeps unmapped members as data).
    private static string? UnknownMemberWords(Type root, List<Step> steps, JsonNode? document) {
        if (
            (steps.Count == 0) || steps[^1].IsIndex ||
            (TypeAt(document: document, root: root, steps: steps[..^1]) is not { } declared) ||
            (DocumentJsonPointer.At(root: document, pointer: PointerOf(steps: steps[..^1])) is not JsonObject node) ||
            !node.ContainsKey(propertyName: steps[^1].Text)
        ) {
            return null;
        }

        var type = (Nullable.GetUnderlyingType(nullableType: declared) ?? declared);

        if (WorldModelShape.Of(type: type) is not { } shape) {
            return null;
        }

        type = (ArmOf(node: node, shape: shape, type: type) ?? type);

        var arm = (WorldModelShape.Of(type: type) ?? shape);
        var members = arm.Members;

        if (!arm.Described || (arm.Kind != JsonTypeInfoKind.Object) || members.Any(predicate: static member => ((member.Access & WorldModelAccess.ExtensionData) != 0)) ||
            members.Any(predicate: member => (member.Name == steps[^1].Text))) {
            return null;
        }

        var accepted = members
            .Where(predicate: static member => ((member.Access & (WorldModelAccess.Write | WorldModelAccess.Ignored)) == WorldModelAccess.Write))
            .Select(selector: static member => member.Name)
            .ToArray();
        var near = DocumentNearestNames.Among(candidates: accepted, name: steps[^1].Text);
        var listed = ((near.Length > 0) ? near : ((accepted.Length <= MaxListedMembers) ? accepted : []));

        return ((listed.Length == 0)
            ? "unknown member"
            : $"unknown member; {((near.Length > 0) ? "nearest" : "valid")} members: {string.Join(separator: ", ", values: listed.Select(selector: static name => $"'{name}'"))}");
    }
    private const int MaxListedMembers = 12;
    // The refusal of a member read through the strict enum converter, which accepts exactly the declared names: the
    // names it accepts against what the document holds, or null when the member is not read that way or holds a name.
    private static string? EnumWords(Type root, List<Step> steps, JsonNode? document) {
        if ((steps.Count == 0) || (TypeAt(document: document, root: root, steps: steps) is not { } declared)) {
            return null;
        }

        var type = (Nullable.GetUnderlyingType(nullableType: declared) ?? declared);

        if (!type.IsEnum || !IsStrictEnum(type: type)) {
            return null;
        }

        var held = DocumentJsonPointer.At(root: document, pointer: PointerOf(steps: steps));
        var text = ((held is JsonValue value) && value.TryGetValue(value: out string? name) ? name : null);
        var names = Enum.GetNames(enumType: type);

        if ((text is not null) && names.Contains(value: text, comparer: StringComparer.Ordinal)) {
            return null;
        }

        var found = ((text is not null) ? $"'{text}'" : (DocumentValueKinds.Of(node: held)?.Words() ?? "nothing"));

        return $"expected one of {string.Join(separator: ", ", values: names.Select(selector: static member => $"'{member}'"))}, found {found}";
    }
    // An enum declares the strict converter on itself, or the context lists it for an enum it does not own.
    private static bool IsStrictEnum(Type type) =>
        IsStrictConverter(converter: type.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType) ||
        WorldJsonContext.Default.Options.Converters.Any(predicate: converter => (IsStrictConverter(converter: converter.GetType()) && converter.CanConvert(typeToConvert: type)));
    private static bool IsStrictConverter(Type? converter) =>
        (converter is { IsGenericType: true }) && (converter.GetGenericTypeDefinition() == typeof(Puck.Abstractions.Documents.StrictEnumConverter<>));
    private static string PointerOf(List<Step> steps) =>
        steps.Aggregate(func: static (pointer, step) => DocumentJsonPointer.Child(name: step.Text, pointer: pointer), seed: string.Empty);
    // The refusal's words for required members the document leaves out.
    private static string MissingWords(List<string> missing) => ((missing.Count == 1)
        ? $"missing required member '{missing[0]}'"
        : $"missing required members {string.Join(separator: ", ", values: missing.Select(selector: static name => $"'{name}'"))}");
    // What the schema reads at the end of `steps`, found by following the model's own member and element types.
    private static DocumentValueKind? ExpectedAt(Type root, List<Step> steps, JsonNode? document) {
        var type = TypeAt(document: document, root: root, steps: steps);

        if (type is null) {
            return null;
        }
        if (DocumentValueKinds.Of(type: type) is { } kind) {
            return kind;
        }

        return ((WorldModelShape.Of(type: type) is { Kind: JsonTypeInfoKind.Object or JsonTypeInfoKind.Dictionary }) ? DocumentValueKind.Block : null);
    }
    // The serializer appends where it refused to the message of the exceptions it raises itself; the path, line and
    // byte position it appends are on the exception, so the suffix is removed by what it is, not by what it looks like.
    private static string Said(Exception exception) {
        var message = exception.Message.ReplaceLineEndings(replacementText: " ");

        if (exception is JsonException { Path: { } path, LineNumber: { } line, BytePositionInLine: { } position }) {
            var suffix = $" Path: {path} | LineNumber: {line} | BytePositionInLine: {position}.";

            if (message.EndsWith(value: suffix, comparisonType: StringComparison.Ordinal)) {
                return message[..^suffix.Length];
            }
        }

        return message;
    }

    /// <summary>Returns the refusal a payload's parse failure is, placed in the document it failed on.</summary>
    /// <param name="exception">The parse failure.</param>
    /// <param name="json">The payload that failed.</param>
    /// <param name="type">The type the payload was read as.</param>
    /// <returns>The structured refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> or <paramref name="type"/> is <see langword="null"/>.</exception>
    public static WorldPayloadRefusal Of(Exception exception, string json, Type type) {
        ArgumentNullException.ThrowIfNull(argument: exception);
        ArgumentNullException.ThrowIfNull(argument: type);

        var steps = ((exception is JsonException { Path: { Length: > 0 } path }) ? Steps(path: path) : []);
        var pointer = PointerOf(steps: steps);
        var detail = Said(exception: exception);
        JsonNode? document = null;
        DocumentValueKind? expected = null;

        if (exception is JsonException { Path.Length: > 0 }) {
            try {
                document = JsonNode.Parse(json: json);
                expected = ((steps.Count > 0) ? ExpectedAt(document: document, root: type, steps: steps) : null);
                if (UnknownMemberWords(document: document, root: type, steps: steps) is { } unknown) {
                    detail = unknown;
                } else if (MissingAt(document: document, root: type, steps: steps) is { Count: > 0 } missing) {
                    detail = MissingWords(missing: missing);
                } else if (EnumWords(document: document, root: type, steps: steps) is { } words) {
                    detail = words;
                }
            } catch (Exception failure) when (failure is JsonException or InvalidOperationException) {
                // The text that failed to parse into a row fails to parse into a tree too, or the build has no model table.
            }
        }

        return new WorldPayloadRefusal(
            Detail: detail,
            Expected: expected,
            Found: DocumentValueKinds.Of(node: DocumentJsonPointer.At(root: document, pointer: pointer)),
            Path: Spell(steps: steps),
            Payload: WorldJsonPayload.Elide(json: json),
            Pointer: pointer
        );
    }
    /// <summary>Returns the refusal of a payload that is absent or parses to nothing.</summary>
    /// <param name="detail">The refusal's words.</param>
    /// <returns>The refusal, which quotes no payload.</returns>
    public static WorldPayloadRefusal Whole(string detail) => new(Detail: detail, Expected: null, Found: null, Path: string.Empty, Payload: null, Pointer: string.Empty);
}
