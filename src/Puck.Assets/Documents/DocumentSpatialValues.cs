using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Assets.Documents;

/// <summary>A state-resolvable document value whose literal form is a spatial JSON array.</summary>
public interface IDocumentSpatialValue : IDocumentStateValue;
/// <summary>A state-resolvable document value wrapping a <typeparamref name="TValue"/> literal.</summary>
public abstract class DocumentSpatialValue<TValue> : IDocumentSpatialValue
    where TValue : struct, IEquatable<TValue> {
    private bool m_isResolved;
    private TValue m_value;

    private protected DocumentSpatialValue(TValue value) {
        m_isResolved = true;
        m_value = value;
    }
    private protected DocumentSpatialValue(string reference) => Reference = reference;

    /// <inheritdoc/>
    public string? Reference { get; private set; }
    /// <inheritdoc/>
    public abstract string ExpectedValue { get; }
    /// <summary>The resolved value.</summary>
    public TValue Value => (m_isResolved
        ? m_value
        : throw new InvalidOperationException(message: $"document spatial reference '{Reference}' has not been resolved by its containing document."));

    /// <inheritdoc/>
    public void Detach() {
        _ = Value;
        Reference = null;
    }
    /// <inheritdoc/>
    public bool TryResolve(string text, out string reason) {
        try {
            m_value = JsonSerializer.Deserialize<TValue>(json: text, options: DocumentJsonOptions.Shared);
            m_isResolved = true;
            reason = string.Empty;
            return true;
        } catch (Exception exception) when ((exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }
    }

    private protected bool EqualsCore(DocumentSpatialValue<TValue>? other) => ((other is not null) && ((Reference is { } reference)
        ? string.Equals(a: reference, b: other.Reference, comparisonType: StringComparison.Ordinal)
        : ((other.Reference is null) && m_value.Equals(other: other.m_value))));
    private protected int GetHashCodeCore() => ((Reference is { } reference)
        ? StringComparer.Ordinal.GetHashCode(obj: reference)
        : m_value.GetHashCode());
}
/// <summary>A <see cref="Vector2"/> authored as <c>[x, y]</c> or as a symbolic reference string.</summary>
[JsonConverter(typeof(DocumentVector2JsonConverter))]
public sealed class DocumentVector2 : DocumentSpatialValue<Vector2>, IEquatable<DocumentVector2> {
    /// <summary>Creates a literal value.</summary>
    public DocumentVector2(Vector2 value) : base(value: value) {
    }

    internal DocumentVector2(string reference) : base(reference: reference) {
    }

    /// <inheritdoc/>
    public override string ExpectedValue => "a Vector2 array [x, y]";
    /// <summary>The resolved X component.</summary>
    public float X => Value.X;
    /// <summary>The resolved Y component.</summary>
    public float Y => Value.Y;

    /// <summary>Wraps a literal without allocation at call sites.</summary>
    public static implicit operator DocumentVector2(Vector2 value) => new(value: value);
    /// <summary>Reads the resolved value.</summary>
    public static implicit operator Vector2(DocumentVector2 value) => value.Value;

    /// <inheritdoc/>
    public bool Equals(DocumentVector2? other) => EqualsCore(other: other);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is DocumentVector2 other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() => GetHashCodeCore();
}
/// <summary>A <see cref="Vector3"/> authored as <c>[x, y, z]</c> or as a symbolic reference string.</summary>
[JsonConverter(typeof(DocumentVector3JsonConverter))]
public sealed class DocumentVector3 : DocumentSpatialValue<Vector3>, IEquatable<DocumentVector3> {
    /// <summary>Creates a literal value.</summary>
    public DocumentVector3(Vector3 value) : base(value: value) {
    }
    /// <summary>Creates a literal value from its components.</summary>
    public DocumentVector3(float x, float y, float z) : this(value: new Vector3(x: x, y: y, z: z)) {
    }

    internal DocumentVector3(string reference) : base(reference: reference) {
    }

    /// <inheritdoc/>
    public override string ExpectedValue => "a Vector3 array [x, y, z]";
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

    /// <summary>Wraps a literal without allocation at call sites.</summary>
    public static implicit operator DocumentVector3(Vector3 value) => new(value: value);
    /// <summary>Reads the resolved value.</summary>
    public static implicit operator Vector3(DocumentVector3 value) => value.Value;
    /// <summary>Scales the resolved value.</summary>
    public static Vector3 operator *(DocumentVector3 value, float scale) => (value.Value * scale);

    /// <inheritdoc/>
    public bool Equals(DocumentVector3? other) => EqualsCore(other: other);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is DocumentVector3 other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() => GetHashCodeCore();
}
/// <summary>A <see cref="Quaternion"/> authored as <c>[x, y, z, w]</c> or as a symbolic reference string.</summary>
[JsonConverter(typeof(DocumentQuaternionJsonConverter))]
public sealed class DocumentQuaternion : DocumentSpatialValue<Quaternion>, IEquatable<DocumentQuaternion> {
    /// <summary>Creates a literal value.</summary>
    public DocumentQuaternion(Quaternion value) : base(value: value) {
    }
    /// <summary>Creates a literal value from its components.</summary>
    public DocumentQuaternion(float x, float y, float z, float w) : this(value: new Quaternion(w: w, x: x, y: y, z: z)) {
    }

    internal DocumentQuaternion(string reference) : base(reference: reference) {
    }

    /// <inheritdoc/>
    public override string ExpectedValue => "a Quaternion array [x, y, z, w]";
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

    /// <summary>Wraps a literal without allocation at call sites.</summary>
    public static implicit operator DocumentQuaternion(Quaternion value) => new(value: value);
    /// <summary>Reads the resolved value.</summary>
    public static implicit operator Quaternion(DocumentQuaternion value) => value.Value;

    /// <inheritdoc/>
    public bool Equals(DocumentQuaternion? other) => EqualsCore(other: other);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is DocumentQuaternion other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() => GetHashCodeCore();
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
        ((reader.TokenType == JsonTokenType.String)
            ? new DocumentVector2(reference: DocumentSpatialValueJson.ReadReference(kind: "Vector2", reader: ref reader))
            : new DocumentVector2(value: new Vector2JsonConverter().Read(options: options, reader: ref reader, typeToConvert: typeof(Vector2))));
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
        ((reader.TokenType == JsonTokenType.String)
            ? new DocumentVector3(reference: DocumentSpatialValueJson.ReadReference(kind: "Vector3", reader: ref reader))
            : new DocumentVector3(value: new Vector3JsonConverter().Read(options: options, reader: ref reader, typeToConvert: typeof(Vector3))));
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
        ((reader.TokenType == JsonTokenType.String)
            ? new DocumentQuaternion(reference: DocumentSpatialValueJson.ReadReference(kind: "Quaternion", reader: ref reader))
            : new DocumentQuaternion(value: new QuaternionJsonConverter().Read(options: options, reader: ref reader, typeToConvert: typeof(Quaternion))));
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DocumentQuaternion value, JsonSerializerOptions options) {
        if (value.Reference is { } reference) {
            writer.WriteStringValue(value: reference);
        } else {
            new QuaternionJsonConverter().Write(writer: writer, value: value.Value, options: options);
        }
    }
}

