using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>An angle in radians. It uses the scalar wire grammar but keys blend along the shorter arc.</summary>
/// <param name="Value">The literal, state binding or clock curve.</param>
[JsonConverter(typeof(BindableAngleJsonConverter))]
public readonly record struct BindableAngle(BindableScalar Value) {
    /// <summary>Whether the angle's literal, binding or keys are authorable in this world.</summary>
    /// <param name="definition">The declaring world.</param>
    /// <returns>Whether its scalar operands and clock are valid.</returns>
    public bool IsAuthorable(WorldDefinition definition) => Value.IsAuthorable(definition);
    /// <summary>Creates an angle literal.</summary>
    /// <param name="value">The angle in radians.</param>
    public static implicit operator BindableAngle(float value) => new(new BindableScalar(value));
    /// <summary>Uses an ordinary scalar literal or binding as an angle.</summary>
    /// <param name="value">The radians operand.</param>
    public static implicit operator BindableAngle(BindableScalar value) => new(value);
}

/// <summary>A pair of bindable components, or a curve whose values are pairs.</summary>
[JsonConverter(typeof(BindableVector2JsonConverter))]
public readonly record struct BindableVector2 {
    /// <summary>Gets the first component.</summary>
    public BindableScalar X { get; }
    /// <summary>Gets the second component.</summary>
    public BindableScalar Y { get; }
    /// <summary>Gets the pair curve, or null for components.</summary>
    public WorldKeys<BindableVector2>? Keys { get; }
    /// <summary>Creates a component pair.</summary>
    /// <param name="x">The first component.</param>
    /// <param name="y">The second component.</param>
    public BindableVector2(BindableScalar x, BindableScalar y) { X = x; Y = y; }
    /// <summary>Creates a keyed pair.</summary>
    /// <param name="keys">The authored curve.</param>
    public BindableVector2(WorldKeys<BindableVector2> keys) => Keys = keys;
    /// <summary>Creates a literal pair.</summary>
    /// <param name="value">The pair.</param>
    public static implicit operator BindableVector2(Vector2 value) => new(value.X, value.Y);
    /// <summary>Creates a pair from document coordinates.</summary>
    /// <param name="value">The authored pair.</param>
    public static implicit operator BindableVector2(DocumentVector2 value) => (Vector2)value;
    /// <summary>Whether every component or key is authorable in this world.</summary>
    /// <param name="definition">The declaring world.</param>
    /// <returns>Whether every operand and clock is valid.</returns>
    public bool IsAuthorable(WorldDefinition definition) => Keys is { } keys
        ? WorldValueValidation.IsAuthorable(keys, definition, static (value, world) => value.IsAuthorable(world))
        : X.IsAuthorable(definition) && Y.IsAuthorable(definition);
}

/// <summary>A vector of bindable components, or a curve whose values are vectors.</summary>
[JsonConverter(typeof(BindableVector3JsonConverter))]
public readonly record struct BindableVector3 {
    /// <summary>Whether every component or key is authorable in this world.</summary>
    /// <param name="definition">The declaring world.</param>
    /// <returns>Whether every operand and clock is valid.</returns>
    public bool IsAuthorable(WorldDefinition definition) => Keys is { } keys
        ? WorldValueValidation.IsAuthorable(keys, definition, static (value, world) => value.IsAuthorable(world))
        : X.IsAuthorable(definition) && Y.IsAuthorable(definition) && Z.IsAuthorable(definition);
    /// <summary>Gets the first component.</summary>
    public BindableScalar X { get; }
    /// <summary>Gets the second component.</summary>
    public BindableScalar Y { get; }
    /// <summary>Gets the third component.</summary>
    public BindableScalar Z { get; }
    /// <summary>Gets the vector curve, or null for components.</summary>
    public WorldKeys<BindableVector3>? Keys { get; }

    /// <summary>Creates a component vector.</summary>
    /// <param name="x">The first component.</param>
    /// <param name="y">The second component.</param>
    /// <param name="z">The third component.</param>
    public BindableVector3(BindableScalar x, BindableScalar y, BindableScalar z) { X = x; Y = y; Z = z; }
    /// <summary>Creates a keyed vector.</summary>
    /// <param name="keys">The authored curve.</param>
    public BindableVector3(WorldKeys<BindableVector3> keys) => Keys = keys;
    /// <summary>Creates a literal vector.</summary>
    /// <param name="value">The vector.</param>
    public static implicit operator BindableVector3(Vector3 value) => new(value.X, value.Y, value.Z);
    /// <summary>Creates a vector from document coordinates.</summary>
    /// <param name="value">The authored vector.</param>
    public static implicit operator BindableVector3(DocumentVector3 value) => (Vector3)value;
}

