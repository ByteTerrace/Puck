using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>CONTRACT UNDER TEST: the Klondike document <c>puck bench world</c> deals from loads the way the host loads a
/// world (composed, then validated against the current schema) and carries the game's state row, so a member the schema
/// has retired cannot sit in it unseen: the bench catches a load failure and prints it as a row's error.</summary>
public sealed class KlondikeBenchFixtureLawTests {
    [Fact]
    public void TheBenchFixtureLoadsAndCarriesTheKlondikeGame() {
        var definition = AuthoredGameFixtures.Load(relativePath: "src/Puck.Cli/Bench/klondike.fixture.puck");

        Assert.Contains(
            collection: definition.State,
            filter: static row => (row.Name.Value == "solitaireKlondike")
        );
    }
}
