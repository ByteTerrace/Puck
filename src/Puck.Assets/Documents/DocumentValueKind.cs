using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Assets.Documents;

/// <summary>The kinds of value a document holds, as an author tells them apart.</summary>
public enum DocumentValueKind {
    /// <summary>Text.</summary>
    Text,
    /// <summary>A true or false.</summary>
    Boolean,
    /// <summary>A number, with or without a fraction.</summary>
    Number,
    /// <summary>A number with no fraction.</summary>
    WholeNumber,
    /// <summary>A list of values, written between square brackets.</summary>
    List,
    /// <summary>A block of named members, written between braces.</summary>
    Block,
}
/// <summary>The one home of the words that name a <see cref="DocumentValueKind"/> and of how a value's kind is read:
/// a parse refusal, a schema refusal and a lowering refusal each say what a value is, or what it should have been, in
/// these words, never in a CLR type name or the serializer's.</summary>
public static class DocumentValueKinds {
    /// <summary>Returns the words an author reads for a kind, the form that follows "expected" or "found".</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The words: <c>text</c>, <c>true or false</c>, <c>a number</c>, <c>a whole number</c>, <c>a list</c> or
    /// <c>a block</c>.</returns>
    public static string Words(this DocumentValueKind kind) => kind switch {
        DocumentValueKind.Text => "text",
        DocumentValueKind.Boolean => "true or false",
        DocumentValueKind.Number => "a number",
        DocumentValueKind.WholeNumber => "a whole number",
        DocumentValueKind.List => "a list",
        DocumentValueKind.Block => "a block",
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(kind)),
    };
    /// <summary>Returns the kind a document node holds.</summary>
    /// <param name="node">The node; <see langword="null"/> for an absent or JSON <c>null</c> value.</param>
    /// <returns>The node's kind, or <see langword="null"/> when it holds no value. Every JSON number is a
    /// <see cref="DocumentValueKind.Number"/>: whether it has a fraction is the schema's question, not the text's.</returns>
    public static DocumentValueKind? Of(JsonNode? node) => node switch {
        JsonArray => DocumentValueKind.List,
        JsonObject => DocumentValueKind.Block,
        JsonValue value => value.GetValueKind() switch {
            JsonValueKind.String => DocumentValueKind.Text,
            JsonValueKind.True or JsonValueKind.False => DocumentValueKind.Boolean,
            JsonValueKind.Number => DocumentValueKind.Number,
            _ => null,
        },
        _ => null,
    };
    /// <summary>Returns the kind a CLR type reads from a document, for the types whose kind the type alone fixes.</summary>
    /// <param name="type">The type; a nullable value type answers for its underlying type.</param>
    /// <returns>The kind, or <see langword="null"/> for a type that carries its own reader or shape (an object, an
    /// enumeration, a converter-backed value).</returns>
    public static DocumentValueKind? Of(Type type) {
        ArgumentNullException.ThrowIfNull(argument: type);

        type = (Nullable.GetUnderlyingType(nullableType: type) ?? type);

        if (type == typeof(string)) {
            return DocumentValueKind.Text;
        }
        if (type == typeof(bool)) {
            return DocumentValueKind.Boolean;
        }
        if ((type == typeof(float)) || (type == typeof(double)) || (type == typeof(decimal)) || (type == typeof(Half))) {
            return DocumentValueKind.Number;
        }
        if (
            (type == typeof(byte)) || (type == typeof(sbyte)) || (type == typeof(short)) || (type == typeof(ushort)) ||
            (type == typeof(int)) || (type == typeof(uint)) || (type == typeof(long)) || (type == typeof(ulong))
        ) {
            return DocumentValueKind.WholeNumber;
        }

        return ((type.IsArray || (type.IsGenericType && (type.GetGenericTypeDefinition() is var definition) &&
            ((definition == typeof(List<>)) || (definition == typeof(IList<>)) || (definition == typeof(IReadOnlyList<>)) ||
            (definition == typeof(IReadOnlyCollection<>)) || (definition == typeof(ICollection<>)) || (definition == typeof(IEnumerable<>)))))
            ? DocumentValueKind.List
            : null
        );
    }
}
