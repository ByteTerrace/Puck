using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>
/// CONTRACT UNDER TEST: an absent list lowers to absent, never to <c>[]</c>. Composition replaces a list that is present
/// and keeps the imported rows beneath one that is absent, so a section that writes only its own properties
/// (<c>placements { policy { … } }</c>) lowers without a <c>rows</c> list, a pool that seeds nothing lowers without
/// <c>initial</c>, and a list the author wrote empty (<c>rows []</c>, <c>layouts []</c>) lowers, and decompiles, as the
/// empty list it is.
/// </summary>
public class AbsentListLoweringLawTests {
    private const string Policy = """
        placements {
            policy {
                candidateCap: 1
            }
        }
        """;
    private const string PoolState = """
        state {
            world {
                slot done = 0
            }

            record Actor {
                score: Int = 7
            }

            pool actors of Actor capacity(1)
        }
        """;

    [Fact]
    public void APlacementsSectionThatWritesOnlyItsPolicyLowersWithoutRows() {
        var placements = Assert.IsType<JsonObject>(@object: WorldSources.LowerClean(body: Policy)["placements"]);

        Assert.False(
            condition: placements.ContainsKey(propertyName: "rows"),
            userMessage: "the section wrote no row, so its rows are absent and compose with the imported modules' rows"
        );
        Assert.Equal(
            actual: placements["policy"]!["candidateCap"]!.GetValue<long>(),
            expected: 1L
        );
    }
    [Fact]
    public void APlacementsSectionThatWritesARowLowersItsRows() {
        var placements = Assert.IsType<JsonObject>(@object: WorldSources.LowerClean(body: """
            placements {
                placement "a" {
                    prototype: "p"
                    position [0, 0, 0]
                }
            }
            """)["placements"]);

        _ = Assert.Single(collection: Assert.IsType<JsonArray>(@object: placements["rows"]));
    }
    [Fact]
    public void AnAuthoredEmptyRowsListLowersAsTheEmptyListItIs() {
        var placements = Assert.IsType<JsonObject>(@object: WorldSources.LowerClean(body: """
            placements {
                rows []
            }
            """)["placements"]);

        Assert.Empty(collection: Assert.IsType<JsonArray>(@object: placements["rows"]));
    }
    [Fact]
    public void APoolThatSeedsNothingLowersWithoutInitial() {
        var pool = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: WorldSources.LowerClean(body: PoolState)["state"]!["pools"])[0]);

        Assert.False(
            condition: pool.ContainsKey(propertyName: "initial"),
            userMessage: "a pool that seeds nothing carries no list, so a restated pool composes with the imported pool's seeds"
        );
    }
    [Fact]
    public void AnAbsentRowsListAndAnEmptyOneEachDecompileToTheirOwnSource() {
        foreach (var body in new[] { Policy, "placements {\n    rows []\n}" }) {
            var lowered = WorldSources.LowerClean(body: body);

            _ = WorldSources.AssertRoundTrips(original: lowered);
        }
    }
    [Fact]
    public void ADocumentWithEmptyViewListsDecompilesToTheLists() {
        var document = WorldSources.Canonical(json: """
            {
                "schema": "puck.world.definition.v1",
                "views": { "layouts": [], "graphs": [], "post": [] }
            }
            """);

        _ = WorldSources.AssertRoundTrips(original: document);
    }
    [Fact]
    public void APoolWithAnEmptyInitialListDecompilesToThatList() {
        var lowered = WorldSources.LowerClean(body: PoolState);
        var pool = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: lowered["state"]!["pools"])[0]);

        pool["initial"] = new JsonArray();
        _ = WorldSources.AssertRoundTrips(original: lowered);
    }
}
