using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>THE LAW: the package README spells a postfix expression the way the reader takes one — an
/// <c>instructions</c> array whose every element names its <c>op</c>.</summary>
public sealed class SchemaReadmeExpressionSpellingLawTests {
    private static string Readme() => File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "src/Puck.World.Schema/README.md"));

    [Fact]
    public void TheReadmeSpellsAPostfixProgramAsAnInstructionArray() {
        var readme = Readme();

        // `"tokens": "<row>"` is the derived board's token row, a different member entirely; only the array form is
        // an expression program, and the reader takes none.
        Assert.DoesNotContain(
            actualString: readme,
            expectedSubstring: "\"tokens\": ["
        );
        Assert.Contains(
            actualString: readme,
            expectedSubstring: "\"instructions\": ["
        );
    }
    [Fact]
    public void EveryOperationTheReadmeNamesIsAnExpressionOperation() {
        var named = Regex.Matches(
            input: Readme(),
            pattern: "\"op\": \"(?<op>[A-Za-z]+)\""
        ).Select(selector: static match => match.Groups["op"].Value).Distinct().ToArray();

        Assert.NotEmpty(collection: named);

        foreach (var operation in named) {
            Assert.True(
                condition: Enum.TryParse(
                    ignoreCase: false,
                    result: out ExpressionOp _,
                    value: operation
                ),
                userMessage: $"the README names '{operation}' as an expression operation"
            );
        }
    }
    [Fact]
    public void TheReaderTakesInstructionsAndRefusesTokens() {
        var program = ExpressionProgramJsonConverter.FromNode(node: JsonNode.Parse(json: """
        { "instructions": [{ "op": "Constant", "value": 1 }, { "op": "Operand", "name": "hp" }, { "op": "Add" }] }
        """));

        Assert.Equal(
            actual: program.Instructions.Count,
            expected: 3
        );
        _ = Assert.Throws<System.Text.Json.JsonException>(testCode: static () => ExpressionProgramJsonConverter.FromNode(node: JsonNode.Parse(json: """
        { "tokens": [{ "op": "Constant", "value": 1 }] }
        """)));
    }
}
