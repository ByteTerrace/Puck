using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The <c>set</c> declaration against the <c>sets</c> document member: the cell-set algebra reads the
/// authored expression, the document carries the parsed tree, and the decompiler prints the same text back.</summary>
public class CellSetDeclarationTests {
    private static (JsonObject Document, DiagnosticBag Diagnostics) Lower(string source) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.NotNull(@object: compilation.Json);

        return (compilation.Json, compilation.Diagnostics);
    }

    [Fact]
    public void ASetDeclarationLowersToTheSetsMemberAndDecompilesToTheSameText() {
        var (document, diagnostics) = Lower(source: """
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
    [Fact]
    public void ASetDeclarationTheAlgebraRefusesReportsPuck101() {
        var (_, diagnostics) = Lower(source: """
            schema: "puck.world.definition.v1"

            set backwards: zone(deck, 4..1)
            """);
        var error = Assert.Single(collection: diagnostics, predicate: d => (d.Severity == DiagnosticSeverity.Error));

        Assert.Equal(PuckDiagnosticCodes.CellSetExpressionInvalid, error.Code);
    }
}
