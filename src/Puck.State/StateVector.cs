using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.State;

/// <summary>An immutable, normalized signed 8-bit embedding vector.</summary>
[JsonConverter(typeof(StateVectorJsonConverter))]
public sealed class StateVector : IEquatable<StateVector> {
    private readonly sbyte[] m_components;

    /// <summary>Gets the vector components as a read-only span of signed 8-bit integers.</summary>
    public ReadOnlySpan<sbyte> Components => m_components;

    /// <summary>Gets the component dimension count.</summary>
    public int Dimensions => m_components.Length;

    private StateVector(sbyte[] components) {
        m_components = components;
    }

    /// <summary>Creates a vector without validating dimensions, range, or admissibility. For internal trusted paths only.</summary>
    internal static StateVector CreateUnchecked(ReadOnlySpan<sbyte> components) =>
        new(components: components.ToArray());

    /// <summary>Tries to create a validated, admitted unit vector from the given components.</summary>
    /// <param name="components">The candidate components.</param>
    /// <param name="vector">The created vector on success; otherwise <see langword="null"/>.</param>
    /// <param name="error">The refusal reason on failure; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if creation succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryCreate(ReadOnlySpan<sbyte> components, [NotNullWhen(true)] out StateVector? vector, [NotNullWhen(false)] out string? error) {
        if ((components.Length < StateCapacity.MinVectorDimensions) || (components.Length > StateCapacity.MaxVectorDimensions)) {
            vector = null;
            error = $"Vector dimensions {components.Length} outside admitted bounds [{StateCapacity.MinVectorDimensions}, {StateCapacity.MaxVectorDimensions}].";

            return false;
        }

        for (var i = 0; i < components.Length; i++) {
            if (components[i] == -128) {
                vector = null;
                error = "Vector component -128 is not permitted.";

                return false;
            }
        }

        if (!SignedByteVectorFunctions.IsUnitAdmissible(components: components)) {
            vector = null;
            error = "Vector components must be non-zero and near unit length.";

            return false;
        }

        vector = new StateVector(components: components.ToArray());
        error = null;

        return true;
    }

    /// <summary>Encodes the vector components as an unpadded base64url string.</summary>
    public string ToBase64Url() {
        var byteSpan = MemoryMarshal.Cast<sbyte, byte>(m_components);

        return Base64Url.EncodeToString(source: byteSpan);
    }

    /// <summary>Tries to parse a base64url string into an admitted vector of the expected dimension count.</summary>
    /// <param name="text">The base64url text.</param>
    /// <param name="dimensions">The expected dimension count.</param>
    /// <param name="vector">The parsed vector on success; otherwise <see langword="null"/>.</param>
    /// <param name="error">The error reason on failure; otherwise <see langword="null"/>.</param>
    public static bool TryParseBase64Url(string text, int dimensions, [NotNullWhen(true)] out StateVector? vector, [NotNullWhen(false)] out string? error) {
        if (string.IsNullOrEmpty(text)) {
            vector = null;
            error = "Base64url vector string cannot be empty.";

            return false;
        }

        var byteBuffer = new byte[dimensions];

        if (Base64Url.DecodeFromChars(source: text.AsSpan(), destination: byteBuffer, charsConsumed: out var consumed, bytesWritten: out var written, isFinalBlock: true) != System.Buffers.OperationStatus.Done) {
            vector = null;
            error = "Failed to decode base64url vector string.";

            return false;
        }

        if ((written != dimensions) || (consumed != text.Length)) {
            vector = null;
            error = $"Decoded vector length {written} does not match expected dimensions {dimensions}.";

            return false;
        }

        var sbyteBuffer = MemoryMarshal.Cast<byte, sbyte>(byteBuffer);

        return TryCreate(components: sbyteBuffer, vector: out vector, error: out error);
    }

    /// <summary>Tries to parse an unpadded base64url string into an admitted vector.</summary>
    /// <param name="text">The base64url text.</param>
    /// <param name="vector">The parsed vector on success; otherwise <see langword="null"/>.</param>
    /// <param name="error">The error reason on failure; otherwise <see langword="null"/>.</param>
    public static bool TryParseBase64Url(string text, [NotNullWhen(true)] out StateVector? vector, [NotNullWhen(false)] out string? error) {
        if (string.IsNullOrEmpty(text)) {
            vector = null;
            error = "Base64url vector string cannot be empty.";

            return false;
        }

        // Maximum dimension is 1024 bytes
        Span<byte> byteBuffer = stackalloc byte[StateCapacity.MaxVectorDimensions];

        if (Base64Url.DecodeFromChars(source: text.AsSpan(), destination: byteBuffer, charsConsumed: out var consumed, bytesWritten: out var written, isFinalBlock: true) != System.Buffers.OperationStatus.Done) {
            vector = null;
            error = "Failed to decode base64url vector string.";

            return false;
        }

        if (consumed != text.Length) {
            vector = null;
            error = "Base64url vector string had unconsumed trailing characters.";

            return false;
        }

        var sbyteSpan = MemoryMarshal.Cast<byte, sbyte>(byteBuffer[..written]);

        return TryCreate(components: sbyteSpan, vector: out vector, error: out error);
    }

    /// <summary>Calculates a 32-bit FNV-1a digest over the vector components for compact human display.</summary>
    public uint ComputeDigest() {
        var bytes = MemoryMarshal.Cast<sbyte, byte>(m_components);
        var hash = Fnv1aHash.Compute(values: bytes);

        return (uint)(hash ^ (hash >> 32));
    }

    /// <inheritdoc/>
    public bool Equals(StateVector? other) {
        if (other is null) {
            return false;
        }

        if (ReferenceEquals(this, other)) {
            return true;
        }

        return m_components.AsSpan().SequenceEqual(other.m_components.AsSpan());
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        (obj is StateVector other) && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() {
        var bytes = MemoryMarshal.Cast<sbyte, byte>(m_components);

        return unchecked((int)Fnv1aHash.Compute(values: bytes));
    }

    /// <inheritdoc/>
    public override string ToString() => ToBase64Url();

    public static bool operator ==(StateVector? left, StateVector? right) =>
        left?.Equals(right) ?? (right is null);

    public static bool operator !=(StateVector? left, StateVector? right) =>
        !(left == right);
}

/// <summary>JSON converter for <see cref="StateVector"/>, serializing as unpadded base64url.</summary>
public sealed class StateVectorJsonConverter : JsonConverter<StateVector> {
    /// <inheritdoc/>
    public override StateVector? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.Null) {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String) {
            throw new JsonException("Expected string token for base64url StateVector.");
        }

        var text = reader.GetString();

        if (text is null) {
            return null;
        }

        if (!StateVector.TryParseBase64Url(text: text, vector: out var vector, error: out var error)) {
            throw new JsonException($"Invalid StateVector base64url: {error}");
        }

        return vector;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, StateVector value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: value.ToBase64Url());
    }
}
