using Puck.Commands;
using System.Numerics;

using Puck.Maths;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Games.Tests;
/// <summary>Proves the handheld pair against the real, composed island document rather than an isolated one: the
/// module's rules land under the <c>arcade</c> import alias, and shipping them still leaves the tick's rule-work
/// sheet under its ceiling with room to spare.</summary>
public sealed class RealArcadeModuleLawTests {
    [Fact]
    public void TheArcadeModuleShipsTheHandheldPairUnderItsImportAlias() {
        var definition = AuthoredGameFixtures.Nexus;

        var pickup = Assert.Single(
            collection: definition.Rules!,
            predicate: rule => (rule.Name.ToString() == "arcade$handheld-pickup")
        );
        var release = Assert.Single(
            collection: definition.Rules!,
            predicate: rule => (rule.Name.ToString() == "arcade$handheld-release")
        );

        Assert.Contains(
            collection: pickup.Effects,
            filter: effect => ((effect is WorldEffect.UpsertPlacement upsert) && (upsert.Placement.Attach != null))
        );
        Assert.Contains(
            collection: release.Effects,
            filter: effect => ((effect is WorldEffect.UpsertPlacement upsert) && (upsert.Placement.Attach == null))
        );

        var stand = Assert.Single(
            collection: definition.Placements,
            predicate: row => (row.Id == "arcade$handheldStand")
        );

        Assert.NotNull(@object: stand.Region);
    }
    [Fact]
    public void TheIslandsRuleWorkStaysUnderTheCeilingWithHeadroomAfterTheHandheldShips() {
        var budget = WorldRuleWorkBudget.Measure(definition: AuthoredGameFixtures.Nexus);

        Assert.True(condition: (budget.WorkUnitsPerTick.Units < RuleCapacity.MaxWorkUnitsPerTick));
        // The headroom is a live measurement, not a knob; a future authoring change is free to spend it, but this
        // pins that the pair did not eat it all.
        Assert.True(condition: ((RuleCapacity.MaxWorkUnitsPerTick - budget.WorkUnitsPerTick.Units) > 10_000L));
    }
}
