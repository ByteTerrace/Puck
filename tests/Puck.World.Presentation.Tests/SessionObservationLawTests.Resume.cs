using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed partial class SessionObservationLawTests {
    [Theory]
    [InlineData(WorldDisclosureTier.Replica)]
    [InlineData(WorldDisclosureTier.Presentation)]
    public void ResumingObservationSeedsOnlyTheCurrentDisclosedImage(WorldDisclosureTier tier) {
        var row = new WorldStateRow(CellName.Parse("hand"), CellKind.Int,
            Visibility: new StateVisibility(Hidden: HiddenCells.Omit), Cells: [
                new StateCell(CellName.Parse("pub"), CellValue.Int(3)),
                new StateCell(CellName.Parse("secret"), CellValue.Int(7), Visibility: new StateVisibility(Readers: ["seat1"]))]);
        using var fixture = Fixtures.FreshServer(Document(tier) with { StateRaw = new WorldStateSection(World: [row]) });
        var (observation, mirror, refusal) = Observe(fixture);
        Assert.NotNull(observation);
        Assert.Empty(refusal);
        using var released = observation;
        var grant = Assert.Single(fixture.Server.GrantRows(observation.Session),
            value => value.Capability == WorldCapability.Observe && value.Subject == GrantSubject.All);
        fixture.Server.Revoke(actor: Principal.Console, grant: grant);
        fixture.Step();
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(Principal.Console, row with {
            Cells = [row.Cells![0] with { Value = CellValue.Int(9) }, row.Cells[1] with { Value = CellValue.Int(19) }]
        }));
        fixture.Step();
        fixture.Server.Grant(actor: Principal.Console, grant: grant);
        fixture.Step();
        var delivered = Assert.Single(mirror.Definition.AuthoredState, value => value.Name.Value == "hand");
        var cell = Assert.Single(delivered.Cells!);
        Assert.Equal("pub", cell.Key.Value);
        Assert.Equal(9, cell.Value.AsInt);
    }
}
