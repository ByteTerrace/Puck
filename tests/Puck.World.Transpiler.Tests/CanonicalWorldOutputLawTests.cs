using System.Text;
using Puck.Abstractions.Documents;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class CanonicalWorldOutputLawTests {
    [Fact]
    public void CompiledDocumentsHaveOneNumberSpellingAndOrdinalMembersAtEveryDepth() {
        const string Source = """
            schema: "puck.world.definition.v1"
            screens [ { origin [1.0, 1, 0.50] name: "screen" } ]
            host { width: 1.0 height: 1 }
            """;
        const string Expected = """
            {
              "host": {
                "height": 1,
                "width": 1
              },
              "schema": "puck.world.definition.v1",
              "screens": [
                {
                  "name": "screen",
                  "origin": [
                    1,
                    1,
                    0.5
                  ]
                }
              ]
            }
            """;
        var compiled = WorldCompiler.Compile(source: Source, cancellationToken: TestContext.Current.CancellationToken).RequireJson();

        Assert.Equal(expected: (Expected + "\n"), actual: Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: compiled)));
    }
}