/// <summary>A number authored as a JSON number or as a symbolic reference string (<c>state.&lt;row&gt;[.&lt;key&gt;]</c>),
/// resolved by its containing document from a numeric or text cell.</summary>
[JsonConverter(typeof(DocumentScalarJsonConverter))]
public sealed class DocumentScalar : DocumentSpatialValue<float>, IEquatable<DocumentScalar> {
    /// <summary>Initializes a new instance of the <see cref="DocumentScalar"/> class holding a literal.</summary>
    /// <param name="value">The literal value.</param>
    public DocumentScalar(float value) : base(value: value) {
    }

    internal DocumentScalar(string reference) : base(reference: reference) {
    }

    /// <inheritdoc/>
    public override string ExpectedValue => "a number";

    /// <summary>Wraps a literal without allocation at call sites.</summary>
    public static implicit operator DocumentScalar(float value) => new(value: value);
    /// <summary>Reads the resolved value.</summary>
    public static implicit operator float(DocumentScalar value) => value.Value;

    /// <inheritdoc/>
    public bool Equals(DocumentScalar? other) => EqualsCore(other: other);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is DocumentScalar other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() => GetHashCodeCore();
}
/// <summary>Reads and writes <see cref="DocumentScalar"/>.</summary>
public sealed class DocumentScalarJsonConverter : JsonConverter<DocumentScalar> {
    /// <inheritdoc/>
    public override DocumentScalar Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ((reader.TokenType == JsonTokenType.String)
            ? new DocumentScalar(reference: DocumentSpatialValueJson.ReadReference(kind: "number", reader: ref reader))
            : new DocumentScalar(value: reader.GetSingle()));
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DocumentScalar value, JsonSerializerOptions options) {
        if (value.Reference is { } reference) {
            writer.WriteStringValue(value: reference);
        } else {
            writer.WriteNumberValue(value: value.Value);
        }
    }
}
