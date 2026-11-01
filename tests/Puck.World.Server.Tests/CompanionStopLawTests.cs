using Xunit;
using Rows = Puck.World.Server.Tests.CompanionRows;

namespace Puck.World.Server.Tests;

/// <summary>The explicit stop of the companion custody protocol (docs/plans/companion-redesign.md, section 5.8) on real
/// authorities: a stop never discards custody. It sends visiting companions home and retires after the drain, and a
/// host that keeps durable images writes a final checkpoint that carries whatever is still abroad, from which, with the
/// crossing log, a later activation derives the same titles. A host that keeps none refuses to stop a home whose title is
/// abroad.</summary>
public sealed class CompanionStopLawTests {
    // Home stops after its player and companion moved on: its final image carries the away title, a later activation
    // recovered from that image and the log holds it at the same custodian and epoch, and the companion's return lands
    // in the held slot one epoch above.
    [Fact]
    public void AStoppedHomeRecoversItsAwayTitleFromItsFinalImage() {
        using var rows = new Rows();
        var companion = rows.Companions[0];

        rows.KeepFinalImages();
        rows.Cross(from: rows.Home, to: rows.B);
        Assert.Equal(actual: (rows.Title(companion: companion).Kind, rows.Title(companion: companion).Custodian), expected: (WorldCustodyKind.Away, rows.B.Server.AuthorityIdentity));

        Assert.Equal(actual: rows.Host.Stop(name: "row-a", reason: out var reason), expected: WorldInstanceStop.Stopping);
        Assert.Contains(expectedSubstring: "away at 'row-b' epoch 1", actualString: reason);
        rows.Step(ticks: 1);
        Assert.False(condition: rows.Host.TryGet(instance: out _, name: "row-a"));

        rows.Activate(name: "row-a");
        var title = rows.Title(companion: companion);

        Assert.Equal(actual: (title.Kind, title.Custodian, title.Epoch), expected: (WorldCustodyKind.Away, rows.B.Server.AuthorityIdentity, 1UL));
        Assert.False(condition: rows.Home.Server.Population.IsActive(index: companion.Index));

        Assert.True(condition: rows.Host.TryEnqueueCompanionReturn(reason: out reason, slot: Rows.SlotOf(row: rows.B, incarnation: companion), sourceInstance: "row-b", transferId: out _), userMessage: reason);
        rows.Step(ticks: 4);

        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: companion), expected: companion.Index);
        Assert.Equal(actual: (rows.Title(companion: companion).Kind, rows.Title(companion: companion).Epoch), expected: (WorldCustodyKind.Home, 2UL));
    }
    // A custodian stops with the companion embodied: the stop sends it home first and retires after the drain, and its
    // final image holds no copy of it.
    [Fact]
    public void AStoppedCustodianSendsItsVisitingCompanionHome() {
        using var rows = new Rows();
        var companion = rows.Companions[0];

        rows.KeepFinalImages();
        rows.Cross(from: rows.Home, to: rows.B);
        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: companion) >= 0);

        Assert.Equal(actual: rows.Host.Stop(name: "row-b", reason: out var reason), expected: WorldInstanceStop.Stopping);
        Assert.Contains(expectedSubstring: "1 visiting companion(s) sent home", actualString: reason);
        rows.Step(ticks: 1);

        Assert.False(condition: rows.Host.TryGet(instance: out _, name: "row-b"));
        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: companion), expected: companion.Index);
        Assert.Equal(actual: (rows.Title(companion: companion).Kind, rows.Title(companion: companion).Epoch), expected: (WorldCustodyKind.Home, 2UL));

        rows.Activate(name: "row-b");
        Assert.Equal(actual: Rows.SlotOf(row: rows.B, incarnation: companion), expected: -1);
    }
    // Without durable images a home whose title is abroad is not stopped: it keeps running and the title stays away. Once
    // the companion is home again the same stop retires it.
    [Fact]
    public void AHostWithoutDurableImagesRefusesToStopAHomeWithATitleAbroad() {
        using var rows = new Rows();
        var companion = rows.Companions[0];

        rows.Cross(from: rows.Home, to: rows.B);

        Assert.Equal(actual: rows.Host.Stop(name: "row-a", reason: out var reason), expected: WorldInstanceStop.Refused);
        Assert.Contains(expectedSubstring: "no durable image", actualString: reason);
        rows.Step(ticks: 1);
        Assert.True(condition: rows.Host.TryGet(instance: out _, name: "row-a"));
        Assert.Equal(actual: rows.Title(companion: companion).Kind, expected: WorldCustodyKind.Away);

        Assert.True(condition: rows.Host.TryEnqueueCompanionReturn(reason: out reason, slot: Rows.SlotOf(row: rows.B, incarnation: companion), sourceInstance: "row-b", transferId: out _), userMessage: reason);
        rows.Step(ticks: 4);
        Assert.Equal(actual: rows.Host.Stop(name: "row-a", reason: out reason), expected: WorldInstanceStop.Retired);
    }
}
