using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The <c>set</c> declaration against the <c>sets</c> document member: the cell-set algebra reads the
/// authored expression, the document carries the parsed tree, and the decompiler prints the same text back. A
/// transform that reads a set of positions names a declared set bare, and the world validator refuses a name that is
/// no row and no set, and a set sharing a row's name.</summary>
public class CellSetDeclarationTests {
    [Fact]
    public void ASetDeclarationLowersToTheSetsMemberAndDecompilesToTheSameText() {
        var (document, diagnostics) = WorldSources.LowerSource(source: """
            schema: "puck.world.definition.v1"

            set liberties: board(grid, 0..0) & ~zone(stones, 1..1)
            """);

        Assert.DoesNotContain(collection: diagnostics, filter: d => (d.Severity == DiagnosticSeverity.Error));

        var row = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: document["sets"])));

        Assert.Equal("liberties", row["name"]?.ToString());
        Assert.Equal("both", Assert.IsType<JsonObject>(@object: row["set"])["$type"]?.ToString());
        Assert.Contains(
            actualString: WorldDecompiler.Decompile(root: document),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "set liberties: board(grid, 0..0) & ~zone(stones, 1..1)"
        );
    }

    private static string Board(string transforms, string set = "contested") => $$"""
        schema: "puck.world.definition.v1"

        state {
          lattices [
            grid(name: "grid", origin: [0, 0, 0], cellSize: 1, width: 3, depth: 3, wrap: None)
          ]

          world {
            row {
              name: "stones"
              kind: "Int"
              domain: cellsOf(topology: grid)
            }

            row {
              name: "marked"
              kind: "Int"
              domain: cellsOf(topology: grid)
            }
          }
        }

        set {{set}}: board(stones, 1..2) & ~board(stones, 2..2)

        rule "paint" {
          mode: Level
        {{transforms}}
        }
        """;
    private static WorldDefinition Deserialize(JsonObject document) => WorldDefinitionSerialization.Deserialize(utf8Json: Encoding.UTF8.GetBytes(s: document.ToJsonString()));

    [Fact]
    public void ATransformNamesADeclaredSetBareAndDecompilesToTheSameText() {
        var (document, diagnostics) = WorldSources.LowerSource(source: Board(transforms: """
              transform boardCombine(row: marked, operation: Copy, left: contested)
              transform writeSet(row: marked, set: contested, value: 1)
            """));

        Assert.DoesNotContain(collection: diagnostics, filter: d => (d.Severity == DiagnosticSeverity.Error));

        var effects = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonArray>(@object: document["rules"])[0]?["effects"]);

        Assert.Equal("contested", effects[0]?["transform"]?["left"]?.ToString());
        Assert.Equal("contested", effects[1]?["transform"]?["set"]?.ToString());

        var printed = WorldDecompiler.Decompile(root: document);

        Assert.Contains(
            actualString: printed,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "left: contested"
        );
        Assert.Contains(
            actualString: printed,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "set: contested"
        );

        var (reparsed, _) = WorldSources.LowerSource(source: printed);

        Assert.True(condition: JsonNode.DeepEquals(
            node1: document["rules"],
            node2: reparsed["rules"]
        ));
        Assert.Equal(
            actual: Deserialize(document: document).Sets.Single().Name.Value,
            expected: "contested"
        );
    }
    [Fact]
    public void ATransformNamingNoRowAndNoSetIsRefusedByTheValidator() {
        var (document, _) = WorldSources.LowerSource(source: Board(transforms: """
              transform boardCombine(row: marked, operation: Copy, left: nowhere)
            """));

        Assert.Contains(
            actualString: Assert.Throws<InvalidDataException>(testCode: () => Deserialize(document: document)).Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "'nowhere', which is neither a state row nor a declared set"
        );
    }
    [Fact]
    public void ASetSharingARowsNameIsRefusedByTheValidator() {
        var (document, _) = WorldSources.LowerSource(source: Board(
            set: "marked",
            transforms: """
              transform writeSet(row: stones, set: marked, value: 1)
            """
        ));

        Assert.Contains(
            actualString: Assert.Throws<InvalidDataException>(testCode: () => Deserialize(document: document)).Message,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "sets[0].name 'marked' is also a state.world row"
        );
    }
    [Fact]
    public void ASetDeclarationTheAlgebraRefusesReportsPuck101() => WorldSources.AssertRefused(
        label: nameof(ASetDeclarationTheAlgebraRefusesReportsPuck101),
        refusal: new(
            Body: "set backwards: zone(deck, 4..1)\n",
            Code: PuckDiagnosticCodes.CellSetExpressionInvalid,
            Needle: "set backwards"
        ) { Alone = true }
    );
}
