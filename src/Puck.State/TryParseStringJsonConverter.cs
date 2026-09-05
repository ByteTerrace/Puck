using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>
/// The free-form validated-string converter shape: a <c>Read</c> that pulls a string token (<see langword="null"/>
/// for any other token type), hands it to the value type's own <c>TryParse</c>, and refuses by that parse's own
/// named reason; a <c>Write</c> that prints the validated string back. A closed subclass supplies only the
/// parse/print pair, exactly as the world document's <c>TokenEnumJsonConverter&lt;T&gt;</c> does for a closed vocabulary.
/// </summary>
/// <typeparam name="T">The validated-string value type.</typeparam>
public abstract class TryParseStringJsonConverter<T> : JsonConverter<T>, IJsonSchemaStringConverter where T : struct {
    // Free-form (validated at parse by the value type's own TryParse, not by a fixed token set) — "type":"string"
    // alone, no "enum". See IJsonSchemaStringConverter's own remarks for why null means this rather than
    // "unconstrained".
    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens => null;

    /// <summary>Parses <paramref name="candidate"/>, or names why it is refused.</summary>
    /// <param name="candidate">The read token, or <see langword="null"/> when the JSON value was not a string.</param>
    /// <param name="value">The parsed value.</param>
    /// <param name="reason">Why the candidate was refused.</param>
    /// <returns><see langword="true"/> when the candidate parsed.</returns>
    protected abstract bool TryParse(string? candidate, out T value, out string reason);
    /// <summary>The validated string <paramref name="value"/> writes back as.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The string form.</returns>
    protected abstract string ToValue(T value);

    /// <inheritdoc/>
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var token = ((reader.TokenType == JsonTokenType.String)
            ? reader.GetString()
            : null
        );

        return (TryParse(
            candidate: token,
            reason: out var reason,
            value: out var value
        )
            ? value
            : throw new JsonException(message: $"'{token}' {reason}.")
        );
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value: ToValue(value: value));
}
