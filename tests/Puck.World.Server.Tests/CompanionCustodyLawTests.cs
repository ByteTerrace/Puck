using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The companion custody records and their reducer (docs/plans/companion-redesign.md, sections 2 and 3) on
/// real authorities: every custody fact is a fold of the authority's durable records, and a restart from an earlier
/// checkpoint plus its crossing log, a checkpoint restore, and a replay's re-drive all derive the custody the live
/// authority holds. Each law is the engine form of a named trace of the custody model
/// (<c>CompanionCustodyModelLawTests</c>). Home is the source row; the companion is an incarnation it minted.</summary>
public sealed class CompanionCustodyLawTests {
    private static WorldEntityAddress Companion(CrossingWorld world) => world.Source.Server.Population.ResolveIncarnation(
        authority: world.Source.Server.AuthorityIdentity,
        index: 1
    )!.Value;
    private static WorldTransferKey Key(CrossingWorld world, ulong id) => new(
        SourceAuthority: world.Source.Server.AuthorityIdentity,
        TransferId: id
    );
    private static WorldCrossingRecord.Hop HopOf(CrossingWorld world, WorldTransferKey key, WorldEntityAddress companion, ulong epoch = 0UL) => new(
        From: world.Source.Server.AuthorityIdentity,
        Key: key,
        Members: [new WorldCustodyHopMember(Epoch: epoch, Incarnation: companion, Next: (epoch + 1UL))],
        To: world.Destination.Server.AuthorityIdentity
    );
    private static void Durable(WorldServer server, WorldCrossingRecord record) => Assert.Equal(
        actual: server.RecordCustody(
            reason: out _,
            record: record
        ),
        expected: WorldCrossingDurability.Durable
    );
    private static WorldCustodyTitle Title(WorldServer server, WorldEntityAddress companion) => server.ExecuteAuthorityOperation(operation: () => server.TransferEscrow.Custody.TitleOf(incarnation: companion))!;

