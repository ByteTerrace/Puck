using Xunit;
using Rows = Puck.World.Server.Tests.CompanionRows;

namespace Puck.World.Server.Tests;

/// <summary>The explicit stop of the companion custody protocol (docs/plans/companion-redesign.md, section 5.8) on real
/// authorities: a stop never discards custody. It sends visiting companions home and retires only when, under the row's
/// authority gate, nothing visits and no accepted arrival is outstanding; a host that keeps durable images writes the
/// accepted stop into the row's image and a final image that carries whatever is still abroad, from which, with the
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

        rows.KeepDurableImages();
        rows.Cross(from: rows.Home, to: rows.B);
        Assert.Equal(actual: (rows.Title(companion: companion).Kind, rows.Title(companion: companion).Custodian), expected: (WorldCustodyKind.Away, rows.B.Server.AuthorityIdentity));

        Assert.True(condition: (rows.Host.Stop(name: "row-a", reason: out var reason) == WorldInstanceStop.Retired), userMessage: reason);
        Assert.False(condition: rows.Host.TryGet(instance: out _, name: "row-a"));
        Assert.Equal(actual: rows.ImageWrites[^1], expected: ("row-a", false));

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

        rows.KeepDurableImages();
        rows.Cross(from: rows.Home, to: rows.B);
        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: companion) >= 0);

        Assert.Equal(actual: rows.Host.Stop(name: "row-b", reason: out var reason), expected: WorldInstanceStop.Stopping);
        Assert.Contains(expectedSubstring: "sent 1 visiting companion(s) home", actualString: reason);
        rows.Step(ticks: 2);

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
        Assert.True(condition: (rows.Host.Stop(name: "row-a", reason: out reason) == WorldInstanceStop.Retired), userMessage: reason);
    }
    // An arrival the custodian accepted before the stop (its commit lost on the way, the lease still live) is waited
    // for: the stop never retires the row under a live lease. The commit lands the companion, the stop sends it home,
    // and only then does the row retire.
    [Fact]
    public void AStopWaitsForAnAcceptedArrivalAndSendsItHome() {
        using var rows = new Rows();
        var companion = rows.Companions[0];

        rows.KeepDurableImages();
        rows.Host.SetPeerCallFault(fault: new FaultingPeerCall(destination: rows.B.Server), instanceName: "row-b");
        _ = rows.Enqueue(from: rows.Home, to: rows.B);
        rows.Step(ticks: 1);
        Assert.Equal(actual: Rows.SlotOf(row: rows.B, incarnation: companion), expected: -1);

        Assert.Equal(actual: rows.Host.Stop(name: "row-b", reason: out var reason), expected: WorldInstanceStop.Stopping);
        Assert.Contains(expectedSubstring: "has accepted", actualString: reason);
        Assert.False(condition: rows.B.Server.IsRetiring);

        rows.Step(ticks: 6);

        Assert.False(condition: rows.Host.TryGet(instance: out _, name: "row-b"));
        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: companion), expected: companion.Index);
        Assert.Equal(actual: rows.Title(companion: companion).Kind, expected: WorldCustodyKind.Home);
    }
    // The final check and the closed admission are one hold of the authority gate: an authority operation another
    // thread asks for while the check runs waits for the gate and then meets the frozen activation, never running.
    [Fact]
    public void NothingLandsBetweenTheFinalCheckAndTheFreeze() {
        using var rows = new Rows();
        var server = rows.C.Server;
        var ran = false;
        Exception? refused = null;
        Thread? other = null;

        Assert.True(condition: server.TryFreezeForRetirement(admits: () => {
            other = new Thread(start: () => {
                try {
                    _ = server.ExecuteAuthorityOperation(operation: () => ran = true);
                } catch (InvalidOperationException exception) {
                    refused = exception;
                }
            });
            other.Start();
            Assert.False(condition: other.Join(millisecondsTimeout: 200));
            return true;
        }));
        other!.Join();

        Assert.False(condition: ran);
        Assert.NotNull(@object: refused);
    }
    // A row frozen for retirement while still admitted (a silo row whose final image is uploading) is skipped by the
    // drain's custody passes instead of throwing out of the host step.
    [Fact]
    public void AFrozenRowStillAdmittedDoesNotStopTheDrain() {
        using var rows = new Rows();

        rows.Cross(from: rows.Home, to: rows.B);
        rows.B.Server.FreezeForRetirement();

        rows.Step(ticks: 3);

        Assert.True(condition: rows.Host.TryGet(instance: out _, name: "row-b"));
        Assert.Equal(actual: rows.Host.Stop(name: "row-b", reason: out var reason), expected: WorldInstanceStop.Refused);
        Assert.Contains(expectedSubstring: "retiring", actualString: reason);
    }
    // A departure in doubt toward a row that then freezes for retirement waits for the activation that replaces it,
    // instead of asking the frozen row and throwing out of the host step.
    [Fact]
    public void ADepartureInDoubtWaitsForAFrozenDestinationsSuccessor() {
        using var rows = new Rows();

        rows.Host.SetPeerCallFault(fault: new FaultingPeerCall(destination: rows.B.Server), instanceName: "row-b");
        _ = rows.Enqueue(from: rows.Home, to: rows.B);
        rows.Step(ticks: 1);
        rows.B.Server.FreezeForRetirement();

        rows.Step(ticks: 3);

        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: rows.Player), expected: -1);
        Assert.Equal(actual: Rows.SlotOf(row: rows.B, incarnation: rows.Player), expected: -1);
    }
    // A custodian whose only body is the visiting companion becomes empty when the stop sends it home, but reaping does
    // not take it: it retires through its stop, after its final image, and a refused final image keeps it frozen and
    // admitted until a later drain writes one.
    [Fact]
    public void AnEmptiedStoppingCustodianRetiresThroughItsFinalImage() {
        using var rows = new Rows(cDeclaresRole: false);
        var companion = rows.Companions[0];
        var refusing = true;

        rows.KeepDurableImages(refuse: name => (refusing && (name == "row-b")));
        rows.Cross(from: rows.Home, to: rows.B);
        rows.Cross(from: rows.B, to: rows.C);
        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: companion) >= 0);
        Assert.True(condition: Rows.SlotOf(row: rows.C, incarnation: rows.Player) >= 0);

        Assert.Equal(actual: rows.Host.Stop(name: "row-b", reason: out _), expected: WorldInstanceStop.Stopping);
        rows.Step(ticks: 4);

        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: companion), expected: companion.Index);
        Assert.True(condition: rows.Host.TryGet(instance: out _, name: "row-b"));
        Assert.True(condition: rows.B.Server.IsRetiring);
        Assert.DoesNotContain(collection: rows.ImageWrites, filter: static write => (write == ("row-b", false)));

        refusing = false;
        rows.Step(ticks: 1);

        Assert.False(condition: rows.Host.TryGet(instance: out _, name: "row-b"));
        Assert.Equal(actual: rows.ImageWrites[^1], expected: ("row-b", false));
    }
    // An accepted stop is in the row's image before it is acknowledged: a custodian that dies before its swept return
    // left restarts from that image still stopping, sends the companion home and retires.
    [Fact]
    public void AStopSurvivesACrashBeforeItsReturnLeaves() {
        using var rows = new Rows();
        var companion = rows.Companions[0];

        rows.KeepDurableImages();
        rows.Cross(from: rows.Home, to: rows.B);
        Assert.Equal(actual: rows.Host.Stop(name: "row-b", reason: out _), expected: WorldInstanceStop.Stopping);
        Assert.Contains(collection: rows.ImageWrites, expected: ("row-b", true));

        rows.Restart(name: "row-b");
        Assert.True(condition: Rows.SlotOf(row: rows.B, incarnation: companion) >= 0);
        Assert.True(condition: rows.Host.TryDescribeCustody(custody: out _, name: "row-b", stopping: out var stopping) && stopping);

        rows.Step(ticks: 4);

        Assert.False(condition: rows.Host.TryGet(instance: out _, name: "row-b"));
        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: companion), expected: companion.Index);
        Assert.Equal(actual: (rows.Title(companion: companion).Kind, rows.Title(companion: companion).Epoch), expected: (WorldCustodyKind.Home, 2UL));
    }
    // A stop is refused while a visiting companion's home runs nowhere in this host: nothing could send it home, and a
    // final image would carry it with no return to resume. Once home runs again the stop sends it there.
    [Fact]
    public void AStopIsRefusedWhileAVisitorsHomeIsDown() {
        using var rows = new Rows();
        var companion = rows.Companions[0];

        rows.KeepDurableImages();
        rows.Cross(from: rows.Home, to: rows.B);
        Assert.True(condition: (rows.Host.Stop(name: "row-a", reason: out var reason) == WorldInstanceStop.Retired), userMessage: reason);

        Assert.Equal(actual: rows.Host.Stop(name: "row-b", reason: out reason), expected: WorldInstanceStop.Refused);
        Assert.Contains(expectedSubstring: "no row this host runs", actualString: reason);
        Assert.True(condition: rows.Host.TryGet(instance: out _, name: "row-b"));

        rows.Activate(name: "row-a");
        Assert.True(condition: (rows.Host.Stop(name: "row-b", reason: out reason) == WorldInstanceStop.Stopping), userMessage: reason);
        rows.Step(ticks: 4);

        Assert.False(condition: rows.Host.TryGet(instance: out _, name: "row-b"));
        Assert.Equal(actual: Rows.SlotOf(row: rows.Home, incarnation: companion), expected: companion.Index);
    }
}
