using System.Text.Json;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The world document's half of the expression contract: its JSON context reads both spellings of a
/// <see cref="ValueExpression"/> and writes each back in its own, and the generated schema admits both. The
/// parse/print laws themselves live in <c>tests/Puck.State.Tests</c>, beside the syntax.</summary>
public sealed class WorldExpressionConverterLawTests {
    [Fact]
    public void TheConverterReadsBothSpellingsAndWritesEachBackInItsOwn() {
        var info = WorldJsonContext.Default.ValueExpression;
        var fromText = JsonSerializer.Deserialize(
            json: "\"hp - minimum(damage, hp)\"",
            jsonTypeInfo: info
        )!;
        var fromTokens = JsonSerializer.Deserialize(
            json: "{\"tokens\":[{\"$type\":\"state\",\"name\":\"hp\"},{\"$type\":\"state\",\"name\":\"damage\"},{\"$type\":\"state\",\"name\":\"hp\"},{\"$type\":\"min\"},{\"$type\":\"subtract\"}]}",
            jsonTypeInfo: info
        )!;

        Assert.Equal(
            fromTokens.Tokens,
            fromText.Tokens
        );
        Assert.Equal(
            "hp - minimum(damage, hp)",
            fromText.Text
        );
        Assert.Null(@object: fromTokens.Text);
        Assert.Equal(
            "\"hp - minimum(damage, hp)\"",
            JsonSerializer.Serialize(
                jsonTypeInfo: info,
                value: fromText
            )
        );
        Assert.Contains(
            "\"tokens\": [",
            JsonSerializer.Serialize(
                jsonTypeInfo: info,
                value: fromTokens
            ),
            StringComparison.Ordinal
        );
        Assert.Equal(
            fromTokens.Tokens,
            JsonSerializer.Deserialize(
                json: JsonSerializer.Serialize(
                    jsonTypeInfo: info,
                    value: fromTokens
                ),
                jsonTypeInfo: info
            )!.Tokens
        );

        var refusal = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: "\"a +\"",
            jsonTypeInfo: info
        ));

        Assert.Contains(
            "reached the end",
            refusal.Message,
            StringComparison.Ordinal
        );
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: "{\"text\":\"a\"}",
            jsonTypeInfo: info
        ));
        Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: "12",
            jsonTypeInfo: info
        ));
    }
    [Fact]
    public void TheSchemaAdmitsBothSpellings() {
        var schema = WorldSchema.Export(postRenderExtensions: []);
        var defs = schema.Common["$defs"]!.AsObject();
        var expression = defs["ValueExpression"]!.AsObject();
        var arms = expression["anyOf"]!.AsArray();

        Assert.Equal(
            2,
            arms.Count
        );
        Assert.Equal(
            "string",
            arms[0]!["type"]!.GetValue<string>()
        );
        Assert.Contains(
            "ValueExpressionTokens",
            arms[1]!.ToJsonString(),
            StringComparison.Ordinal
        );
        Assert.True(condition: defs.ContainsKey(propertyName: "ValueExpressionTokens"));
        Assert.True(condition: defs.ContainsKey(propertyName: "ValueToken"));
    }
}
