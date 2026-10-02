using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;
using Puck.World.Authoring;

namespace Puck.World;

/// <summary>
/// One parsed <c>state.&lt;row&gt;[.&lt;key&gt;][.$target]</c> binding — the state arm of the grammar every bindable
/// document token speaks (<see cref="BindableColor"/>, <see cref="BindableScalar"/>, <see cref="WorldColor"/>, a HUD
/// element's binding). <see cref="TryParse"/> is the one parse of that arm; a bindable stores its parsed binding
/// rather than re-parsing it on every read.
/// </summary>
/// <param name="Row">The bound row's name.</param>
/// <param name="Key">The bound cell key, or <see langword="null"/> for the row's own slot cell.</param>
/// <param name="Target">Whether the token carried the trailing <c>.$target</c> facet: a read of the cell's stored
/// truth rather than its eased value, which differ only while a cell carrying an easing trait is still moving.</param>
public readonly record struct StateBinding(string Row, string? Key, bool Target) {
    /// <summary>The cell-key token a presentation state reference spells a body's own index with:
    /// <c>state.&lt;row&gt;.$body</c> reads the cell keyed by the reading body's 0-based index. The token is the whole
    /// key, never a substring of one.</summary>
    public const string BodyKey = "$body";

    /// <summary>Resolves a reference's cell key against the body reading it: a key that is exactly
    /// <see cref="BodyKey"/> is the token and names the body's decimal index; any other key, including one that merely
    /// contains the token's text, names the cell spelled that way.</summary>
    /// <param name="key">The parsed key, or <see langword="null"/> for the slot cell.</param>
    /// <param name="bodyIndex">The reading body's 0-based index, or negative for no body.</param>
    /// <param name="resolved">The key to read.</param>
    /// <returns><see langword="false"/> when the key is the token but no body is reading.</returns>
    public static bool TryResolveBodyKey(string? key, int bodyIndex, out string? resolved) {
        resolved = key;

        if (!string.Equals(
            a: key,
            b: BodyKey,
            comparisonType: StringComparison.Ordinal
        )) {
            return true;
        }

        if (bodyIndex < 0) {
            return false;
        }

        resolved = bodyIndex.ToString(provider: CultureInfo.InvariantCulture);

        return true;
    }
    /// <summary>Parses the state arm of the binding grammar: <c>state.&lt;row&gt;</c> (the row's slot cell) or
    /// <c>state.&lt;row&gt;.&lt;key&gt;</c>, either with an optional trailing <c>.$target</c>. Any other token —
    /// including a literal — is not a state binding.</summary>
    /// <param name="token">The candidate token.</param>
    /// <param name="binding">The parsed binding, or <see langword="default"/> when the token is not one.</param>
    /// <returns><see langword="true"/> when <paramref name="token"/> is a well-formed state binding.</returns>
    public static bool TryParse(string? token, out StateBinding binding) {
        if (
            string.IsNullOrEmpty(value: token) ||
            !HudBindingVocabulary.TryParse(
            binding: out var parsed,
            token: token
        ) ||
            (parsed.Kind != HudBindingKind.StateNamed)
        ) {
            binding = default;

            return false;
        }

        binding = new StateBinding(
            Key: parsed.StateCellKey,
            Row: parsed.StateName!,
            Target: parsed.Target
        );

        return true;
    }
    /// <summary>Returns the parsed binding a token carries, or <see langword="null"/> when it carries none.</summary>
    /// <param name="token">The candidate token.</param>
    /// <returns>The parsed binding, or <see langword="null"/>.</returns>
    public static StateBinding? Parse(string? token) => (TryParse(
        binding: out var binding,
        token: token
    )
        ? binding
        : null
    );
}
/// <summary>
/// A color authored as a <c>#RRGGBB</c>/<c>#RRGGBBAA</c> hex literal, a <c>state.&lt;row&gt;[.&lt;key&gt;][.$target]</c>
/// binding naming a Text cell that holds one, or keys on a clock (<see cref="Keys"/>) — the theme/marker/render
/// vocabulary's shared color grammar. A literal or binding parses and serializes as a plain JSON string, keys as
/// <see cref="WorldKeyTrackJson"/>'s object. The token is parsed once, into <see cref="Literal"/> or
/// <see cref="State"/>; a presentation reads the binding and the keys through the client's state mirror, the one
/// binding path, which reads a binding eased by default and as stored truth with <c>.$target</c>, and blends keys in
/// linear light (<see cref="WorldKeyResolver.Color"/>). This carries alpha — a translucent theme surface bakes it into
/// the token — while an opaque consumer (the sky and lighting fields) drops it.
/// </summary>
[JsonConverter(typeof(BindableColorJsonConverter))]
public readonly record struct BindableColor {
    /// <summary>The refusal every bindable color field shares.</summary>
    public const string Grammar = "must be #RRGGBB, #RRGGBBAA, state.<row>[.<key>] naming a Text cell that holds one, or keys on a clock whose values are #RRGGBB or #RRGGBBAA";

    /// <summary>Initializes a new instance of the <see cref="BindableColor"/> struct as a literal or a binding.</summary>
    /// <param name="Raw">The authored token, verbatim.</param>
    /// <exception cref="ArgumentNullException"><paramref name="Raw"/> is <see langword="null"/>.</exception>
    public BindableColor(string Raw) {
        ArgumentNullException.ThrowIfNull(argument: Raw);

        this.Raw = Raw;
        Keys = null;
        Literal = (HexColor.TryParseRgba(
            rgba: out var literal,
            value: Raw
        )
            ? literal
            : null
        );
        State = StateBinding.Parse(token: Raw);
    }
    /// <summary>Initializes a new instance of the <see cref="BindableColor"/> struct as keys on a clock.</summary>
    /// <param name="keys">The keys, each a literal.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <see langword="null"/>.</exception>
    public BindableColor(WorldKeyTrack<BindableColor> keys) {
        ArgumentNullException.ThrowIfNull(argument: keys);

        Keys = keys;
        Literal = null;
        Raw = null;
        State = null;
    }

    /// <summary>Gets the authored token, verbatim, or <see langword="null"/> when the color is keyed.</summary>
    public string? Raw { get; }
    /// <summary>Gets the parsed hex literal, or <see langword="null"/> when the token is a binding, keyed or
    /// malformed.</summary>
    public Vector4? Literal { get; }
    /// <summary>Gets the parsed state binding, or <see langword="null"/> when the token is a literal, keyed or
    /// malformed.</summary>
    public StateBinding? State { get; }
    /// <summary>Gets the keys on a clock, or <see langword="null"/> when the color is a literal or a binding.</summary>
    public WorldKeyTrack<BindableColor>? Keys { get; }

    /// <summary>Returns whether this color is admissible against a document: a hex literal, a state binding naming a
    /// declared Text cell whose text is one, or keys whose every value is a hex literal (the keys' clock and times are
    /// judged with every keyed value, <see cref="WorldDefinitionValidator"/>).</summary>
    /// <param name="definition">The document to check the binding half against.</param>
    /// <returns><see langword="true"/> when the color is admissible.</returns>
    public bool IsAuthorable(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (Keys is { } keys) {
            foreach (var key in keys.Keys) {
                if (!key.Value.Literal.HasValue) {
                    return false;
                }
            }

            return true;
        }

        if (State is not { } binding) {
            return Literal.HasValue;
        }

        return (
            WorldStateReader.TryRead(
            definition: definition,
            engineTick: 0UL,
            key: binding.Key,
            rawValue: out _,
            row: out var stateRow,
            rowName: binding.Row,
            text: out var text,
            tick: 0UL
        ) &&
            (stateRow.Kind == CellKind.Text) &&
            HexColor.TryParseRgba(
            rgba: out _,
            value: text
        )
        );
    }
    /// <inheritdoc/>
    public override string ToString() => (Raw ?? (Keys?.ToString() ?? string.Empty));
}
/// <summary>
/// A scalar authored as a finite number literal, a <c>state.&lt;row&gt;[.&lt;key&gt;][.$target]</c> binding naming a
/// Fixed or Int cell whose live value drives it, or keys on a clock (<see cref="Keys"/>) — the numeric twin of
/// <see cref="BindableColor"/>, sharing its binding grammar (<see cref="StateBinding"/>) and its one read path, the
/// client's state mirror. Parses as a JSON number (literal), string (binding) or object (keys), and keys blend
/// linearly.
/// </summary>
[JsonConverter(typeof(BindableScalarJsonConverter))]
public readonly record struct BindableScalar {
    /// <summary>The refusal every bindable scalar field shares.</summary>
    public const string Grammar = "must be a finite number, state.<row>[.<key>] naming a Fixed or Int cell, or keys on a clock whose values are finite numbers";

    /// <summary>Initializes a new instance of the <see cref="BindableScalar"/> struct as a literal.</summary>
    /// <param name="literal">The authored value.</param>
    public BindableScalar(float literal) {
        Binding = null;
        Keys = null;
        Literal = literal;
        State = null;
    }
    /// <summary>Initializes a new instance of the <see cref="BindableScalar"/> struct as a binding.</summary>
    /// <param name="binding">The authored binding token.</param>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
    public BindableScalar(string binding) {
        ArgumentNullException.ThrowIfNull(argument: binding);

        Binding = binding;
        Keys = null;
        Literal = null;
        State = StateBinding.Parse(token: binding);
    }
    /// <summary>Initializes a new instance of the <see cref="BindableScalar"/> struct as keys on a clock.</summary>
    /// <param name="keys">The keys.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <see langword="null"/>.</exception>
    public BindableScalar(WorldKeyTrack<float> keys) {
        ArgumentNullException.ThrowIfNull(argument: keys);

        Binding = null;
        Keys = keys;
        Literal = null;
        State = null;
    }

    /// <summary>Gets the authored binding token, or <see langword="null"/> when this is a literal or keyed.</summary>
    public string? Binding { get; }
    /// <summary>Gets the authored literal value, or <see langword="null"/> when this is a binding or keyed.</summary>
    public float? Literal { get; }
    /// <summary>Gets the parsed state binding, or <see langword="null"/> when this is a literal, keyed, or the token is
    /// malformed.</summary>
    public StateBinding? State { get; }
    /// <summary>Gets the keys on a clock, or <see langword="null"/> when this is a literal or a binding.</summary>
    public WorldKeyTrack<float>? Keys { get; }

    /// <summary>Converts a literal into a bindable scalar.</summary>
    /// <param name="literal">The literal.</param>
    public static implicit operator BindableScalar(float literal) => new(literal: literal);

    /// <summary>Returns the values the document authors for this scalar: its literal, or every key's value; none for a
    /// binding, whose values the validator cannot know.</summary>
    /// <returns>The authored values.</returns>
    public IEnumerable<float> AuthoredValues() {
        if (Literal is { } literal) {
            yield return literal;
        }

        if (Keys is { } keys) {
            foreach (var key in keys.Keys) {
                yield return key.Value;
            }
        }
    }
    /// <summary>Returns whether this scalar is admissible against a document: a finite literal, a state binding naming
    /// a declared Fixed or Int cell, or keys whose every value is finite.</summary>
    /// <param name="definition">The document to check the binding half against.</param>
    /// <returns><see langword="true"/> when the scalar is admissible.</returns>
    public bool IsAuthorable(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (Keys is { } keys) {
            foreach (var key in keys.Keys) {
                if (!float.IsFinite(f: key.Value)) {
                    return false;
                }
            }

            return true;
        }

        if (Binding is null) {
            return (
                (Literal is { } literal) &&
                float.IsFinite(f: literal)
            );
        }

        return (
            (State is { } binding) &&
            WorldStateReader.TryRead(
            definition: definition,
            engineTick: 0UL,
            key: binding.Key,
            rawValue: out _,
            row: out var stateRow,
            rowName: binding.Row,
            text: out _,
            tick: 0UL
        ) &&
            (stateRow.Kind is CellKind.Fixed or CellKind.Int)
        );
    }
    /// <inheritdoc/>
    public override string ToString() => (Binding ?? (Keys?.ToString() ?? (Literal?.ToString(provider: CultureInfo.InvariantCulture) ?? string.Empty)));
}
/// <summary>
/// An angle in radians authored as a finite number literal, a <c>state.&lt;row&gt;[.&lt;key&gt;][.$target]</c>
/// binding naming a Fixed or Int cell that holds radians, or keys on a clock, whose values blend
/// along the shorter arc across a whole turn (<see cref="WorldKeyResolver.Angle"/>). A <c>.puck</c> source writes it in
/// degrees (<c>0.53deg</c>), which the transpiler converts. Its wire form is <see cref="BindableScalar"/>'s.
/// </summary>
[JsonConverter(typeof(BindableAngleJsonConverter))]
public readonly record struct BindableAngle {
    /// <summary>The refusal every bindable angle field shares.</summary>
    public const string Grammar = "must be a finite angle in radians, state.<row>[.<key>] naming a Fixed or Int cell, or keys on a clock whose values are finite angles";

    /// <summary>Initializes a new instance of the <see cref="BindableAngle"/> struct over its scalar form.</summary>
    /// <param name="value">The angle's literal, binding or keys, in radians.</param>
    public BindableAngle(BindableScalar value) => Value = value;

    /// <summary>Gets the angle's literal, binding or keys, in radians.</summary>
    public BindableScalar Value { get; }

    /// <summary>Converts a literal angle, in radians, into a bindable angle.</summary>
    /// <param name="radians">The angle, in radians.</param>
    public static implicit operator BindableAngle(float radians) => new(value: new BindableScalar(literal: radians));
    /// <summary>Converts a scalar form, read in radians, into a bindable angle.</summary>
    /// <param name="value">The literal, binding or keys, in radians.</param>
    public static implicit operator BindableAngle(BindableScalar value) => new(value: value);

    /// <summary>Returns whether this angle is admissible against a document, as its scalar form is.</summary>
    /// <param name="definition">The document to check the binding half against.</param>
    /// <returns><see langword="true"/> when the angle is admissible.</returns>
    public bool IsAuthorable(WorldDefinition definition) => Value.IsAuthorable(definition: definition);
    /// <inheritdoc/>
    public override string ToString() => Value.ToString();
}
/// <summary>
/// A direction authored as a nonzero <c>[x, y, z]</c> literal of any length, or keys on a clock
/// (<see cref="Keys"/>), whose values blend along the great circle between their unit vectors
/// (<see cref="WorldKeyResolver.Direction"/>). A direction binds no state row.
/// </summary>
[JsonConverter(typeof(BindableDirectionJsonConverter))]
public readonly record struct BindableDirection {
    /// <summary>The refusal every bindable direction field shares.</summary>
    public const string Grammar = "must be a nonzero [x, y, z], or keys on a clock whose values are nonzero [x, y, z]";

    /// <summary>Initializes a new instance of the <see cref="BindableDirection"/> struct as a literal.</summary>
    /// <param name="literal">The direction, any nonzero length.</param>
    public BindableDirection(Vector3 literal) {
        Keys = null;
        Literal = literal;
    }
    /// <summary>Initializes a new instance of the <see cref="BindableDirection"/> struct as keys on a clock.</summary>
    /// <param name="keys">The keys.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <see langword="null"/>.</exception>
    public BindableDirection(WorldKeyTrack<Vector3> keys) {
        ArgumentNullException.ThrowIfNull(argument: keys);

        Keys = keys;
        Literal = null;
    }

    /// <summary>Gets the literal direction, or <see langword="null"/> when it is keyed.</summary>
    public Vector3? Literal { get; }
    /// <summary>Gets the keys on a clock, or <see langword="null"/> when it is a literal.</summary>
    public WorldKeyTrack<Vector3>? Keys { get; }

    /// <summary>Converts a literal direction into a bindable direction.</summary>
    /// <param name="literal">The direction, any nonzero length.</param>
    public static implicit operator BindableDirection(Vector3 literal) => new(literal: literal);

    /// <summary>Returns the directions the document authors: its literal, or every key's value.</summary>
    /// <returns>The authored directions.</returns>
    public IEnumerable<Vector3> AuthoredValues() {
        if (Literal is { } literal) {
            yield return literal;
        }

        if (Keys is { } keys) {
            foreach (var key in keys.Keys) {
                yield return key.Value;
            }
        }
    }
    /// <inheritdoc/>
    public override string ToString() => (Keys?.ToString() ?? ((Literal is { } literal)
        ? $"[{literal.X.ToString(provider: CultureInfo.InvariantCulture)}, {literal.Y.ToString(provider: CultureInfo.InvariantCulture)}, {literal.Z.ToString(provider: CultureInfo.InvariantCulture)}]"
        : string.Empty));
}
/// <summary>
/// A two-component vector authored as an <c>[x, y]</c> literal, or keys on a clock (<see cref="Keys"/>), whose values
/// blend linearly (<see cref="WorldKeyResolver.Vector(WorldKeyTrack{Vector2}, double, double)"/>). It binds no state row.
/// </summary>
[JsonConverter(typeof(BindableVector2JsonConverter))]
public readonly record struct BindableVector2 {
    /// <summary>The refusal every bindable two-component vector field shares.</summary>
    public const string Grammar = "must be a finite [x, y], or keys on a clock whose values are finite [x, y]";

    /// <summary>Initializes a new instance of the <see cref="BindableVector2"/> struct as a literal.</summary>
    /// <param name="literal">The vector.</param>
    public BindableVector2(Vector2 literal) {
        Keys = null;
        Literal = literal;
    }
    /// <summary>Initializes a new instance of the <see cref="BindableVector2"/> struct as keys on a clock.</summary>
    /// <param name="keys">The keys.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <see langword="null"/>.</exception>
    public BindableVector2(WorldKeyTrack<Vector2> keys) {
        ArgumentNullException.ThrowIfNull(argument: keys);

        Keys = keys;
        Literal = null;
    }

    /// <summary>Gets the literal vector, or <see langword="null"/> when it is keyed.</summary>
    public Vector2? Literal { get; }
    /// <summary>Gets the keys on a clock, or <see langword="null"/> when it is a literal.</summary>
    public WorldKeyTrack<Vector2>? Keys { get; }

    /// <summary>Converts a literal vector into a bindable vector.</summary>
    /// <param name="literal">The vector.</param>
    public static implicit operator BindableVector2(Vector2 literal) => new(literal: literal);

    /// <summary>Returns the vectors the document authors: its literal, or every key's value.</summary>
    /// <returns>The authored vectors.</returns>
    public IEnumerable<Vector2> AuthoredValues() {
        if (Literal is { } literal) {
            yield return literal;
        }

        if (Keys is { } keys) {
            foreach (var key in keys.Keys) {
                yield return key.Value;
            }
        }
    }
    /// <inheritdoc/>
    public override string ToString() => (Keys?.ToString() ?? ((Literal is { } literal)
        ? $"[{literal.X.ToString(provider: CultureInfo.InvariantCulture)}, {literal.Y.ToString(provider: CultureInfo.InvariantCulture)}]"
        : string.Empty));
}
/// <summary>
/// A three-component vector authored as an <c>[x, y, z]</c> literal, or keys on a clock (<see cref="Keys"/>), whose
/// values blend linearly, unnormalized (a position, an offset): never along an arc, which is
/// <see cref="BindableDirection"/>'s blend. It binds no state row.
/// </summary>
[JsonConverter(typeof(BindableVector3JsonConverter))]
public readonly record struct BindableVector3 {
    /// <summary>The refusal every bindable three-component vector field shares.</summary>
    public const string Grammar = "must be a finite [x, y, z], or keys on a clock whose values are finite [x, y, z]";

    /// <summary>Initializes a new instance of the <see cref="BindableVector3"/> struct as a literal.</summary>
    /// <param name="literal">The vector.</param>
    public BindableVector3(Vector3 literal) {
        Keys = null;
        Literal = literal;
    }
    /// <summary>Initializes a new instance of the <see cref="BindableVector3"/> struct as keys on a clock.</summary>
    /// <param name="keys">The keys.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <see langword="null"/>.</exception>
    public BindableVector3(WorldKeyTrack<Vector3> keys) {
        ArgumentNullException.ThrowIfNull(argument: keys);

        Keys = keys;
        Literal = null;
    }

    /// <summary>Gets the literal vector, or <see langword="null"/> when it is keyed.</summary>
    public Vector3? Literal { get; }
    /// <summary>Gets the keys on a clock, or <see langword="null"/> when it is a literal.</summary>
    public WorldKeyTrack<Vector3>? Keys { get; }

    /// <summary>Converts a literal vector into a bindable vector.</summary>
    /// <param name="literal">The vector.</param>
    public static implicit operator BindableVector3(Vector3 literal) => new(literal: literal);

    /// <summary>Returns the vectors the document authors: its literal, or every key's value.</summary>
    /// <returns>The authored vectors.</returns>
    public IEnumerable<Vector3> AuthoredValues() {
        if (Literal is { } literal) {
            yield return literal;
        }

        if (Keys is { } keys) {
            foreach (var key in keys.Keys) {
                yield return key.Value;
            }
        }
    }
    /// <inheritdoc/>
    public override string ToString() => (Keys?.ToString() ?? ((Literal is { } literal)
        ? $"[{literal.X.ToString(provider: CultureInfo.InvariantCulture)}, {literal.Y.ToString(provider: CultureInfo.InvariantCulture)}, {literal.Z.ToString(provider: CultureInfo.InvariantCulture)}]"
        : string.Empty));
}
/// <summary>Reads/writes <see cref="BindableColor"/>: a plain string (a literal or a binding, validated against
/// <see cref="BindableColor.Grammar"/> at read/resolve, never a closed token set), or a keyed object whose values are
/// strings.</summary>
public sealed class BindableColorJsonConverter : JsonConverter<BindableColor>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() {
        ["anyOf"] = new JsonArray(
            new JsonObject { ["type"] = "string" },
            WorldKeyTrackJson.Schema(value: new JsonObject { ["type"] = "string" })
        ),
    };
    /// <inheritdoc/>
    public override BindableColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.StartObject) {
            return new BindableColor(keys: WorldKeyTrackJson.Read(
                kind: "color",
                reader: ref reader,
                readValue: static (ref Utf8JsonReader value) => ReadToken(reader: ref value)
            ));
        }

        return ReadToken(reader: ref reader);
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindableColor value, JsonSerializerOptions options) {
        if (value.Keys is { } keys) {
            WorldKeyTrackJson.Write(
                track: keys,
                writer: writer,
                writeValue: static (target, key) => target.WriteStringValue(value: (key.Raw ?? string.Empty))
            );

            return;
        }

        writer.WriteStringValue(value: (value.Raw ?? string.Empty));
    }

    private static BindableColor ReadToken(ref Utf8JsonReader reader) {
        if (reader.TokenType != JsonTokenType.String) {
            throw new JsonException(message: $"Expected {nameof(BindableColor)} to be a string or keys on a clock ({BindableColor.Grammar}).");
        }

        return new BindableColor(Raw: (reader.GetString() ?? throw new JsonException(message: $"{nameof(BindableColor)} must not be null.")));
    }
}
/// <summary>Reads/writes <see cref="BindableScalar"/> as a JSON number (literal), string (binding) or keyed object
/// whose values are numbers.</summary>
public sealed class BindableScalarJsonConverter : JsonConverter<BindableScalar>, IJsonSchemaNodeConverter {
    /// <summary>Builds the schema every scalar-shaped bindable shares: a number, a binding string, or keys whose
    /// values are numbers.</summary>
    /// <returns>The schema node.</returns>
    public static JsonObject Schema() => new() {
        ["anyOf"] = new JsonArray(
            new JsonObject { ["type"] = "number" },
            new JsonObject { ["type"] = "string" },
            WorldKeyTrackJson.Schema(value: new JsonObject { ["type"] = "number" })
        ),
    };
    /// <summary>Reads a scalar-shaped bindable.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="kind">The bindable's name, for refusals.</param>
    /// <returns>The scalar.</returns>
    /// <exception cref="JsonException">The token is not a number, a string or keys.</exception>
    public static BindableScalar ReadScalar(ref Utf8JsonReader reader, string kind) {
        if (reader.TokenType == JsonTokenType.Number) {
            return new BindableScalar(literal: reader.GetSingle());
        }

        if (reader.TokenType == JsonTokenType.String) {
            return new BindableScalar(binding: (reader.GetString() ?? throw new JsonException(message: $"{kind} must not be null.")));
        }

        if (reader.TokenType == JsonTokenType.StartObject) {
            return new BindableScalar(keys: WorldKeyTrackJson.Read(
                kind: kind,
                reader: ref reader,
                readValue: static (ref Utf8JsonReader value) => ((value.TokenType == JsonTokenType.Number)
                    ? value.GetSingle()
                    : throw new JsonException(message: "A keyed number's value must be a number."))
            ));
        }

        throw new JsonException(message: $"Expected {kind} to be a number, a string or keys on a clock ({BindableScalar.Grammar}).");
    }
    /// <summary>Writes a scalar-shaped bindable.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The scalar.</param>
    public static void WriteScalar(Utf8JsonWriter writer, BindableScalar value) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        if (value.Keys is { } keys) {
            WorldKeyTrackJson.Write(
                track: keys,
                writer: writer,
                writeValue: static (target, key) => target.WriteNumberValue(value: key)
            );
        } else if (value.Binding is { } binding) {
            writer.WriteStringValue(value: binding);
        } else {
            writer.WriteNumberValue(value: (value.Literal ?? 0f));
        }
    }
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => Schema();
    /// <inheritdoc/>
    public override BindableScalar Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ReadScalar(
        kind: nameof(BindableScalar),
        reader: ref reader
    );
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindableScalar value, JsonSerializerOptions options) => WriteScalar(
        value: value,
        writer: writer
    );
}
/// <summary>Reads/writes <see cref="BindableAngle"/> in <see cref="BindableScalar"/>'s wire form.</summary>
public sealed class BindableAngleJsonConverter : JsonConverter<BindableAngle>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => BindableScalarJsonConverter.Schema();
    /// <inheritdoc/>
    public override BindableAngle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(value: BindableScalarJsonConverter.ReadScalar(
        kind: nameof(BindableAngle),
        reader: ref reader
    ));
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindableAngle value, JsonSerializerOptions options) => BindableScalarJsonConverter.WriteScalar(
        value: value.Value,
        writer: writer
    );
}
/// <summary>Reads/writes <see cref="BindableDirection"/> as an <c>[x, y, z]</c> array or a keyed object whose values
/// are arrays.</summary>
public sealed class BindableDirectionJsonConverter : JsonConverter<BindableDirection>, IJsonSchemaNodeConverter {
    private static readonly Vector3JsonConverter Vector = new();

    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() {
        ["anyOf"] = new JsonArray(
            FixedArityNumberArraySchema.Build(arity: 3),
            WorldKeyTrackJson.Schema(value: FixedArityNumberArraySchema.Build(arity: 3))
        ),
    };
    /// <inheritdoc/>
    public override BindableDirection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.StartObject) {
            return new BindableDirection(keys: WorldKeyTrackJson.Read(
                kind: "direction",
                reader: ref reader,
                readValue: static (ref Utf8JsonReader value) => Vector.Read(
                    options: WorldJsonContext.Default.Options,
                    reader: ref value,
                    typeToConvert: typeof(Vector3)
                )
            ));
        }

        return new BindableDirection(literal: Vector.Read(
            options: options,
            reader: ref reader,
            typeToConvert: typeof(Vector3)
        ));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindableDirection value, JsonSerializerOptions options) {
        if (value.Keys is { } keys) {
            WorldKeyTrackJson.Write(
                track: keys,
                writer: writer,
                writeValue: static (target, key) => Vector.Write(
                    options: WorldJsonContext.Default.Options,
                    value: key,
                    writer: target
                )
            );

            return;
        }

        Vector.Write(
            options: options,
            value: (value.Literal ?? Vector3.Zero),
            writer: writer
        );
    }
}
/// <summary>Reads/writes <see cref="BindableVector2"/> as an <c>[x, y]</c> array or a keyed object whose values are
/// arrays.</summary>
public sealed class BindableVector2JsonConverter : JsonConverter<BindableVector2>, IJsonSchemaNodeConverter {
    private static readonly Vector2JsonConverter Vector = new();

    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() {
        ["anyOf"] = new JsonArray(
            FixedArityNumberArraySchema.Build(arity: 2),
            WorldKeyTrackJson.Schema(value: FixedArityNumberArraySchema.Build(arity: 2))
        ),
    };
    /// <inheritdoc/>
    public override BindableVector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.StartObject) {
            return new BindableVector2(keys: WorldKeyTrackJson.Read(
                kind: "vector",
                reader: ref reader,
                readValue: static (ref Utf8JsonReader value) => Vector.Read(
                    options: WorldJsonContext.Default.Options,
                    reader: ref value,
                    typeToConvert: typeof(Vector2)
                )
            ));
        }

        return new BindableVector2(literal: Vector.Read(
            options: options,
            reader: ref reader,
            typeToConvert: typeof(Vector2)
        ));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindableVector2 value, JsonSerializerOptions options) {
        if (value.Keys is { } keys) {
            WorldKeyTrackJson.Write(
                track: keys,
                writer: writer,
                writeValue: static (target, key) => Vector.Write(
                    options: WorldJsonContext.Default.Options,
                    value: key,
                    writer: target
                )
            );

            return;
        }

        Vector.Write(
            options: options,
            value: (value.Literal ?? Vector2.Zero),
            writer: writer
        );
    }
}
/// <summary>Reads/writes <see cref="BindableVector3"/> as an <c>[x, y, z]</c> array or a keyed object whose values are
/// arrays.</summary>
public sealed class BindableVector3JsonConverter : JsonConverter<BindableVector3>, IJsonSchemaNodeConverter {
    private static readonly Vector3JsonConverter Vector = new();

    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() {
        ["anyOf"] = new JsonArray(
            FixedArityNumberArraySchema.Build(arity: 3),
            WorldKeyTrackJson.Schema(value: FixedArityNumberArraySchema.Build(arity: 3))
        ),
    };
    /// <inheritdoc/>
    public override BindableVector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.StartObject) {
            return new BindableVector3(keys: WorldKeyTrackJson.Read(
                kind: "vector",
                reader: ref reader,
                readValue: static (ref Utf8JsonReader value) => Vector.Read(
                    options: WorldJsonContext.Default.Options,
                    reader: ref value,
                    typeToConvert: typeof(Vector3)
                )
            ));
        }

        return new BindableVector3(literal: Vector.Read(
            options: options,
            reader: ref reader,
            typeToConvert: typeof(Vector3)
        ));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindableVector3 value, JsonSerializerOptions options) {
        if (value.Keys is { } keys) {
            WorldKeyTrackJson.Write(
                track: keys,
                writer: writer,
                writeValue: static (target, key) => Vector.Write(
                    options: WorldJsonContext.Default.Options,
                    value: key,
                    writer: target
                )
            );

            return;
        }

        Vector.Write(
            options: options,
            value: (value.Literal ?? Vector3.Zero),
            writer: writer
        );
    }
}
