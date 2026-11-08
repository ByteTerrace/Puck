using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Assets.Documents;

/// <summary>A state-resolvable document value whose literal form is a spatial JSON array.</summary>
public interface IDocumentSpatialValue : IDocumentStateValue;
/// <summary>A state-resolvable document value wrapping a <typeparamref name="TValue"/> literal.</summary>
public abstract class DocumentSpatialValue<TValue> : IDocumentSpatialValue
    where TValue : struct, IEquatable<TValue> {
    private readonly bool m_isResolved;
    private readonly TValue m_value;
    private readonly LiveRead? m_live;

    private protected DocumentSpatialValue(TValue value) {
        m_isResolved = true;
        m_value = value;
    }
    private protected DocumentSpatialValue(string reference) => Reference = reference;
    private protected DocumentSpatialValue(string? reference, bool resolved, TValue value, LiveRead? live) {
        Reference = reference;
        m_isResolved = resolved;
        m_value = value;
        m_live = live;
    }

    /// <inheritdoc/>
    public abstract string ExpectedValue { get; }
    /// <inheritdoc/>
    public string? Reference { get; }
    /// <inheritdoc/>
    public object? Binder => m_live?.Binder;
    /// <summary>Gets the resolved value, or the live one a <see cref="Bound"/> copy reads.</summary>
    /// <exception cref="InvalidOperationException">The value is a reference its containing document has not resolved,
    /// or a bound copy whose cell holds no admissible value.</exception>
    public TValue Value => ((m_live is { } live)
        ? live.Read(reference: Reference)
        : (m_isResolved
            ? m_value
            : throw new InvalidOperationException(message: $"document spatial reference '{Reference}' has not been resolved by its containing document.")));

    // Builds this concrete type over the given state; the one constructor every copy goes through.
    private protected abstract DocumentSpatialValue<TValue> Copy(string? reference, bool resolved, TValue value, LiveRead? live);
    /// <inheritdoc/>
    public IDocumentStateValue Bound(object binder, Func<string?> read) {
        ArgumentNullException.ThrowIfNull(argument: binder);
        ArgumentNullException.ThrowIfNull(argument: read);

        return Copy(live: new LiveRead(binder: binder, read: read), reference: Reference, resolved: false, value: default);
    }
    /// <inheritdoc/>
    public bool TryResolved(string text, bool detach, out IDocumentStateValue resolved, out string reason) {
        if (!TryParse(reason: out reason, text: text, value: out var value)) {
            resolved = this;

            return false;
        }

        resolved = Copy(live: null, reference: (detach ? null : Reference), resolved: true, value: value);

        return true;
    }
    private static bool TryParse(string text, out TValue value, out string reason) {
        try {
            value = JsonSerializer.Deserialize<TValue>(
                json: text,
                options: DocumentJsonOptions.Shared
            );
            reason = string.Empty;
            return true;
        } catch (Exception exception) when ((exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)) {
            value = default;
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");
            return false;
        }
    }

    /// <summary>The live read a bound copy holds: the store's owner, the read, and the last text it parsed, so a read
    /// whose text has not moved parses nothing. The memo is one reference swapped whole, so a reader on another
    /// thread sees a text and its value together.</summary>
    private protected sealed class LiveRead(object binder, Func<string?> read) {
        private Memo? m_memo;

        public object Binder => binder;

        public TValue Read(string? reference) {
            var text = read();
            var memo = Volatile.Read(location: ref m_memo);

            if ((memo is not null) && string.Equals(a: memo.Text, b: text, comparisonType: StringComparison.Ordinal)) {
                return memo.Value;
            }
            if ((text is null) || !TryParse(reason: out _, text: text, value: out var value)) {
                throw new InvalidOperationException(message: $"document spatial reference '{reference}' has no admissible live value.");
            }

            Volatile.Write(location: ref m_memo, value: new Memo(Text: text, Value: value));

            return value;
        }

        private sealed record Memo(string Text, TValue Value);
    }

    private protected bool EqualsCore(DocumentSpatialValue<TValue>? other) => ((other is not null) && ((Reference is { } reference)
        ? string.Equals(
            a: reference,
            b: other.Reference,
            comparisonType: StringComparison.Ordinal
        )
        : ((other.Reference is null) && m_value.Equals(other: other.m_value))));
    private protected int GetHashCodeCore() => ((Reference is { } reference)
        ? StringComparer.Ordinal.GetHashCode(obj: reference)
        : m_value.GetHashCode()
    );
}
/// <summary>A <see cref="Vector2"/> authored as <c>[x, y]</c> or as a symbolic reference string.</summary>
[JsonConverter(typeof(DocumentVector2JsonConverter))]
public sealed class DocumentVector2 : DocumentSpatialValue<Vector2>, IEquatable<DocumentVector2> {
    /// <summary>Creates a literal value.</summary>
    public DocumentVector2(Vector2 value) : base(value: value) {
    }