/// <summary>A direction: bindable vector components, or keys blended on the unit sphere.</summary>
[JsonConverter(typeof(BindableDirectionJsonConverter))]
public readonly record struct BindableDirection {
    /// <summary>Whether every direction key or component is authorable and its authored value is nonzero.</summary>
    /// <param name="definition">The declaring world.</param>
    /// <returns>Whether the direction can be resolved.</returns>
    public bool IsAuthorable(WorldDefinition definition) => Keys is { } keys
        ? WorldValueValidation.IsAuthorable(keys, definition, static (value, world) => value.IsAuthorable(world))
        : Value.IsAuthorable(definition) && new WorldValueResolver(definition, default).TryDirection(this, Vector3.Zero, out _);
    /// <summary>Gets the components for an unkeyed direction.</summary>
    public BindableVector3 Value { get; }
    /// <summary>Gets the direction curve, or null for components.</summary>
    public WorldKeys<BindableDirection>? Keys { get; }
    /// <summary>Creates a direction from bindable components.</summary>
    /// <param name="value">The components, normalized when resolved.</param>
    public BindableDirection(BindableVector3 value) => Value = value;
    /// <summary>Creates a direction from its bindable components.</summary>
    /// <param name="x">The first component.</param>
    /// <param name="y">The second component.</param>
    /// <param name="z">The third component.</param>
    public BindableDirection(BindableScalar x, BindableScalar y, BindableScalar z) : this(new BindableVector3(x, y, z)) { }
    /// <summary>Creates a keyed direction.</summary>
    /// <param name="keys">The authored curve.</param>
    public BindableDirection(WorldKeys<BindableDirection> keys) => Keys = keys;
    /// <summary>Creates a literal direction.</summary>
    /// <param name="value">The direction.</param>
    public static implicit operator BindableDirection(Vector3 value) => new((BindableVector3)value);
    /// <summary>Creates a direction from document coordinates.</summary>
    /// <param name="value">The authored direction.</param>
    public static implicit operator BindableDirection(DocumentVector3 value) => (Vector3)value;
}

/// <summary>The pair's array or keyed-object wire grammar.</summary>
public sealed class BindableVector2JsonConverter : JsonConverter<BindableVector2>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() {
        ["anyOf"] = new JsonArray(new JsonObject {
            ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 2, ["items"] = exportType(typeof(BindableScalar)),
        }, exportType(typeof(WorldKeys<BindableVector2>))),
    };
    /// <inheritdoc/>
    public override BindableVector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.StartObject) {
            return new BindableVector2(JsonSerializer.Deserialize<WorldKeys<BindableVector2>>(ref reader, options)!);
        }
        if (reader.TokenType != JsonTokenType.StartArray) { throw new JsonException("A vector requires two components or clock keys."); }
        reader.Read();
        var x = JsonSerializer.Deserialize<BindableScalar>(ref reader, options);
        reader.Read();
        var y = JsonSerializer.Deserialize<BindableScalar>(ref reader, options);
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) { throw new JsonException("A vector requires exactly two components."); }
        return new BindableVector2(x, y);
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindableVector2 value, JsonSerializerOptions options) {
        if (value.Keys is { } keys) { JsonSerializer.Serialize(writer, keys, options); return; }
        writer.WriteStartArray();
        JsonSerializer.Serialize(writer, value.X, options);
        JsonSerializer.Serialize(writer, value.Y, options);
        writer.WriteEndArray();
    }
}

/// <summary>The angle's scalar wire grammar.</summary>
public sealed class BindableAngleJsonConverter : JsonConverter<BindableAngle>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() { ["allOf"] = new JsonArray(exportType(typeof(BindableScalar))) };
    /// <inheritdoc/>
    public override BindableAngle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(JsonSerializer.Deserialize<BindableScalar>(ref reader, options));
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindableAngle value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.Value, options);
}

/// <summary>The vector's array or keyed-object wire grammar.</summary>
public sealed class BindableVector3JsonConverter : JsonConverter<BindableVector3>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() {
        ["anyOf"] = new JsonArray(new JsonObject {
            ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = exportType(typeof(BindableScalar)),
        }, exportType(typeof(WorldKeys<BindableVector3>))),
    };
    /// <inheritdoc/>
    public override BindableVector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.StartObject) {
            return new BindableVector3(JsonSerializer.Deserialize<WorldKeys<BindableVector3>>(ref reader, options)!);
        }
        if (reader.TokenType != JsonTokenType.StartArray) { throw new JsonException("A vector requires three components or clock keys."); }
        reader.Read();
        var x = JsonSerializer.Deserialize<BindableScalar>(ref reader, options);
        reader.Read();
        var y = JsonSerializer.Deserialize<BindableScalar>(ref reader, options);
        reader.Read();
        var z = JsonSerializer.Deserialize<BindableScalar>(ref reader, options);
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) { throw new JsonException("A vector requires exactly three components."); }
        return new BindableVector3(x, y, z);
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindableVector3 value, JsonSerializerOptions options) {
        if (value.Keys is { } keys) { JsonSerializer.Serialize(writer, keys, options); return; }
        writer.WriteStartArray();
        JsonSerializer.Serialize(writer, value.X, options);
        JsonSerializer.Serialize(writer, value.Y, options);
        JsonSerializer.Serialize(writer, value.Z, options);
        writer.WriteEndArray();
    }
}

/// <summary>The direction's component-array or keyed-object wire grammar.</summary>
public sealed class BindableDirectionJsonConverter : JsonConverter<BindableDirection>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() {
        ["anyOf"] = new JsonArray(new JsonObject {
            ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = exportType(typeof(BindableScalar)),
        }, exportType(typeof(WorldKeys<BindableDirection>))),
    };
    /// <inheritdoc/>
    public override BindableDirection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.StartObject
        ? new BindableDirection(JsonSerializer.Deserialize<WorldKeys<BindableDirection>>(ref reader, options)!)
        : new BindableDirection(JsonSerializer.Deserialize<BindableVector3>(ref reader, options));
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindableDirection value, JsonSerializerOptions options) {
        if (value.Keys is { } keys) { JsonSerializer.Serialize(writer, keys, options); }
        else { JsonSerializer.Serialize(writer, value.Value, options); }
    }
}
