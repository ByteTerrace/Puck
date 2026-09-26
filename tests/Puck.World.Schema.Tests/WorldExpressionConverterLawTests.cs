using System.Text.Json;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The world document's half of the expression contract: its JSON context reads and writes an
/// <see cref="ExpressionProgram"/> as the IR and refuses every other spelling, and the generated schema describes
/// that one shape. The parse/print laws themselves live in <c>tests/Puck.State.Tests</c>, beside the syntax.</summary>
public sealed class WorldExpressionConverterLawTests {
    private const string Ir = """
        {"instructions":[{"op":"Operand","name":"hp"},{"op":"Operand","name":"damage"},{"op":"Operand","name":"hp"},{"op":"Minimum"},{"op":"Subtract"}]}
        """;

    [Fact]
    public void TheConverterReadsTheIrAndWritesItBack() {
        var info = WorldJsonContext.Default.ExpressionProgram;
        var program = JsonSerializer.Deserialize(
            json: Ir,
            jsonTypeInfo: info
        )!;

        Assert.Equal(
            actual: ExpressionSpelling.Print(program: program),
            expected: "hp - minimum(damage, hp)"
        );
        Assert.Equal(
            actual: JsonSerializer.Deserialize(
                json: JsonSerializer.Serialize(
                    jsonTypeInfo: info,
                    value: program
                ),
                jsonTypeInfo: info
            )!.Instructions,
            expected: program.Instructions
        );
    }
    [Fact]
    public void TheConverterRefusesEverySpellingButTheIr() {
        var info = WorldJsonContext.Default.ExpressionProgram;

        _ = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: "\"hp - minimum(damage, hp)\"",
            jsonTypeInfo: info
        ));
        _ = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: "{\"tokens\":[]}",
            jsonTypeInfo: info
        ));
        _ = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: "12",
            jsonTypeInfo: info
        ));
    }
    [Fact]
    public void TheSchemaDescribesTheIr() {
        var schema = WorldSchema.Export(postProcessPackages: []);
        var defs = schema.Common["$defs"]!.AsObject();
        var expression = defs["ExpressionProgram"]!.AsObject();

        Assert.Equal(
            actual: expression["type"]!.GetValue<string>(),
            expected: "object"
        );
        Assert.Contains(
            actualString: expression["properties"]!.ToJsonString(),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "instructions"
        );
        Assert.False(condition: defs.ContainsKey(propertyName: "ValueToken"));
    }
}
