using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>A shade program a document uses (a palette row's, a look case's or the world's default): the program's
/// identity, and the program once the document's table has supplied it. The identity is computed from the program's
/// canonical bytes (<see cref="KeyOf"/>), never authored, so every use of one program names one row of the world's
/// <c>shades</c> table and a program used by many creations is held, written and compiled once.</summary>
/// <remarks>A document writes a use as its key alone. Reading one gives a reference that holds only the key until the
/// world's table resolves it, which every door that turns bytes into a live world does (the world schema's
/// <c>WorldShadeTable</c>); a reference read as a whole program (an inline definition, as hand-written JSON or a
/// mutation may carry) holds it from the start. Two references are equal when their keys are, since a key is the
/// program's content identity.</remarks>
[JsonConverter(typeof(ShadeReferenceJsonConverter))]
public sealed class ShadeReference : IEquatable<ShadeReference> {
    // A definition is immutable, so its key is computed once however many uses share it.
    private static readonly Puck.Abstractions.CodeDerivedTable<SdfShadeDefinition, string> Keys = new();

    private readonly SdfShadeDefinition? m_definition;

    private ShadeReference(string key, SdfShadeDefinition? definition) {
        Key = key;
        m_definition = definition;
    }

    /// <summary>Gets the program's identity: its definition's name, a slash and the SHA-256 hex64 of its canonical
    /// bytes.</summary>
    public string Key { get; }
    /// <summary>Gets whether the program is held, rather than only named.</summary>
    [JsonIgnore]
    public bool IsResolved => (m_definition is not null);
    /// <summary>Gets the program.</summary>
    /// <exception cref="InvalidOperationException">The reference names a program its world's table has not resolved:
    /// it was read outside the doors that resolve a live world.</exception>
    [JsonIgnore]
    public SdfShadeDefinition Definition => (m_definition
        ?? throw new InvalidOperationException(message: $"Shade program '{Key}' was read before its world's shades table resolved it."));

    /// <summary>Returns a reference that holds a program.</summary>
    /// <param name="definition">The program.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static ShadeReference Of(SdfShadeDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return new(key: KeyOf(definition: definition), definition: definition);
    }
    /// <summary>Returns a reference that names a program by its key alone, for a table to resolve.</summary>
    /// <param name="key">The program's key.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="null"/> or empty.</exception>
    public static ShadeReference Named(string key) {
        ArgumentException.ThrowIfNullOrEmpty(argument: key);

        return new(key: key, definition: null);
    }
    /// <summary>Returns a program's identity, the one key every table and use names it by.</summary>
    /// <param name="definition">The program.</param>
    /// <returns>Its name, a slash and the SHA-256 hex64 of its compact canonical JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static string KeyOf(SdfShadeDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Keys.GetValue(key: definition, derive: static definition => $"{definition.Name}/{ContentPin.Compute(
            content: CanonicalJsonDocument.SerializeCompact(value: definition, jsonTypeInfo: CreationJsonContext.Document.SdfShadeDefinition)).Hex}");
    }
    /// <summary>Returns whether every shade program a palette uses is held, rather than only named: each slot's own
    /// and each of its selected cases'.</summary>
    /// <param name="palette">The palette, or <see langword="null"/> for none.</param>
    /// <returns><see langword="false"/> when a use still waits for its world's table.</returns>
    public static bool HoldsEvery(IReadOnlyList<PaletteEntryDocument>? palette) {
        foreach (var entry in (palette ?? [])) {
            if (entry.Shade is { IsResolved: false }) { return false; }
            foreach (var look in (entry.ToonSelect?.Cases ?? [])) {
                if (look?.Shade is { IsResolved: false }) { return false; }
            }
        }

        return true;
    }
    /// <summary>Returns this reference holding <paramref name="definition"/>, which must be the program its key names.</summary>
    /// <param name="definition">The program a table holds under this reference's key.</param>
    /// <param name="resolved">This reference when it already holds a program, else a resolved copy.</param>
    /// <returns><see langword="false"/> when <paramref name="definition"/>'s key is not this reference's.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public bool TryResolve(SdfShadeDefinition definition, out ShadeReference resolved) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        resolved = this;
        if (IsResolved) {
            return true;
        }
        if (!string.Equals(a: KeyOf(definition: definition), b: Key, comparisonType: StringComparison.Ordinal)) {
            return false;
        }
        resolved = new(key: Key, definition: definition);

        return true;
    }

    /// <summary>Returns a reference holding <paramref name="definition"/>, or <see langword="null"/> for none.</summary>
    /// <param name="definition">The program, or <see langword="null"/>.</param>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(parameterName: nameof(definition))]
    public static implicit operator ShadeReference?(SdfShadeDefinition? definition) => ((definition is null) ? null : Of(definition: definition));

    /// <inheritdoc/>
    public bool Equals(ShadeReference? other) => ((other is not null) && string.Equals(a: Key, b: other.Key, comparisonType: StringComparison.Ordinal));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(other: (obj as ShadeReference));
    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(obj: Key);
    /// <inheritdoc/>
    public override string ToString() => Key;
}

/// <summary>Reads a <see cref="ShadeReference"/> from its key or from a whole program, and writes its key.</summary>
public sealed class ShadeReferenceJsonConverter : JsonConverter<ShadeReference>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    public override ShadeReference? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch {
        JsonTokenType.String => ShadeReference.Named(key: (reader.GetString() is { Length: > 0 } key) ? key : throw new JsonException(message: "a shade program's key is not empty")),
        JsonTokenType.StartObject => ShadeReference.Of(definition: (JsonSerializer.Deserialize(reader: ref reader, jsonTypeInfo: CreationJsonContext.Document.SdfShadeDefinition)
            ?? throw new JsonException(message: "a shade program is a definition object or a key"))),
        _ => throw new JsonException(message: "a shade program is a key into the world's shades table, or a definition object"),
    };
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ShadeReference value, JsonSerializerOptions options) => writer.WriteStringValue(value: value.Key);
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() {
        ["anyOf"] = new JsonArray(
            new JsonObject { ["type"] = "string", ["description"] = "The key of a row of the world's shades table." },
            exportType(typeof(SdfShadeDefinition))),
    };
}
