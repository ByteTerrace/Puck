using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a host that writes a <c>placements</c> section without rows composes with the rows beneath it,
/// the basis's own and the imported module's, and a host that writes <c>rows []</c> replaces them. An absent list
/// lowers to absent and an authored empty list to the empty list it is, and composition replaces a list only when the
/// host's is present.
/// </summary>
public sealed class PlacementsSectionCompositionLawTests {
    private const string EmptyRowsHost = "tests/Puck.World.Tests/Fixtures/placements-empty-rows-host.puck";
    private const string PolicyHost = "tests/Puck.World.Tests/Fixtures/placements-policy-host.puck";

    [Fact]
    public void AHostSectionWithoutRowsComposesWithTheBasisAndModuleRows() {
        var definition = AuthoredGameFixtures.Load(relativePath: PolicyHost);

        Assert.Contains(
            collection: definition.Placements,
            filter: static placement => (placement.Id == "groundPlane")
        );
        Assert.Contains(
            collection: definition.Placements,
            filter: static placement => placement.Id.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "module$"
            )
        );
    }
    [Fact]
    public void AHostSectionThatWritesAnEmptyRowsListReplacesThem() {
        Assert.Empty(collection: AuthoredGameFixtures.Load(relativePath: EmptyRowsHost).Placements);
    }
}
