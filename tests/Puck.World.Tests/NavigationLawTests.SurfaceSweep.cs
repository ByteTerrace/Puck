using Puck.Maths;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class NavigationLawTests {
    // A surface edge's lowest sweep rides maxStepHeight above the foot, so a diagonal across a flat floor is proven
    // in a handful of march steps and admitted; a sweep skimming the floor at its own contact skin advances one skin
    // per step and spends its budget short of the far cell, refusing every diagonal.
    [Fact]
    public void SurfaceNavigationAdmitsDiagonalEdgesAcrossAFlatFloor() {
        var definition = WithFloor(definition: NavigationDocument(domain: SurfaceDomain()));
        using var fixture = Fixtures.FreshServer(definition: definition);
        _ = JoinNavigator(
            fixture: fixture,
            goal: new FixedVector3(X: FixedQ4816.FromInteger(value: 3), Y: FixedQ4816.Zero, Z: FixedQ4816.FromInteger(value: 3))
        );
        fixture.Step();

        var route = Assert.IsType<WorldPopulation.WorldPopulationNavigationCheckpoint>(
            @object: fixture.Server.Population.Capture().Entries.Single(row => row.Index == 0).Navigation
        );

        Assert.Contains(expectedSubstring: "clear=16/16", actualString: fixture.Server.Population.DescribeNavigation(), comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: 4, actual: route.Path.Length);
    }
}
