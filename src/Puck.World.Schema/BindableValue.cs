using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
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
/// A color authored as a <c>#RRGGBB</c>/<c>#RRGGBBAA</c> hex literal, or a <c>state.&lt;row&gt;[.&lt;key&gt;][.$target]</c>
/// binding naming a Text cell that holds one — the theme/marker/render vocabulary's shared color grammar. Parses and
/// serializes as a plain JSON string. The token is parsed once, into <see cref="Literal"/> or <see cref="State"/>; a
/// presentation reads the binding through the client's state mirror, the one binding path, which reads it eased by
/// default and as stored truth with <c>.$target</c>. This carries alpha — a translucent theme surface bakes it into
/// the token — while an opaque consumer (the sky and lighting fields) drops it.
/// </summary>
/// <param name="Raw">The authored token, verbatim.</param>
[JsonConverter(typeof(BindableColorJsonConverter))]
public readonly record struct BindableColor(string Raw) {
    /// <summary>The refusal every bindable color field shares.</summary>
    public const string Grammar = "must be #RRGGBB, #RRGGBBAA, or state.<row>[.<key>] naming a Text cell that holds one";

    /// <summary>Gets the parsed hex literal, or <see langword="null"/> when the token is a binding or malformed.</summary>
    public Vector4? Literal { get; } = (HexColor.TryParseRgba(
        rgba: out var literal,
        value: Raw
    )
        ? literal
        : null
    );
    /// <summary>Gets the parsed state binding, or <see langword="null"/> when the token is a literal or malformed.</summary>
    public StateBinding? State { get; } = StateBinding.Parse(token: Raw);

    /// <summary>Returns whether this color is admissible against a document: a hex literal, or a state binding
    /// naming a declared Text cell whose text is one.</summary>
    /// <param name="definition">The document to check the binding half against.</param>
    /// <returns><see langword="true"/> when the color is admissible.</returns>
    public bool IsAuthorable(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

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
}
/// <summary>
/// A scalar authored as a finite number literal, or a <c>state.&lt;row&gt;[.&lt;key&gt;][.$target]</c> binding naming
/// a Fixed or Int cell whose live value drives it — the numeric twin of <see cref="BindableColor"/>, sharing its
/// binding grammar (<see cref="StateBinding"/>) and its one read path, the client's state mirror. Parses as a JSON
/// number (literal) or string (binding).
/// </summary>
[JsonConverter(typeof(BindableScalarJsonConverter))]
public readonly record struct BindableScalar {
    /// <summary>The refusal every bindable scalar field shares.</summary>
    public const string Grammar = "must be a finite number, or state.<row>[.<key>] naming a Fixed or Int cell";

    /// <summary>Gets the authored binding token, or <see langword="null"/> when this is a literal.</summary>
    public string? Binding { get; }
    /// <summary>Gets the authored literal value, or <see langword="null"/> when this is a binding.</summary>
    public float? Literal { get; }
    /// <summary>Gets the parsed state binding, or <see langword="null"/> when this is a literal or the token is
    /// malformed.</summary>
    public StateBinding? State { get; }

    /// <summary>Initializes a new instance of the <see cref="BindableScalar"/> struct as a literal.</summary>
    /// <param name="literal">The authored value.</param>
    public BindableScalar(float literal) {
        Binding = null;
        Literal = literal;
        State = null;
    }
    /// <summary>Initializes a new instance of the <see cref="BindableScalar"/> struct as a binding.</summary>
    /// <param name="binding">The authored binding token.</param>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
    public BindableScalar(string binding) {
        ArgumentNullException.ThrowIfNull(argument: binding);

        Binding = binding;
        Literal = null;
        State = StateBinding.Parse(token: binding);
    }

    /// <summary>Returns whether this scalar is admissible against a document: a finite literal, or a state binding
    /// naming a declared Fixed or Int cell.</summary>
    /// <param name="definition">The document to check the binding half against.</param>
    /// <returns><see langword="true"/> when the scalar is admissible.</returns>
    public bool IsAuthorable(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

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
}
/// <summary>Reads/writes <see cref="BindableColor"/> as its plain-string wire form — a free-form string validated
/// against <see cref="BindableColor.Grammar"/> at read/resolve, never a closed token set.</summary>
public sealed class BindableColorJsonConverter : JsonConverter<BindableColor>, IJsonSchemaStringConverter {
    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens => null;

    /// <inheritdoc/>
    public override BindableColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.String) {
            throw new JsonException(message: $"Expected {nameof(BindableColor)} to be a string ({BindableColor.Grammar}).");
        }

        return new BindableColor(Raw: (reader.GetString() ?? throw new JsonException(message: $"{nameof(BindableColor)} must not be null.")));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindableColor value, JsonSerializerOptions options) => writer.WriteStringValue(value: value.Raw);
}
/// <summary>Reads/writes <see cref="BindableScalar"/> as a JSON number (literal) or string (binding).</summary>
public sealed class BindableScalarJsonConverter : JsonConverter<BindableScalar>, IJsonSchemaTypeConverter {
    private static readonly string[] AcceptedSchemaTypes = ["number", "string"];

    /// <inheritdoc/>
    public IReadOnlyList<string> SchemaTypes => AcceptedSchemaTypes;

    /// <inheritdoc/>
    public override BindableScalar Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.Number) {
            return new BindableScalar(literal: reader.GetSingle());
        }

        if (reader.TokenType == JsonTokenType.String) {
            return new BindableScalar(binding: (reader.GetString() ?? throw new JsonException(message: $"{nameof(BindableScalar)} must not be null.")));
        }

        throw new JsonException(message: $"Expected {nameof(BindableScalar)} to be a number or a string ({BindableScalar.Grammar}).");
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, BindableScalar value, JsonSerializerOptions options) {
        if (value.Binding is { } binding) {
            writer.WriteStringValue(value: binding);
        } else {
            writer.WriteNumberValue(value: (value.Literal ?? 0f));
        }
    }
}
