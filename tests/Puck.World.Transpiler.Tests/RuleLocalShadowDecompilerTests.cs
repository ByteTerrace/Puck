using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A rule whose local shares a state row's name: the decompiler must print the row read in a form that
/// still reads the row, not the local, once the rule's locals are back in scope at recompile.</summary>
public class RuleLocalShadowDecompilerTests {
    [Fact]
    public void ARowReadALocalShadowsDecompilesAndRecompilesToTheSameDocument() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse("""
            {
                "schema": "puck.world.definition.v1",
                "state": {
                    "world": [
                        { "name": "k", "kind": "Int", "value": 0, "min": 0, "max": 100 },
                        { "name": "flag", "kind": "Int", "value": 0, "min": 0, "max": 100 }
                    ]
                },
                "rules": [
                    {
                        "name": "r",
                        "locals": [
                            {
                                "name": "k",
                                "expression": { "instructions": [{ "op": "Constant", "value": 1 }] }
                            }
                        ],
                        "effects": [
                            { "$type": "setState", "state": "flag", "fromState": "k" }
                        ]
                    }
                ]
            }
            """));

        var decompiled = WorldDecompiler.Decompile(root: original);
        var recompilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: decompiled
        );

        Assert.False(
            condition: recompilation.Diagnostics.HasErrors,
            userMessage: $"{recompilation.Diagnostics.FormatReport(decompiled)}\n---\n{decompiled}"
        );

        var mismatch = JsonMismatch.Find(
            actual: recompilation.RequireJson(),
            expected: original,
            path: "$"
        );

        Assert.True(
            condition: (mismatch is null),
            userMessage: $"{mismatch}\n---\n{decompiled}"
        );
    }
}
