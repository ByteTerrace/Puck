using System.Text;

using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>Exercises the pure <c>Engine/</c> core directly (linked as source — see the project's own remarks on why
/// a browser-wasm exe cannot be referenced) over inline documents.</summary>
public sealed class BrowserEngineTests {
    [InlineData("bodies.localSeats -1 is outside 0..4 (the host's seat ceiling).", "bodies.localSeats")]
    [InlineData("an addon requires a name.", null)]
    [InlineData("screens[0].index 5 is duplicated.", "screens[0].index")]
    [Theory]
    public void BrowserErrorPaths_splits_the_leading_path_token_when_present(string message, string? expectedPath) {
        var split = BrowserErrorPaths.Split(message: message);

        Assert.Equal(
            expected: expectedPath,
            actual: split.Path
        );
    }
    [Fact]
    public void Cells_compiles_a_standalone_grid_topology() {
        const string TopologyJson = /*lang=json*/ """
            { "$type": "grid", "name": "g", "width": 4, "depth": 4, "cellSize": 1, "band": 0.3, "origin": [0, 0, 0] }
            """;

        var ok = BrowserTopology.TryCells(
            cells: out var cells,
            reason: out var reason,
            topologyJson: TopologyJson
        );

        Assert.True(
            condition: ok,
            userMessage: reason
        );
        Assert.Equal(
            expected: 16,
            actual: cells.Count
        );
        Assert.Equal(
            expected: "0",
            actual: cells[0].Key
        );
    }
    [Fact]
    public void Parse_malformed_json_refuses_without_throwing() {
        var result = BrowserParser.Parse(utf8Json: Encoding.UTF8.GetBytes(s: "{ not json"));

        Assert.False(condition: result.Ok);
        Assert.NotEmpty(collection: result.Errors!);
    }
    [Fact]
    public void Parse_wrong_schema_refuses_by_name() {
        var result = BrowserParser.Parse(utf8Json: Encoding.UTF8.GetBytes(s: /*lang=json*/ """{"schema":"not.a.real.schema"}"""));

        Assert.False(condition: result.Ok);
        Assert.Null(@object: result.Document);
        Assert.Contains(
            collection: result.Errors!,
            filter: error => error.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "not.a.real.schema"
            )
        );
    }
}
