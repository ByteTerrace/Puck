using System.Text;
using System.Text.Json;
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
    // The member `step` names under a value of `type`, whose document node is `node`; null when the model does not say.
    private static Type? Descend(Type type, Step step, JsonNode? node) {
        type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);

        if (ElementOf(type: type) is { } element) {
            return element;
        }
        if (WorldModelShape.Of(type: type) is not { } shape) {
            return null;
        }
        if ((shape.Arms.Count > 0) && ((node as JsonObject)?["$type"] is JsonValue discriminator) && discriminator.TryGetValue(value: out string? name) &&
            (shape.Arms.FirstOrDefault(predicate: arm => (arm.Discriminator == name)) is { } arm) && (arm.Type != type)) {
            return Descend(node: node, step: step, type: arm.Type);
        }

        var members = (shape.Described ? shape.Members : shape.Properties);

        return members.FirstOrDefault(predicate: member => (member.Name == step.Text))?.Type;
    }
    // What the schema reads at the end of `steps`, found by following the model's own member and element types.
    private static DocumentValueKind? ExpectedAt(Type root, List<Step> steps, JsonNode? document) {
        var type = (Type?)root;
        var node = document;

        foreach (var step in steps) {
            type = ((type is null) ? null : Descend(node: node, step: step, type: type));
            node = (step.IsIndex
                ? DocumentJsonPointer.At(root: node, pointer: $"/{step.Text}")
                : DocumentJsonPointer.At(root: node, pointer: $"/{DocumentJsonPointer.Escape(value: step.Text)}"));
            if (type is null) {
                return null;
            }
        }

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
        var pointer = string.Concat(values: steps.Select(selector: static step => ('/' + (step.IsIndex ? step.Text : DocumentJsonPointer.Escape(value: step.Text)))));
        JsonNode? document = null;
        DocumentValueKind? expected = null;

        if (steps.Count > 0) {
            try {
                document = JsonNode.Parse(json: json);
                expected = ExpectedAt(document: document, root: type, steps: steps);
            } catch (Exception failure) when (failure is JsonException or InvalidOperationException) {
                // The text that failed to parse into a row fails to parse into a tree too, or the build has no model table.
            }
        }

        return new WorldPayloadRefusal(
            Detail: Said(exception: exception),
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
