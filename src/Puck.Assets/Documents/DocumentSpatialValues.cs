using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Assets.Documents;

/// <summary>A state-resolvable document value whose literal form is a spatial JSON array.</summary>
public interface IDocumentSpatialValue : IDocumentStateValue;

/// <summary>A <see cref="Vector2"/> authored as <c>[x, y]</c> or as a symbolic reference string.</summary>
[JsonConverter(typeof(DocumentVector2JsonConverter))]
public sealed class DocumentVector2 : IDocumentSpatialValue, IEquatable<DocumentVector2> {
    private bool m_isResolved;
    private Vector2 m_value;

    /// <summary>Creates a literal value.</summary>
    public DocumentVector2(Vector2 value) {
        m_isResolved = true;
        m_value = value;
    }
    internal DocumentVector2(string reference) => Reference = reference;

    /// <inheritdoc/>
    public string? Reference { get; }
    /// <inheritdoc/>
    public string ExpectedValue => "a Vector2 array [x, y]";
    /// <summary>The resolved value.</summary>
    public Vector2 Value => (m_isResolved
        ? m_value
        : throw new InvalidOperationException(message: $"document spatial reference '{Reference}' has not been resolved by its containing document."));
    /// <summary>The resolved X component.</summary>
    public float X => Value.X;
    /// <summary>The resolved Y component.</summary>
    public float Y => Value.Y;

    /// <inheritdoc/>
    public bool TryResolve(string text, out string reason) {
        try {
            m_value = JsonSerializer.Deserialize<Vector2>(json: text, options: DocumentJsonOptions.Shared);
            m_isResolved = true;
            reason = string.Empty;
            return true;
        } catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }
    }

    /// <summary>Wraps a literal without allocation at call sites.</summary>
    public static implicit operator DocumentVector2(Vector2 value) => new(value: value);
    /// <summary>Reads the resolved value.</summary>
    public static implicit operator Vector2(DocumentVector2 value) => value.Value;
    /// <inheritdoc/>
    public bool Equals(DocumentVector2? other) => (other is not null) && ((Reference is { } reference)
        ? string.Equals(reference, other.Reference, StringComparison.Ordinal)
        : (other.Reference is null) && m_value.Equals(other.m_value));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => (obj is DocumentVector2 other) && Equals(other: other);
    /// <inheritdoc/>
    public override int GetHashCode() => ((Reference is { } reference)
        ? StringComparer.Ordinal.GetHashCode(obj: reference)
        : m_value.GetHashCode());
}

/// <summary>A <see cref="Vector3"/> authored as <c>[x, y, z]</c> or as a symbolic reference string.</summary>
[JsonConverter(typeof(DocumentVector3JsonConverter))]
public sealed class DocumentVector3 : IDocumentSpatialValue, IEquatable<DocumentVector3> {
    private bool m_isResolved;
    private Vector3 m_value;

    /// <summary>Creates a literal value.</summary>
    public DocumentVector3(Vector3 value) {
        m_isResolved = true;
        m_value = value;
    }
    /// <summary>Creates a literal value from its components.</summary>
    public DocumentVector3(float x, float y, float z) : this(value: new Vector3(x: x, y: y, z: z)) {
    }
    internal DocumentVector3(string reference) => Reference = reference;

    /// <inheritdoc/>
    public string? Reference { get; }
    /// <inheritdoc/>
    public string ExpectedValue => "a Vector3 array [x, y, z]";
    /// <summary>The resolved value.</summary>
    public Vector3 Value => (m_isResolved
        ? m_value
        : throw new InvalidOperationException(message: $"document spatial reference '{Reference}' has not been resolved by its containing document."));
    /// <summary>The resolved X component.</summary>
    public float X => Value.X;
    /// <summary>The resolved Y component.</summary>
    public float Y => Value.Y;
    /// <summary>The resolved Z component.</summary>
    public float Z => Value.Z;
    /// <summary>Returns the resolved vector's length.</summary>
    public float Length() => Value.Length();
    /// <summary>Returns the resolved vector's squared length.</summary>
    public float LengthSquared() => Value.LengthSquared();

    /// <inheritdoc/>
    public bool TryResolve(string text, out string reason) {
        try {
            m_value = JsonSerializer.Deserialize<Vector3>(json: text, options: DocumentJsonOptions.Shared);
            m_isResolved = true;
            reason = string.Empty;
            return true;
        } catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }
    }

    /// <summary>Wraps a literal without allocation at call sites.</summary>
    public static implicit operator DocumentVector3(Vector3 value) => new(value: value);
    /// <summary>Reads the resolved value.</summary>
    public static implicit operator Vector3(DocumentVector3 value) => value.Value;
    /// <summary>Scales the resolved value.</summary>
    public static Vector3 operator *(DocumentVector3 value, float scale) => (value.Value * scale);
    /// <inheritdoc/>
    public bool Equals(DocumentVector3? other) => (other is not null) && ((Reference is { } reference)
        ? string.Equals(reference, other.Reference, StringComparison.Ordinal)
        : (other.Reference is null) && m_value.Equals(other.m_value));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => (obj is DocumentVector3 other) && Equals(other: other);
    /// <inheritdoc/>
    public override int GetHashCode() => ((Reference is { } reference)
        ? StringComparer.Ordinal.GetHashCode(obj: reference)
        : m_value.GetHashCode());
}

