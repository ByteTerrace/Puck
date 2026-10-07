using System.Text.Json.Nodes;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldDecompileRoundTripLawTests {
    [Fact]
    public void GenericPlacementRowsRetainQuotedPrototypeNamesAndExplicitNulls() {
        var original = JsonNode.Parse(json: """
            {
              "schema": "puck.world.definition.v1",
              "placements": {
                "rows": [
                  { "id": "tile", "prototypeId": "floor", "distribution": null }
                ]
              }
            }
            """)!.AsObject();
        // The explicit null keeps the placement in the generic row form used by a serialized live world.
        var printed = WorldDecompiler.Decompile(root: original);
        var compiled = WorldCompiler.Compile(source: printed, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: compiled.Diagnostics.HasErrors, userMessage: string.Join(separator: "\n", values: compiled.Diagnostics));
        var restored = compiled.RequireJson();

        Assert.Equal(expected: "floor", actual: restored["placements"]!["rows"]![0]!["prototypeId"]!.GetValue<string>());
        Assert.True(condition: JsonNode.DeepEquals(node1: original, node2: restored), userMessage: printed);
    }
}
