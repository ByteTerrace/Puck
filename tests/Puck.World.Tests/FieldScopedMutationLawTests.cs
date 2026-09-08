using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Two edits to sibling fields of one whole-section row, queued in the same tick, both land: each
/// field-scoped mutation composes against the row as it stands when it applies, never against the snapshot the
/// console read when it was typed.</summary>
public sealed class FieldScopedMutationLawTests {
    [Fact]
    public void PopulationSpawnAndCensusQueuedTogetherBothLand() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument());
        var before = fixture.Server.Definition.Population;
        var distribution = new WorldDistribution(Region: new WorldDistributionRegion.Disc(Radius: 9f, SampleCount: 16), Fill: before.Distribution.Fill);
        IReadOnlyList<SeatActivationPolicy> activation = [.. Enumerable.Range(0, WorldBodiesLimits.LocalSeatCount).Select(static slot => ((slot < 1) ? SeatActivationPolicy.Eager : SeatActivationPolicy.OnDemand))];

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetPopulationDistribution(Principal: WorldPrincipal.Console, Distribution: distribution));
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetPopulationCensus(Principal: WorldPrincipal.Console, SeatActivation: activation, NetworkPlayers: 0));
        Assert.True(fixture.Server.DrainAdministrative());

        var after = fixture.Server.Definition.Population;
        var disc = Assert.IsType<WorldDistributionRegion.Disc>(after.Distribution.Region);
        Assert.Equal(9f, disc.Radius);
        Assert.Equal(16, disc.SampleCount);
        Assert.Equal(activation, after.SeatActivation);
        Assert.Equal(0, after.NetworkPlayers);
    }

    [Fact]
    public void SeatRigAndSeatControlQueuedTogetherBothLand() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.BuildDocument());
        var views = fixture.Server.Definition.Views;
        var rig = views.SeatRig with {
            Operations = [.. views.SeatRig.Operations.Select(static op => (op is WorldCameraProgramOp.Orbit orbit) ? (orbit with { Distance = 9f }) : op)],
        };
        var control = views.SeatControl with { MinPitch = -0.2f };

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetViewSeatRig(Principal: WorldPrincipal.Console, SeatRig: rig));
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetViewSeatControl(Principal: WorldPrincipal.Console, SeatControl: control));
        Assert.True(fixture.Server.DrainAdministrative());

        var after = fixture.Server.Definition.Views;
        Assert.Equal(rig, after.SeatRig);
        Assert.Equal(-0.2f, after.SeatControl.MinPitch);
        Assert.Equal(views.Layouts, after.Layouts);
    }
}