    // Model trace ACrashBetweenTheHopRecordAndTheDepartureRecoversTheTitle: home recorded its own hop and died before
    // departing. The restart redoes the hop from the log, so the title is pending, not lost and not silently home; the
    // abandon settles the hop as not arrived, and the title is home again at the epoch it never left.
    [Fact]
    public void ACrashBetweenTheHopRecordAndTheDepartureRecoversTheTitle() {
        using var world = CrossingWorld.Build();
        var companion = Companion(world: world);
        var key = Key(world: world, id: 7UL);

        Durable(record: HopOf(companion: companion, key: key, world: world), server: world.Source.Server);
        Assert.Equal(actual: Title(companion: companion, server: world.Source.Server).Kind, expected: WorldCustodyKind.Pending);

        using var restarted = world.Restart(sourceDied: true, destinationDied: false);
        var home = restarted.Source.Server;
        var title = Title(companion: companion, server: home);

        Assert.Equal(actual: (title.Kind, title.PendingKey), expected: (WorldCustodyKind.Pending, (WorldTransferKey?)key));
        Assert.Equal(actual: title.MaxIssued, expected: 1UL);
        Durable(record: new WorldCrossingRecord.HopDone(Arrived: false, Incarnation: companion, Key: key), server: home);
        title = Title(companion: companion, server: home);
        Assert.Equal(actual: (title.Kind, title.Custodian, title.Epoch), expected: (WorldCustodyKind.Home, home.AuthorityIdentity, 0UL));

        // A second restart from the same pre-crossing checkpoint redoes both records to the same title.
        using var again = restarted.Restart(sourceDied: true, destinationDied: false);
        var redone = Title(companion: companion, server: again.Source.Server);

        Assert.Equal(actual: (redone.Kind, redone.Epoch, redone.PendingKey), expected: (WorldCustodyKind.Home, 0UL, (WorldTransferKey?)null));
    }
    // A checkpoint taken after custody records reflects them: its restore folds its captured history, and the log redo
    // past its watermark adds only what came after, so no record folds twice.
    [Fact]
    public void ACheckpointCarriesTheCustodyItFoldedAndRecoveryRedoesOnlyWhatFollows() {
        using var world = CrossingWorld.Build();
        var companion = Companion(world: world);
        var first = Key(world: world, id: 3UL);

        Durable(record: HopOf(companion: companion, key: first, world: world), server: world.Source.Server);
        Durable(record: new WorldCrossingRecord.HopDone(Arrived: true, Incarnation: companion, Key: first), server: world.Source.Server);
        world.CheckpointSource();
        Durable(record: new WorldCrossingRecord.Recall(Epoch: 2UL, Incarnation: companion), server: world.Source.Server);

        using var restarted = world.Restart(sourceDied: true, destinationDied: false);
        var home = restarted.Source.Server;
        var title = Title(companion: companion, server: home);

        Assert.Equal(actual: (title.Kind, title.Epoch, title.MaxIssued), expected: (WorldCustodyKind.Home, 2UL, 2UL));
        Assert.Equal(actual: title.Recalls, expected: [2UL]);
        Assert.Equal(actual: home.ExecuteAuthorityOperation(operation: () => home.TransferEscrow.Custody.History.Count), expected: 3);
    }
    // Model trace ACrashBetweenTheRecallAndItsNoticeStillNotifies: the notice is a durable obligation of its own,
    // derived from the recall record, so a restart between the two still owes it, and each recall owes its own.
    [Fact]
    public void ACrashBetweenTheRecallAndItsNoticeStillNotifies() {
        using var world = CrossingWorld.Build();
        var companion = Companion(world: world);
        var key = Key(world: world, id: 5UL);
        var home = world.Source.Server;

        Durable(record: HopOf(companion: companion, key: key, world: world), server: home);
        Durable(record: new WorldCrossingRecord.HopDone(Arrived: true, Incarnation: companion, Key: key), server: home);
        Assert.Equal(actual: (Title(companion: companion, server: home).Kind, Title(companion: companion, server: home).Epoch), expected: (WorldCustodyKind.Away, 1UL));
        Durable(record: new WorldCrossingRecord.Recall(Epoch: 2UL, Incarnation: companion), server: home);
        world.SourceLog.CrashBefore = typeof(WorldCrossingRecord.Noticed);
        _ = Assert.Throws<AuthorityCrashedException>(testCode: () => home.RecordCustody(reason: out _, record: new WorldCrossingRecord.Noticed(Epoch: 2UL, Incarnation: companion, What: WorldCustodyNotice.Recalled)));

        using var restarted = world.Restart(sourceDied: true, destinationDied: false);
        var recovered = restarted.Source.Server;

        Assert.Equal(
            actual: recovered.ExecuteAuthorityOperation(operation: () => recovered.TransferEscrow.Custody.OwedNotices()),
            expected: [(companion, WorldCustodyNotice.Recalled, 2UL)]
        );
        Durable(record: new WorldCrossingRecord.Noticed(Epoch: 2UL, Incarnation: companion, What: WorldCustodyNotice.Recalled), server: recovered);
        Assert.Empty(collection: recovered.ExecuteAuthorityOperation(operation: () => recovered.TransferEscrow.Custody.OwedNotices()));

        // A later excursion and a second recall owe a second notice.
        var later = Key(world: world, id: 6UL);

        Durable(record: HopOf(companion: companion, epoch: 2UL, key: later, world: world), server: recovered);
        Durable(record: new WorldCrossingRecord.HopDone(Arrived: true, Incarnation: companion, Key: later), server: recovered);
        Durable(record: new WorldCrossingRecord.Recall(Epoch: 4UL, Incarnation: companion), server: recovered);
        Assert.Equal(
            actual: recovered.ExecuteAuthorityOperation(operation: () => recovered.TransferEscrow.Custody.OwedNotices()),
            expected: [(companion, WorldCustodyNotice.Recalled, 4UL)]
        );
    }
    // Model trace AnUncertainHopRecordIsNotServedBeforeRecovery: an uncertain hop record that landed is not acted on
    // (the title is not pending, the grant is not current, the authority answers uncertain about the key and records
    // nothing more) until a recovered activation reads the log and folds it.
    [Fact]
    public void AnUncertainHopRecordIsNotServedBeforeRecovery() {
        using var world = CrossingWorld.Build();
        var companion = Companion(world: world);
        var key = Key(world: world, id: 9UL);
        var home = world.Source.Server;
        var destination = world.Destination.Server.AuthorityIdentity;

        world.SourceLog.LoseAnswerOn = typeof(WorldCrossingRecord.Hop);
        world.SourceLog.LostAnswerLands = true;
        Assert.Equal(
            actual: home.RecordCustody(reason: out _, record: HopOf(companion: companion, key: key, world: world)),
            expected: WorldCrossingDurability.Uncertain
        );
        Assert.Equal(actual: Title(companion: companion, server: home).Kind, expected: WorldCustodyKind.Home);
        Assert.True(condition: home.IsUncertainAbout(key: key));
        Assert.False(condition: home.IsUncertainAbout(key: Key(world: world, id: 10UL)));
        Assert.False(condition: home.ExecuteAuthorityOperation(operation: () => home.TransferEscrow.Custody.IsGrantCurrent(destination: destination, incarnation: companion, key: key, next: out _)));
        Assert.Equal(
            actual: home.RecordCustody(reason: out _, record: new WorldCrossingRecord.HopDone(Arrived: false, Incarnation: companion, Key: key)),
            expected: WorldCrossingDurability.Refused
        );

        using var restarted = world.Restart(sourceDied: true, destinationDied: false);
        var recovered = restarted.Source.Server;

        Assert.Equal(actual: Title(companion: companion, server: recovered).Kind, expected: WorldCustodyKind.Pending);
        Assert.False(condition: recovered.IsUncertainAbout(key: key));
        Assert.True(condition: recovered.ExecuteAuthorityOperation(operation: () => recovered.TransferEscrow.Custody.IsGrantCurrent(destination: destination, incarnation: companion, key: key, next: out var next) && (next == 1UL)));
    }
    // Model trace TwoReleasedCompanionsRetireInSeparateSteps: a custodian retires each copy home refused in an
    // operation of its own, one record each, so a crash between them finds the first retired and the second not.
    [Fact]
    public void TwoReleasedCompanionsRetireInSeparateSteps() {
        using var world = CrossingWorld.Build();
        var custodian = world.Destination.Server;
        var first = Companion(world: world);
        var second = world.Source.Server.Population.ResolveIncarnation(authority: world.Source.Server.AuthorityIdentity, index: 0)!.Value;
        var before = world.DestinationLog.Count;

        Durable(record: new WorldCrossingRecord.Retire(Cause: WorldCustodyRetireCause.Released, Incarnation: first), server: custodian);
        Assert.Equal(actual: world.DestinationLog.Count, expected: (before + 1));
        world.DestinationLog.CrashBefore = typeof(WorldCrossingRecord.Retire);
        _ = Assert.Throws<AuthorityCrashedException>(testCode: () => custodian.RecordCustody(reason: out _, record: new WorldCrossingRecord.Retire(Cause: WorldCustodyRetireCause.Released, Incarnation: second)));

        using var restarted = world.Restart(sourceDied: false, destinationDied: true);
        var recovered = restarted.Destination.Server;

        Assert.Equal(actual: recovered.ExecuteAuthorityOperation(operation: () => recovered.TransferEscrow.Custody.Retired.ToArray()), expected: [first]);
        // Custody belongs to the authority that minted the incarnation: a custodian holds no title for it.
        Assert.Null(@object: recovered.ExecuteAuthorityOperation(operation: () => recovered.TransferEscrow.Custody.TitleOf(incarnation: first)));
    }
    // A release installed while a hop is pending waits for that hop: the title is released only when the hop resolves.
    [Fact]
    public void AReleaseDuringAPendingHopWaitsForTheHopToResolve() {
        using var world = CrossingWorld.Build();
        var companion = Companion(world: world);
        var key = Key(world: world, id: 11UL);
        var home = world.Source.Server;

        Durable(record: HopOf(companion: companion, key: key, world: world), server: home);
        Durable(record: new WorldCrossingRecord.Release(Incarnation: companion), server: home);
        Assert.Equal(actual: Title(companion: companion, server: home).Kind, expected: WorldCustodyKind.Pending);
        Assert.False(condition: home.ExecuteAuthorityOperation(operation: () => home.TransferEscrow.Custody.IsGrantCurrent(destination: world.Destination.Server.AuthorityIdentity, incarnation: companion, key: key, next: out _)));
        Durable(record: new WorldCrossingRecord.HopDone(Arrived: true, Incarnation: companion, Key: key), server: home);
        Assert.Equal(actual: Title(companion: companion, server: home).Kind, expected: WorldCustodyKind.Released);
        Assert.Equal(
            actual: home.ExecuteAuthorityOperation(operation: () => home.TransferEscrow.Custody.OwedNotices()),
            expected: [(companion, WorldCustodyNotice.Released, 0UL)]
        );
    }
    // A replay re-drives the custody records the live authority made durable and folds them through the same reducer:
    // the authoritative hash, which folds the custody, matches tick for tick. Red leg: a tape without the custody
    // entries diverges at the tick of the first record.
    [Fact]
    public void ARecordedCustodyReplaysTickForTick() {
        using var hostRoot = new TemporaryDirectory(prefix: "puck-custody-host-");
        using var tapeRoot = new TemporaryDirectory(prefix: "puck-custody-tape-");
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: true,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: hostRoot.RootPath)
        );
        using var source = HostRow.Build(name: "row-a");
        using var destination = HostRow.Build(name: "row-b");

        host.Admit(row: source.Instance);
        host.Admit(row: destination.Instance);

        var tape = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: source.Server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: source.Server.Profiles,
            stateRoot: new WorldStateRoot(path: tapeRoot.RootPath),
            transport: ((LoopbackTransport)source.Instance.Link)
        );

        source.Instance.Tape = tape;
        for (var slot = 0; (slot < 2); slot++) {
            Assert.True(condition: source.Server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: slot),
                Slot: slot,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
        }

        var name = $"custody-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: refusal);
        void Step(int ticks) {
            for (var tick = 0; (tick < ticks); tick++) {
                host.DrainPendingTransfers();
                host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
            }
        }

        var companion = source.Server.Population.ResolveIncarnation(authority: source.Server.AuthorityIdentity, index: 1)!.Value;
        var key = new WorldTransferKey(SourceAuthority: source.Server.AuthorityIdentity, TransferId: 1UL);

        Step(ticks: 3);
        Durable(server: source.Server, record: new WorldCrossingRecord.Hop(
            From: source.Server.AuthorityIdentity,
            Key: key,
            Members: [new WorldCustodyHopMember(Epoch: 0UL, Incarnation: companion, Next: 1UL)],
            To: destination.Server.AuthorityIdentity
        ));
        Step(ticks: 2);
        Durable(server: source.Server, record: new WorldCrossingRecord.HopDone(Arrived: true, Incarnation: companion, Key: key));
        Step(ticks: 2);
        Durable(server: source.Server, record: new WorldCrossingRecord.Recall(Epoch: 2UL, Incarnation: companion));
        Step(ticks: 2);
        _ = tape.StopRecording();

        var verdict = tape.Verify(name: name);

        Assert.Equal(actual: verdict.Primary.DivergedAt, expected: -1);
        Assert.Equal(actual: Title(companion: companion, server: source.Server).Epoch, expected: 2UL);
    }
}
