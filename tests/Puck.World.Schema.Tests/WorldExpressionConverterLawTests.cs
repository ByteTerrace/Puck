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
        var fromText = JsonSerializer.Deserialize("\"hp - min(damage, hp)\"", info)!;
        var fromTokens = JsonSerializer.Deserialize(
            "{\"tokens\":[{\"$type\":\"state\",\"name\":\"hp\"},{\"$type\":\"state\",\"name\":\"damage\"},{\"$type\":\"state\",\"name\":\"hp\"},{\"$type\":\"min\"},{\"$type\":\"subtract\"}]}",
            info
        )!;
        Assert.Equal(fromTokens.Tokens, fromText.Tokens);
        Assert.Equal("hp - min(damage, hp)", fromText.Text);
        Assert.Null(fromTokens.Text);
        Assert.Equal("\"hp - min(damage, hp)\"", JsonSerializer.Serialize(fromText, info));
        Assert.Contains("\"tokens\": [", JsonSerializer.Serialize(fromTokens, info), StringComparison.Ordinal);
        Assert.Equal(fromTokens.Tokens, JsonSerializer.Deserialize(JsonSerializer.Serialize(fromTokens, info), info)!.Tokens);

        var refusal = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"a +\"", info));
        Assert.Contains("reached the end", refusal.Message, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"text\":\"a\"}", info));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("12", info));
    }

    [Fact]
    public void TheSchemaAdmitsBothSpellings() {
        var schema = WorldSchema.Export(postRenderExtensions: []);
        var defs = schema.Common["$defs"]!.AsObject();
        var expression = defs["ValueExpression"]!.AsObject();
        var arms = expression["anyOf"]!.AsArray();
        Assert.Equal(2, arms.Count);
        Assert.Equal("string", arms[0]!["type"]!.GetValue<string>());
        Assert.Contains("ValueExpressionTokens", arms[1]!.ToJsonString(), StringComparison.Ordinal);
        Assert.True(defs.ContainsKey("ValueExpressionTokens"));
        Assert.True(defs.ContainsKey("ValueToken"));
    }
}