    internal DocumentVector2(string reference) : base(reference: reference) {
    }

    private DocumentVector2(string? reference, bool resolved, Vector2 value, LiveRead? live) : base(live: live, reference: reference, resolved: resolved, value: value) {
    }

    /// <inheritdoc/>
    public override string ExpectedValue => "a Vector2 array [x, y]";

    private protected override DocumentSpatialValue<Vector2> Copy(string? reference, bool resolved, Vector2 value, LiveRead? live) => new DocumentVector2(live: live, reference: reference, resolved: resolved, value: value);
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
    public DocumentVector3(float x, float y, float z) : this(value: new Vector3(
        x: x,
        y: y,
        z: z
    )) {
    }

    internal DocumentVector3(string reference) : base(reference: reference) {
    }

    private DocumentVector3(string? reference, bool resolved, Vector3 value, LiveRead? live) : base(live: live, reference: reference, resolved: resolved, value: value) {
    }

    /// <inheritdoc/>
    public override string ExpectedValue => "a Vector3 array [x, y, z]";

    private protected override DocumentSpatialValue<Vector3> Copy(string? reference, bool resolved, Vector3 value, LiveRead? live) => new DocumentVector3(live: live, reference: reference, resolved: resolved, value: value);
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
    public DocumentQuaternion(float x, float y, float z, float w) : this(value: new Quaternion(
        w: w,
        x: x,
        y: y,
        z: z
    )) {
    }

    internal DocumentQuaternion(string reference) : base(reference: reference) {
    }

    private DocumentQuaternion(string? reference, bool resolved, Quaternion value, LiveRead? live) : base(live: live, reference: reference, resolved: resolved, value: value) {
    }

    /// <inheritdoc/>
    public override string ExpectedValue => "a Quaternion array [x, y, z, w]";

    private protected override DocumentSpatialValue<Quaternion> Copy(string? reference, bool resolved, Quaternion value, LiveRead? live) => new DocumentQuaternion(live: live, reference: reference, resolved: resolved, value: value);
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
    /// <summary>Builds the schema every document spatial value shares: the literal form, or a
    /// <c>state.&lt;row&gt;[.&lt;key&gt;]</c> reference string the containing document resolves.</summary>
    public static JsonObject LiteralOrReference(JsonObject literal) => new() {
        ["anyOf"] = new JsonArray(
        literal,
        new JsonObject {
            ["type"] = "string",
            ["minLength"] = 1,
            ["description"] = "A state.<row>[.<key>] reference the containing document resolves from a cell.",
        }
    ),
    };
    public static string ReadReference(ref Utf8JsonReader reader, string kind) {
        var reference = reader.GetString();

        if (string.IsNullOrWhiteSpace(value: reference)) {
            throw new JsonException(message: $"a {kind} reference must be a non-empty string.");
        }
        return reference;
    }
    public static T ReadValueOrReference<T, TValue>(
        ref Utf8JsonReader reader,
        string kind,
        JsonSerializerOptions options,
        JsonConverter<TValue> converter,
        Func<string, T> fromReference,
        Func<TValue, T> fromValue
    ) where TValue : struct {
        if (reader.TokenType == JsonTokenType.String) {
            return fromReference(ReadReference(kind: kind, reader: ref reader));
        }

        return fromValue(converter.Read(
            options: options,
            reader: ref reader,
            typeToConvert: typeof(TValue)
        ));
    }
    public static void WriteValueOrReference<TValue>(
        Utf8JsonWriter writer,
        DocumentSpatialValue<TValue> value,
        JsonSerializerOptions options,
        Action<Utf8JsonWriter, TValue, JsonSerializerOptions> writeValue
    ) where TValue : struct, IEquatable<TValue> {
        if (value.Reference is { } reference) {
            writer.WriteStringValue(value: reference);
        } else {
            writeValue(writer, value.Value, options);
        }
    }
}