/// <summary>A <see cref="Quaternion"/> authored as <c>[x, y, z, w]</c> or as a symbolic reference string.</summary>
[JsonConverter(typeof(DocumentQuaternionJsonConverter))]
public sealed class DocumentQuaternion : IDocumentSpatialValue, IEquatable<DocumentQuaternion> {
    private bool m_isResolved;
    private Quaternion m_value;

    /// <summary>Creates a literal value.</summary>
    public DocumentQuaternion(Quaternion value) {
        m_isResolved = true;
        m_value = value;
    }
    /// <summary>Creates a literal value from its components.</summary>
    public DocumentQuaternion(float x, float y, float z, float w) : this(value: new Quaternion(x: x, y: y, z: z, w: w)) {
    }
    internal DocumentQuaternion(string reference) => Reference = reference;

    /// <inheritdoc/>
    public string? Reference { get; }
    /// <inheritdoc/>
    public string ExpectedValue => "a Quaternion array [x, y, z, w]";
    /// <summary>The resolved value.</summary>
    public Quaternion Value => (m_isResolved
        ? m_value
        : throw new InvalidOperationException(message: $"document spatial reference '{Reference}' has not been resolved by its containing document."));
    /// <summary>The resolved X component.</summary>
    public float X => Value.X;
    /// <summary>The resolved Y component.</summary>
    public float Y => Value.Y;
    /// <summary>The resolved Z component.</summary>
    public float Z => Value.Z;
    /// <summary>The resolved W component.</summary>
    public float W => Value.W;
    /// <summary>Returns the resolved quaternion's squared length.</summary>
    public float LengthSquared() => Value.LengthSquared();

    /// <inheritdoc/>
    public bool TryResolve(string text, out string reason) {
        try {
            m_value = JsonSerializer.Deserialize<Quaternion>(json: text, options: DocumentJsonOptions.Shared);
            m_isResolved = true;
            reason = string.Empty;
            return true;
        } catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }
    }

    /// <summary>Wraps a literal without allocation at call sites.</summary>
    public static implicit operator DocumentQuaternion(Quaternion value) => new(value: value);
    /// <summary>Reads the resolved value.</summary>
    public static implicit operator Quaternion(DocumentQuaternion value) => value.Value;
    /// <inheritdoc/>
    public bool Equals(DocumentQuaternion? other) => (other is not null) && ((Reference is { } reference)
        ? string.Equals(reference, other.Reference, StringComparison.Ordinal)
        : (other.Reference is null) && m_value.Equals(other.m_value));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => (obj is DocumentQuaternion other) && Equals(other: other);
    /// <inheritdoc/>
    public override int GetHashCode() => ((Reference is { } reference)
        ? StringComparer.Ordinal.GetHashCode(obj: reference)
        : m_value.GetHashCode());
}

internal static class DocumentSpatialValueJson {
    public static string ReadReference(ref Utf8JsonReader reader, string kind) {
        var reference = reader.GetString();
        if (string.IsNullOrWhiteSpace(value: reference)) {
            throw new JsonException(message: $"a {kind} reference must be a non-empty string.");
        }
        return reference;
    }
}

/// <summary>Reads and writes <see cref="DocumentVector2"/>.</summary>
public sealed class DocumentVector2JsonConverter : JsonConverter<DocumentVector2> {
    /// <inheritdoc/>
    public override DocumentVector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        (reader.TokenType == JsonTokenType.String)
            ? new DocumentVector2(reference: DocumentSpatialValueJson.ReadReference(reader: ref reader, kind: "Vector2"))
            : new DocumentVector2(value: new Vector2JsonConverter().Read(reader: ref reader, typeToConvert: typeof(Vector2), options: options));
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DocumentVector2 value, JsonSerializerOptions options) {
        if (value.Reference is { } reference) {
            writer.WriteStringValue(value: reference);
        } else {
            new Vector2JsonConverter().Write(writer: writer, value: value.Value, options: options);
        }
    }
}

/// <summary>Reads and writes <see cref="DocumentVector3"/>.</summary>
public sealed class DocumentVector3JsonConverter : JsonConverter<DocumentVector3> {
    /// <inheritdoc/>
    public override DocumentVector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        (reader.TokenType == JsonTokenType.String)
            ? new DocumentVector3(reference: DocumentSpatialValueJson.ReadReference(reader: ref reader, kind: "Vector3"))
            : new DocumentVector3(value: new Vector3JsonConverter().Read(reader: ref reader, typeToConvert: typeof(Vector3), options: options));
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DocumentVector3 value, JsonSerializerOptions options) {
        if (value.Reference is { } reference) {
            writer.WriteStringValue(value: reference);
        } else {
            new Vector3JsonConverter().Write(writer: writer, value: value.Value, options: options);
        }
    }
}

/// <summary>Reads and writes <see cref="DocumentQuaternion"/>.</summary>
public sealed class DocumentQuaternionJsonConverter : JsonConverter<DocumentQuaternion> {
    /// <inheritdoc/>
    public override DocumentQuaternion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        (reader.TokenType == JsonTokenType.String)
            ? new DocumentQuaternion(reference: DocumentSpatialValueJson.ReadReference(reader: ref reader, kind: "Quaternion"))
            : new DocumentQuaternion(value: new QuaternionJsonConverter().Read(reader: ref reader, typeToConvert: typeof(Quaternion), options: options));
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DocumentQuaternion value, JsonSerializerOptions options) {
        if (value.Reference is { } reference) {
            writer.WriteStringValue(value: reference);
        } else {
            new QuaternionJsonConverter().Write(writer: writer, value: value.Value, options: options);
        }
    }
}
