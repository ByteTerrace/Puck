using Puck.Commands;
using Puck.Networking;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>A replay keeps the crossing ledger its recording kept, although it has no closed rewind group's boundary to
/// ask. Whether a source is a guest's session is decided once, at the live world's door, and written into the arrival
/// the tape carries; the shadow that re-drives the tape marks the session from those bytes, counts its rows as a
/// guest's, and forgets the session on the tick the live world did, when its last traveler is retired.</summary>
public sealed class GuestLedgerReplayLawTests {
    private const string Issuer = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task AReplayReclaimsAGuestsSessionOnTheTickTheLiveWorldDid() {
        var ct = TestContext.Current.CancellationToken;
        var document = Fixtures.PeerPopulationDocument(networkPlayers: 2);

        // No reconnect grace: the guest's traveler is retired on the tick that takes its disconnect.
        document = document with { PopulationRaw = document.Population with { ReconnectGraceSeconds = 0f } };
        using var hostRoot = new TemporaryDirectory(prefix: "puck-guest-ledger-replay-host-");
        using var tapeRoot = new TemporaryDirectory(prefix: "puck-guest-ledger-replay-tape-");
        using var host = new WorldInstanceHost(
            applicationStopping: CancellationToken.None,
            admitsSpawn: true,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: hostRoot.RootPath)
        );
        using var row = HostRow.Build(definition: document, name: "row-a");
        var server = row.Server;

        // The live world is a closed rewind group of one that admits the issuer's players as guests.
        server.ConstrainTransferAuthorities(boundary: new WorldTransferBoundary(
            contains: name => (name == server.AuthorityIdentity),
            guestIssuer: Issuer
        ));
        host.Admit(row: row.Instance);
        var tape = new WorldReplayTape(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            liveServer: server,
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: server.Profiles,
            stateRoot: new WorldStateRoot(path: tapeRoot.RootPath),
            transport: ((LoopbackTransport)row.Instance.Link)
        );

        row.Instance.Tape = tape;
        var live = new List<WorldTransferTableCounts>();

        void Step(int ticks) {
            for (var tick = 0; (tick < ticks); tick++) {
                host.DrainPendingTransfers();
                host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
                live.Add(item: server.ExecuteAuthorityOperation(operation: () => server.TransferEscrow.Counts));
            }
        }
        var name = $"guest-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: refusal);
        Step(ticks: 3);

        // A guest's seat is leased and lands: its session holds a key, a floor and a traveler's credential.
        var run = Guid.NewGuid().ToString(format: "N");
        var session = $"{Issuer}/player-one/{run}";
        var guest = WorldTransferCaller.Authenticated(identity: new VerifiedPeerIdentity(
            Authority: session,
            Session: new VerifiedSession(Domain: Issuer, Run: run, Subject: "player-one", Vouched: true),
            ValidThrough: long.MaxValue
        ));
        var minted = new WorldEntityAddress(Authority: session, Generation: 1, Index: 0);
        var reserved = await server.ReserveTransferAsync(
            caller: guest,
            ct: ct,
            request: new WorldTransferReservationRequest(
                Border: string.Empty,
                BorderCapacity: null,
                DeadlineSourceTick: 600,
                Members: [new WorldTransferReservationMember(
                    BodyColor: default,
                    CatalogRig: 0,
                    Identity: null,
                    Mobility: new WorldMobilityIdentity(DepartedFrom: minted, Epoch: 0, Incarnation: minted),
                    PreferredSlot: -1,
                    Principal: Principal.Seat(slot: 0),
                    Source: IntentSource.Live
                )],
                PartyAllOrNothing: true,
                PeerAdmission: true,
                SourceAuthority: session,
                SourceRateHz: server.Configuration.SimulationRateHz,
                SourceTick: 0,
                TransferId: 1
            )
        );

        Assert.True(condition: reserved.Accepted, userMessage: reserved.Reason);
        var committed = await server.CommitTransferAsync(
            caller: guest,
            ct: ct,
            members: [new WorldTransferCommitMember(
                Arrival: WorldTransferArrival.Spawn,
                BodyMotionProgramName: string.Empty,
                PlanarVelocity: default,
                Position: default,
                Profile: null,
                VerticalVelocity: default,
                YawRadians: default
            )],
            sourceAuthority: session,
            transferId: 1
        );

        Assert.True(condition: (committed.Status == WorldTransferStatus.Committed), userMessage: committed.Reason);
        Step(ticks: 3);
        var present = live[^1];

        Assert.Equal(expected: 1, actual: present.MobilityCredentials);
        Assert.NotEqual(expected: 0, actual: present.GuestTransferKeys);

        // The guest's traveler is retired where it stands, and the session then holds nothing live: every row goes.
        Assert.True(condition: server.DisconnectPeer(
            actor: Principal.Console,
            peer: server.ExecuteAuthorityOperation(operation: () => server.Population.PeerPrincipal(index: reserved.BodyIndices[0]))
        ));
        Step(ticks: 4);
        Assert.Equal(
            expected: new WorldTransferTableCounts(ActiveTransactions: 0, GuestTransferKeys: 0, MobilityCredentials: 0, MobilityLeases: 0, TransferKeys: 0),
            actual: live[^1]
        );
        var stop = tape.StopRecording();

        Assert.Null(@object: stop.VerifyFault);
        Assert.True(condition: stop.Verdict!.Match, userMessage: stop.Verdict.Describe());

        // The shadow has no boundary. Its ledger equals the live world's on every recorded tick.
        WorldReplaySnapshot recorded;

        using (var stream = File.OpenRead(path: tape.PathFor(name: name))) {
            recorded = WorldReplaySnapshot.Read(stream: stream);
        }
        var replayed = new WorldTransferTableCounts[recorded.Ticks.Count];
        var bounded = false;

        _ = recorded.DriveTraces(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            observeTick: (tick, shadow) => {
                bounded |= (shadow.TransferBoundary is not null);
                replayed[tick] = shadow.TransferEscrow.Counts;
            },
            profiles: server.Profiles
        );
        Assert.False(condition: bounded, userMessage: "the shadow carried a boundary, so the ledger was not decided from the tape alone");
        Assert.Equal(expected: live, actual: replayed);
    }
}
