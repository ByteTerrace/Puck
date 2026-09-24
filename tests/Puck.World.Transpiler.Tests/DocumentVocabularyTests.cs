using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class DocumentVocabularyTests {
    private const string Custom = "puck.custom.v1";

    // Only a top-level `schema:` names the document's schema: not a longer key that ends in it, not text inside a
    // raw string, a nested block or a comment.
    [InlineData("subschema: \"puck.world.definition.v1\"\n", null)]
    [InlineData("let text = \"\"\"\nschema: \"puck.world.definition.v1\"\n\"\"\"\n", null)]
    [InlineData("world {\n    schema: \"puck.world.definition.v1\"\n}\n", null)]
    [InlineData("// Leading comment\nschema: \"puck.custom.v1\"\n\ncustom {\n}\n", "puck.custom.v1")]
    [InlineData("// schema: \"puck.cartridge.v1\"\nschema: \"puck.world.definition.v1\"\n\nsql {\n    DECLARE counter INT = 0;\n}\n", "puck.world.definition.v1")]
    [Theory]
    public void OnlyATopLevelSchemaPropertyNamesTheDocumentsSchema(string source, string? expected) {
        Assert.Equal(
            actual: PuckParser.TryReadDocumentSchema(
                schema: out var schema,
                source: source
            ),
            expected: (expected is not null)
        );
        Assert.Equal(
            actual: schema,
            expected: expected
        );
    }
    [Fact]
    public void TestDocumentVocabularyResolver() {
        var resolver = InertVocabulary.ResolverFor(schema: Custom);

        Assert.Same(expected: InertVocabulary.Instance, actual: resolver.Resolve(source: "schema: \"puck.custom.v1\"\n"));
        Assert.Same(expected: WorldDocumentVocabulary.Instance, actual: resolver.Resolve(source: "schema: \"puck.world.definition.v1\"\n"));
        Assert.Same(expected: WorldDocumentVocabulary.Instance, actual: resolver.Resolve(source: "let x = 1\n"));
        Assert.Same(expected: WorldDocumentVocabulary.Instance, actual: resolver.Resolve(source: "subschema: \"puck.custom.v1\"\n"));
    }
    [Fact]
    public async Task TestCustomDocumentInLspGetsNoSqlHoverOrCompletion() {
        var cursor = MarkedSource.Parse(marked: """
            schema: "puck.custom.v1"

            sql {
                SE|LECT 1;
            }
            """);
        var transcript = await LanguageServerClient.SessionAsync(
            requests: [
                LanguageServerClient.PositionRequest(
                    character: cursor.Character,
                    id: 2,
                    line: cursor.Line,
                    method: "textDocument/completion"
                ),
                LanguageServerClient.PositionRequest(
                    character: cursor.Character,
                    id: 3,
                    line: cursor.Line,
                    method: "textDocument/hover"
                ),
            ],
            text: cursor.Text,
            vocabularyResolver: InertVocabulary.ResolverFor(schema: Custom)
        );

        Assert.DoesNotContain(
            collection: LanguageServerClient.Completions(result: transcript.Result(id: 2)),
            filter: static item => (item.Label == "SELECT")
        );
        Assert.Null(@object: transcript.Result(id: 3));
    }
}
