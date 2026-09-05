using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>The postfix object spelling of a <see cref="ValueExpression"/>: <c>{ "tokens": [...] }</c>. The
/// converter reads this shape when an expression is authored as tokens and writes it back for an expression that
/// carries no infix text.</summary>
/// <param name="Tokens">The postfix tokens, in evaluation order.</param>
public sealed record ValueExpressionTokens(IReadOnlyList<ValueToken> Tokens);

/// <summary>Reads a <see cref="ValueExpression"/> from either of its two spellings — an infix string
/// (<see cref="ExpressionSpelling"/>) or the postfix <see cref="ValueExpressionTokens"/> object — and writes
/// it back in the spelling it was authored in.</summary>
public sealed class ValueExpressionJsonConverter : JsonConverter<ValueExpression>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    public override ValueExpression? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.String) {
            var text = reader.GetString()!;
            if (!ExpressionSpelling.TryParse(text: text, tokens: out var tokens, error: out var error)) {
                throw new JsonException(message: $"expression \"{text}\" {error}");
            }
            return new ValueExpression(Tokens: tokens) { Text = text };
        }
        if (reader.TokenType == JsonTokenType.StartObject) {
            var carrier = JsonSerializer.Deserialize(reader: ref reader, jsonTypeInfo: TokensTypeInfo(options: options))
                ?? throw new JsonException(message: "an expression object carries 'tokens'");
            return new ValueExpression(Tokens: carrier.Tokens);
        }
        throw new JsonException(message: "an expression is an infix string (\"a * 2 - b\") or an object carrying postfix 'tokens'");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ValueExpression value, JsonSerializerOptions options) {
        if (value.Text is { } text) {
            writer.WriteStringValue(value: text);
            return;
        }
        JsonSerializer.Serialize(writer: writer, value: new ValueExpressionTokens(Tokens: value.Tokens), jsonTypeInfo: TokensTypeInfo(options: options));
    }

    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() {
        ["anyOf"] = new JsonArray(
            new JsonObject {
                ["description"] = "The infix spelling: C precedence over + - * / % & | ^ ~ << >> >>> == != < <= > >= and condition ? whenTrue : whenFalse; named forms as calls (min, max, clamp, abs, sign, popCount, leadingZeroCount, trailingZeroCount, lowestSetBit, clearLowestSetBit, byteSwap, bitReverse, rotateLeft, rotateRight, parallelBitExtract, parallelBitDeposit, bitField, bitInsert, boardShift(mask, topology, direction), boardImage(mask, topology, element), select); a state read as its row name, keyed as row[key], backquoted when not a bare name; decimal or 0x-hexadecimal literals.",
                ["type"] = "string",
                ["minLength"] = 1,
                ["maxLength"] = ExpressionSpelling.MaxLength,
            },
            exportType(typeof(ValueExpressionTokens))
        ),
    };

    private static JsonTypeInfo<ValueExpressionTokens> TokensTypeInfo(JsonSerializerOptions options) =>
        (JsonTypeInfo<ValueExpressionTokens>)options.GetTypeInfo(type: typeof(ValueExpressionTokens));
}
