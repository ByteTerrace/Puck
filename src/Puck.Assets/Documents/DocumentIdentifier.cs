using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Assets.Documents;

/// <summary>A literal document identifier or a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> reference to one.</summary>
/// <remarks>
/// JSON strings beginning with <c>state.</c> are references; every other string is a literal identifier. A containing
/// world resolves references from Text state cells after the complete document is available. The reference remains
/// attached after resolution so canonical serialization preserves the authored single source of truth.
/// </remarks>
[JsonConverter(typeof(DocumentIdentifierJsonConverter))]
public sealed class DocumentIdentifier : IDocumentStateValue, IEquatable<DocumentIdentifier> {
    private bool m_isResolved;
    private string? m_value;

    /// <summary>Creates a literal identifier.</summary>
    public DocumentIdentifier(string value) {
        m_value = value ?? throw new ArgumentNullException(paramName: nameof(value));
        m_isResolved = true;
    }

    private DocumentIdentifier(string reference, bool _) => Reference = reference;

    /// <inheritdoc/>
    public string? Reference { get; }
    /// <inheritdoc/>
    public string ExpectedValue => "a non-empty identifier";
    /// <summary>The resolved identifier.</summary>
    public string Value => (m_isResolved
        ? m_value!
        : throw new InvalidOperationException(message: $"document identifier reference '{Reference}' has not been resolved by its containing document."));

    /// <inheritdoc/>
    public bool TryResolve(string text, out string reason) {
        if (string.IsNullOrEmpty(value: text)) {
            reason = "the identifier must be non-empty.";
            return false;
        }

        m_value = text;
        m_isResolved = true;
        reason = string.Empty;
        return true;
    }

    /// <summary>Wraps a literal identifier.</summary>
    public static implicit operator DocumentIdentifier(string value) => new(value: value);
    /// <summary>Reads the resolved identifier.</summary>
    public static implicit operator string(DocumentIdentifier value) => value.Value;
    /// <inheritdoc/>
    public bool Equals(DocumentIdentifier? other) => (other is not null) && ((Reference is { } reference)
        ? string.Equals(a: reference, b: other.Reference, comparisonType: StringComparison.Ordinal)
        : (other.Reference is null) && string.Equals(a: m_value, b: other.m_value, comparisonType: StringComparison.Ordinal));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => (obj is DocumentIdentifier other) && Equals(other: other);
    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(obj: (Reference ?? m_value ?? string.Empty));
    /// <inheritdoc/>
    public override string ToString() => Value;

    internal static DocumentIdentifier FromReference(string reference) => new(reference: reference, _: true);
}

/// <summary>Reads literal identifiers and state references while preserving both as JSON strings.</summary>
public sealed class DocumentIdentifierJsonConverter : JsonConverter<DocumentIdentifier>, IJsonSchemaStringConverter {
    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens => null;

    /// <inheritdoc/>
    public override DocumentIdentifier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.String) {
            throw new JsonException(message: "a document identifier must be a string.");
        }

        var value = reader.GetString();
        if (value is null) {
            throw new JsonException(message: "a document identifier must be a non-null string.");
        }

        return value.StartsWith(value: "state.", comparisonType: StringComparison.Ordinal)
            ? DocumentIdentifier.FromReference(reference: value)
            : new DocumentIdentifier(value: value);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DocumentIdentifier value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value: (value.Reference ?? value.Value));
}