/// <summary>Reads and writes <see cref="DocumentVector2"/>.</summary>
public sealed class DocumentVector2JsonConverter : JsonConverter<DocumentVector2>, IJsonSchemaNodeConverter {
    private static readonly Vector2JsonConverter Vector2Converter = new();

    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => DocumentSpatialValueJson.LiteralOrReference(literal: FixedArityNumberArraySchema.Build(arity: 2));
    /// <inheritdoc/>
    public override DocumentVector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DocumentSpatialValueJson.ReadValueOrReference(
            converter: Vector2Converter,
            fromReference: static r => new DocumentVector2(reference: r),
            fromValue: static v => new DocumentVector2(value: v),
            kind: "Vector2",
            options: options,
            reader: ref reader
        );
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DocumentVector2 value, JsonSerializerOptions options) =>
        DocumentSpatialValueJson.WriteValueOrReference(
            options: options,
            value: value,
            writeValue: Vector2Converter.Write,
            writer: writer
        );
}
/// <summary>Reads and writes <see cref="DocumentVector3"/>.</summary>
public sealed class DocumentVector3JsonConverter : JsonConverter<DocumentVector3>, IJsonSchemaNodeConverter {
    private static readonly Vector3JsonConverter Vector3Converter = new();

    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => DocumentSpatialValueJson.LiteralOrReference(literal: FixedArityNumberArraySchema.Build(arity: 3));
    /// <inheritdoc/>
    public override DocumentVector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DocumentSpatialValueJson.ReadValueOrReference(
            converter: Vector3Converter,
            fromReference: static r => new DocumentVector3(reference: r),
            fromValue: static v => new DocumentVector3(value: v),
            kind: "Vector3",
            options: options,
            reader: ref reader
        );
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DocumentVector3 value, JsonSerializerOptions options) =>
        DocumentSpatialValueJson.WriteValueOrReference(
            options: options,
            value: value,
            writeValue: Vector3Converter.Write,
            writer: writer
        );
}
/// <summary>Reads and writes <see cref="DocumentQuaternion"/>.</summary>
public sealed class DocumentQuaternionJsonConverter : JsonConverter<DocumentQuaternion>, IJsonSchemaNodeConverter {
    private static readonly QuaternionJsonConverter QuaternionConverter = new();

    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => DocumentSpatialValueJson.LiteralOrReference(literal: FixedArityNumberArraySchema.Build(arity: 4));
    /// <inheritdoc/>
    public override DocumentQuaternion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DocumentSpatialValueJson.ReadValueOrReference(
            converter: QuaternionConverter,
            fromReference: static r => new DocumentQuaternion(reference: r),
            fromValue: static v => new DocumentQuaternion(value: v),
            kind: "Quaternion",
            options: options,
            reader: ref reader
        );
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DocumentQuaternion value, JsonSerializerOptions options) =>
        DocumentSpatialValueJson.WriteValueOrReference(
            options: options,
            value: value,
            writeValue: QuaternionConverter.Write,
            writer: writer
        );
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

    private DocumentScalar(string? reference, bool resolved, float value, LiveRead? live) : base(live: live, reference: reference, resolved: resolved, value: value) {
    }

    /// <inheritdoc/>
    public override string ExpectedValue => "a number";

    private protected override DocumentSpatialValue<float> Copy(string? reference, bool resolved, float value, LiveRead? live) => new DocumentScalar(live: live, reference: reference, resolved: resolved, value: value);

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
public sealed class DocumentScalarJsonConverter : JsonConverter<DocumentScalar>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => DocumentSpatialValueJson.LiteralOrReference(literal: new JsonObject { ["type"] = "number" });
    /// <inheritdoc/>
    public override DocumentScalar Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ((reader.TokenType == JsonTokenType.String)
            ? new DocumentScalar(reference: DocumentSpatialValueJson.ReadReference(
                kind: "number",
                reader: ref reader
            ))
            : new DocumentScalar(value: reader.GetSingle())
        );
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, DocumentScalar value, JsonSerializerOptions options) {
        if (value.Reference is { } reference) {
            writer.WriteStringValue(value: reference);
        } else {
            writer.WriteNumberValue(value: value.Value);
        }
    }
}
