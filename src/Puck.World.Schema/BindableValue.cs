using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.World.Authoring;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The shared "authored literal, or a state.&lt;row&gt;[.&lt;key&gt;] binding" grammar every bindable document token
/// speaks — promoted from the pattern <see cref="WorldColor"/> established for the sky's cloud color.
/// <see cref="BindableColor"/> and <see cref="BindableScalar"/> are its two closed instances; both parse their
/// binding half through <see cref="TryParseBinding"/> and resolve it through <see cref="WorldStateReader"/>, so no
/// second binding-token parser or state reader exists anywhere in the document model.
/// </summary>
public static class BindableState {
    /// <summary>Parses the state-binding arm of the grammar: <c>state.&lt;row&gt;</c> (the row's slot cell) or
    /// <c>state.&lt;row&gt;.&lt;key&gt;</c>. Any other token — including a literal — is not a binding.</summary>
    /// <param name="value">The candidate token.</param>
    /// <param name="row">The bound row's name, or empty when not a binding.</param>
    /// <param name="key">The bound cell key, or <see langword="null"/> for the row's own slot cell.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a well-formed state binding.</returns>
    public static bool TryParseBinding(string? value, out string row, out string? key) {
        row = string.Empty;
        key = null;

        if (
            string.IsNullOrEmpty(value: value) ||
            !HudBindingVocabulary.TryParse(binding: out var binding, token: value) ||
            (binding.Kind != HudBindingKind.StateNamed)
        ) {
            return false;
        }

        row = binding.StateName!;
        key = binding.StateCellKey;

        return true;
    }
}
/// <summary>
/// A color authored as a <c>#RRGGBB</c>/<c>#RRGGBBAA</c> hex literal, or a <c>state.&lt;row&gt;[.&lt;key&gt;]</c>
/// binding naming a Text cell that holds one — the theme/marker vocabulary's promoted form of the grammar
/// <see cref="WorldColor"/> has spoken for the sky's cloud color. Parses and serializes as a plain JSON string;
/// resolution reuses <see cref="HexColor"/>'s literal parse and <see cref="WorldStateReader"/>'s state read exactly
/// as <see cref="WorldColor"/> does, so a theme color and the sky's color never disagree about what counts as a
/// valid token. Unlike <see cref="WorldColor"/> (RGB-only, matching the render path's opaque sky/lighting fields),
/// this carries alpha — a theme surface bakes its translucency into the token.
/// </summary>
/// <param name="Raw">The authored token, verbatim.</param>
[JsonConverter(typeof(BindableColorJsonConverter))]
public readonly record struct BindableColor(string Raw) {
    /// <summary>The refusal every bindable color field shares.</summary>
    public const string Grammar = "must be #RRGGBB, #RRGGBBAA, or state.<row>[.<key>] naming a Text cell that holds one";

    /// <summary>Returns whether this color is admissible against a document: a hex literal, or a state binding
    /// naming a declared Text cell whose text is one.</summary>
    /// <param name="definition">The document to check the binding half against.</param>
    public bool IsAuthorable(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!BindableState.TryParseBinding(
            key: out var key,
            row: out var row,
            value: Raw
        )) {
            return HexColor.TryParseRgba(
                rgba: out _,
                value: Raw
            );
        }

        return (
            WorldStateReader.TryRead(
                definition: definition, key: key, rawValue: out _, row: out var stateRow,
                rowName: row, text: out var text, tick: 0UL
            ) &&
            (stateRow.Kind == CellKind.Text) &&
            HexColor.TryParseRgba(
                rgba: out _,
                value: text
            )
        );
    }
    /// <summary>Resolves this color against the live document — a hex literal parses directly, a state binding
    /// reads its Text cell — falling back when the token is neither, the cell is absent, or its text is not a hex
    /// color (the validator refuses all three at author time for a literal binding; a live cell edit can still put a
    /// non-color there).</summary>
    /// <param name="definition">The document to resolve against.</param>
    /// <param name="fallback">The color returned when this token does not resolve.</param>
    /// <param name="tick">The tick a bound cell's value is read as of.</param>
    public Vector4 Resolve(WorldDefinition definition, Vector4 fallback, ulong tick = 0UL) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (!BindableState.TryParseBinding(
            key: out var key,
            row: out var row,
            value: Raw
        )) {
            return (HexColor.TryParseRgba(
                rgba: out var literal,
                value: Raw
            )
                ? literal
                : fallback);
        }

        if (
            WorldStateReader.TryRead(
                definition: definition, key: key, rawValue: out _, row: out var stateRow,
                rowName: row, text: out var text, tick: tick
            ) &&
            (stateRow.Kind == CellKind.Text) &&
            HexColor.TryParseRgba(
                rgba: out var bound,
                value: text
            )
        ) {
            return bound;
        }

        return fallback;
    }
}
/// <summary>
/// A scalar authored as a finite number literal, or a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> binding naming a Fixed
/// or Int cell whose live value drives it — the numeric twin of <see cref="BindableColor"/>, sharing its binding
/// grammar (<see cref="BindableState.TryParseBinding"/>) and its resolve-through-<see cref="WorldStateReader"/>
/// shape. Parses as a JSON number (literal) or string (binding).
/// </summary>
[JsonConverter(typeof(BindableScalarJsonConverter))]
public readonly record struct BindableScalar {
    /// <summary>The refusal every bindable scalar field shares.</summary>
    public const string Grammar = "must be a finite number, or state.<row>[.<key>] naming a Fixed or Int cell";

    /// <summary>Gets the authored binding token, or <see langword="null"/> when this is a literal.</summary>
    public string? Binding { get; }
    /// <summary>Gets the authored literal value, or <see langword="null"/> when this is a binding.</summary>
    public float? Literal { get; }

    /// <summary>Creates a literal scalar.</summary>
    public BindableScalar(float literal) {
        Binding = null;
        Literal = literal;
    }
    /// <summary>Creates a bound scalar.</summary>
    public BindableScalar(string binding) {
        ArgumentNullException.ThrowIfNull(argument: binding);

        Binding = binding;
        Literal = null;
    }

    /// <summary>Returns whether this scalar is admissible against a document: a finite literal, or a state binding
    /// naming a declared Fixed or Int cell.</summary>
    /// <param name="definition">The document to check the binding half against.</param>
    public bool IsAuthorable(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (Binding is null) {
            return ((Literal is { } literal) && float.IsFinite(f: literal));
        }

        return (
            BindableState.TryParseBinding(
            key: out var key,
            row: out var row,
            value: Binding
        ) &&
            WorldStateReader.TryRead(
                definition: definition, key: key, rawValue: out _, row: out var stateRow,
                rowName: row, text: out _, tick: 0UL
            ) &&
            (stateRow.Kind is CellKind.Fixed or CellKind.Int)
        );
    }
    /// <summary>Resolves this scalar against the live document — a literal parses directly, a state binding reads
    /// its Fixed/Int cell — falling back when the token is neither, the cell is absent, or it is not a Fixed/Int
    /// cell.</summary>
    /// <param name="definition">The document to resolve against.</param>
    /// <param name="fallback">The value returned when this token does not resolve.</param>
    /// <param name="tick">The tick a bound cell's value is read as of.</param>
    public float Resolve(WorldDefinition definition, float fallback, ulong tick = 0UL) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        if (Binding is null) {
            return (((Literal is { } literal) && float.IsFinite(f: literal))
                ? literal
                : fallback);
        }

        if (
            !BindableState.TryParseBinding(
            key: out var key,
            row: out var row,
            value: Binding
        ) ||
            !WorldStateReader.TryRead(
                definition: definition, key: key, rawValue: out var raw, row: out var stateRow,
                rowName: row, text: out _, tick: tick
            ) ||
            (raw is not { } rawValue)
        ) {
            return fallback;
        }

        return (stateRow.Kind switch {
            CellKind.Fixed => ((float)((double)FixedQ4816.FromRawBits(value: rawValue))),
            CellKind.Int => ((float)rawValue),
            _ => fallback,
        });
    }
}
/// <summary>Reads/writes <see cref="BindableColor"/> as its plain-string wire form.</summary>
public sealed class BindableColorJsonConverter : JsonConverter<BindableColor> {
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
