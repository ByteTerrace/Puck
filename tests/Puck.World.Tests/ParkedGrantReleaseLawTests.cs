using Xunit;

using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: park-with-grace defers a disconnected peer's BODY teardown, never its AUTHORITY — authority follows the
/// CONNECTION. A generation's grant rows are released at the disconnect event itself, on the same path an
/// authored-zero grace already took, and a checkpoint restore releases a restored park's the same way; the body
/// meanwhile parks exactly as before (same pose, same occupancy, same deadline). A verified-identity reconnect
/// resumes the parked BODY (<see cref="WorldPopulation.TryResumeParkedPeer"/>) and re-mints its admission templates
/// through the ordinary <c>PeerAdmitted</c> event, so what does not survive the gap is exactly a live acquisition
/// beyond the templates — an exclusive subject freed at the disconnect stays with whoever took it since.
/// <para>The discriminating pair is the PRINCIPAL: a local seat's rows survive a leave untouched (the one
/// participant a re-join resumes onto with its table intact); a peer's go at the event and come back as a fresh
/// mint. Both halves are measured here against the same grant table.</para>
/// </summary>
public sealed class ParkedGrantReleaseLawTests {
    private const int PeerBodyIndex = 4;
    private const int PeerGeneration = 1;

    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [],
        AppliedTransferHighWater: null,
        AppliedTransferIds: [],
        ElapsedEngineTicks: 0,
        ForwardedBodies: [],
        FreshCounter: 0,
        InDoubtTransfers: [],
        IsPaused: false,
        NextTransferId: 1,
        PortalOccupancy: [],
        Retained: false,
        ScheduleAccumulatorTicks: 0,
        SeededArrivals: []
    );
    private static WorldDefinition WithGrace(float seconds, int rateHz = 240) {
        var definition = Fixtures.BuildDocument();

        return (definition with {
            PopulationRaw = (definition.Population with {
                CapacityRaw = (definition.Population.LocalSeats + 1),
                NetworkPlayers = 1,
                ReconnectGraceSeconds = seconds,
            }),
            Simulation = new WorldSimulationDefaults(RateHz: rateHz),
        });
    }
    private static WorldPeerEventEntry PeerEntry(WorldPrincipal peer) => new(
        BodyIndex: PeerBodyIndex,
        Generation: peer.Generation,
        Source: IntentSource.Live,
        Identity: peer,
        IdentityDomain: "test",
        IdentitySubject: "peer",
        AuthorityTransferred: false,
        PlacementId: null,
        CatalogRig: 0
    );
    // Admits the peer and hands it an EXCLUSIVE Control hold over its own body — the subject every law below measures
    // against. Control rather than Drive on purpose: WorldServer.Grant refuses a Drive row over an admitted peer's
    // body for any other principal by name, which would mask the exclusivity question this suite is asking.
    private static WorldPrincipal AdmitPeer(WorldFixture fixture) {
        var peer = WorldPrincipal.Peer(
            index: PeerBodyIndex,
            generation: PeerGeneration
        );

        fixture.Server.ApplyServerEvent(serverEvent: new WorldServerEvent.PeerAdmitted(
            Entries: [
                PeerEntry(peer: peer),
            ],
            MintedGrants: []
        ));

        // The population owns the generation the admitted entry actually took, so the identity every later check
        // reads is resolved from it rather than assumed.
        peer = fixture.Server.Population.PeerPrincipal(index: PeerBodyIndex);

        fixture.Server.Grant(
            actor: WorldPrincipal.Console,
            grant: new WorldGrant(
                Principal: peer,
                Capability: WorldCapability.Control,
                Subject: GrantSubject.Body(index: PeerBodyIndex),
                Exclusive: true
            )
        );

        return peer;
    }
    private static WorldServerEvent.PeerDisconnected DisconnectEvent(WorldFixture fixture, WorldPrincipal peer) =>
        // The identical shape WorldServer.ApplyLifecycleEvents dispatches and WorldReplaySnapshot re-drives: the
        // entries, plus the generation's live rows snapshotted at dispatch time.
        new(
            Entries: [
                PeerEntry(peer: peer),
            ],
            RevokedGrants: [.. fixture.Server.GrantRows(principal: peer)]
        );
    private static void Disconnect(WorldFixture fixture, WorldPrincipal peer) =>
        fixture.Server.ApplyServerEvent(serverEvent: DisconnectEvent(
            fixture: fixture,
            peer: peer
        ));
    private static bool Holds(WorldFixture fixture, WorldPrincipal principal, WorldCapability capability, GrantSubject subject) =>
        fixture.Server.Grants.Held(principal: principal).Contains(value: (capability, subject));
    private static bool Controls(WorldFixture fixture, WorldPrincipal principal) =>
        fixture.Server.Grants.Allows(
            capability: WorldCapability.Control,
            principal: principal,
            subject: GrantSubject.Body(index: PeerBodyIndex)
        ).IsAllowed;

    /// <summary>THE cure's own law. A subject a peer generation holds exclusively is acquirable by another principal
    /// the moment that peer disconnects — no waiting for a grace deadline that only governs the body.
    /// <para>Denial leg: while the peer is connected, the reservation blocks both the effective verdict and a rival
    /// acquisition. Control leg: the identical acquisition, by the identical principal, over the identical subject,
    /// succeeds one disconnect later.</para></summary>
    [Fact]
    public void ExclusiveSubjectHeldByAPeerIsAcquirableImmediatelyAfterItDisconnects() {
        using var fixture = Fixtures.FreshServer(definition: WithGrace(seconds: 3.0f));

        var peer = AdmitPeer(fixture: fixture);
        var rival = WorldPrincipal.Seat(slot: 1);
        var subject = GrantSubject.Body(index: PeerBodyIndex);
        var acquisition = new WorldGrant(
            Principal: rival,
            Capability: WorldCapability.Control,
            Subject: subject,
            Exclusive: true
        );

        Assert.True(condition: Controls(fixture: fixture, principal: peer));
        // The denial leg. The rival holds the boot-seeded Control/all wildcard, so nothing but the peer's exclusive
        // reservation can be what refuses it here.
        Assert.False(condition: Controls(fixture: fixture, principal: rival), userMessage: "the peer's exclusive reservation must beat the rival's seeded Control/all wildcard");

        fixture.Server.Grant(
            actor: WorldPrincipal.Console,
            grant: acquisition
        );

        Assert.False(condition: Holds(fixture: fixture, principal: rival, capability: WorldCapability.Control, subject: subject), userMessage: "an exclusive acquisition over a live peer's reserved subject must be refused");

        Disconnect(
            fixture: fixture,
            peer: peer
        );

        // The control leg — no step, no sweep, no deadline: the same call, immediately after the disconnect event.
        fixture.Server.Grant(
            actor: WorldPrincipal.Console,
            grant: acquisition
        );

        Assert.True(condition: Holds(fixture: fixture, principal: rival, capability: WorldCapability.Control, subject: subject), userMessage: "the disconnected peer's reservation must not outlive its connection");
        Assert.True(condition: Controls(fixture: fixture, principal: rival));
        Assert.False(condition: Controls(fixture: fixture, principal: peer));
        Assert.Empty(collection: fixture.Server.Grants.Held(principal: peer));
    }
    /// <summary>The other half of the cure, and the guard against over-correcting it: releasing the authority must
    /// leave the BODY's park-with-grace behavior exactly as it was — still parked, still occupied, still counting down
    /// a finite deadline.</summary>
    [Fact]
    public void PeerDisconnectParksTheBodyItReleasedTheAuthorityOf() {
        using var fixture = Fixtures.FreshServer(definition: WithGrace(seconds: 3.0f));

        var peer = AdmitPeer(fixture: fixture);

        Disconnect(
            fixture: fixture,
            peer: peer
        );

        Assert.Empty(collection: fixture.Server.Grants.Held(principal: peer));
        Assert.True(condition: fixture.Server.Population.IsParked(index: PeerBodyIndex), userMessage: "a positive grace must still park the body");
        Assert.True(condition: fixture.Server.Population.IsHumanOccupied(bodyIndex: PeerBodyIndex), userMessage: "a parked body stays human-occupied through its grace window");
        Assert.Equal(
            actual: fixture.Server.Population.ParkedRemainingTicks(index: PeerBodyIndex, tick: fixture.Server.NextInputTick),
            expected: ((long?)720L)
        );

        fixture.Step();

        Assert.True(condition: fixture.Server.Population.IsParked(index: PeerBodyIndex), userMessage: "one step is nowhere near a 3-second grace");
        Assert.Empty(collection: fixture.Server.Grants.Held(principal: peer));
    }
    /// <summary>The unchanged-path control: an authored-zero grace parks nothing, and its rows release at the same
    /// event they always did. Both graces now agree on the grant table and disagree only on the body — which is the
    /// whole shape of the cure.</summary>
    [Fact]
    public void DisconnectWithGraceDisabledReleasesGrantsImmediately() {
        using var fixture = Fixtures.FreshServer(definition: WithGrace(seconds: 0f));

        var peer = AdmitPeer(fixture: fixture);

        Assert.True(condition: Controls(fixture: fixture, principal: peer));

        Disconnect(
            fixture: fixture,
            peer: peer
        );

        Assert.Empty(collection: fixture.Server.Grants.Held(principal: peer));
        Assert.False(condition: fixture.Server.Population.IsParked(index: PeerBodyIndex), userMessage: "an authored-zero grace must tear the body down immediately");
    }
    /// <summary>The rate-0 law. A positive grace compiled against rate 0 is NEVER, and a rate-0 world never steps, so
    /// <see cref="WorldPopulation.ReclaimExpiredParks"/> is unreachable there: a deadline-deferred release would have
    /// been a permanent one. Zero rows for the disconnected generation, with no sweep available to produce them.
    /// <para>The body-side control keeps the two halves honest — the same disconnect parks forever, exactly as the
    /// rate-0 park law requires.</para></summary>
    [Fact]
    public void RateZeroWorldHoldsNoRowsForADisconnectedPeerGeneration() {
        using var fixture = Fixtures.FreshServer(definition: WithGrace(rateHz: 0, seconds: 3.0f));

        var peer = AdmitPeer(fixture: fixture);

        Assert.True(condition: Controls(fixture: fixture, principal: peer));

        Disconnect(
            fixture: fixture,
            peer: peer
        );

        Assert.Empty(collection: fixture.Server.Grants.Held(principal: peer));
        Assert.False(condition: Controls(fixture: fixture, principal: peer));
        Assert.True(condition: fixture.Server.Population.IsParked(index: PeerBodyIndex));
        Assert.Null(@object: fixture.Server.Population.ParkedRemainingTicks(index: PeerBodyIndex, tick: fixture.Server.NextInputTick));
    }
    /// <summary>The seat-side discriminator: a local seat CAN resume (its retained body's own profile id is the match
    /// rule), so a leave must leave its rows completely alone — through the park and through the resume alike. This is
    /// the behavior the peer-side cure must not have generalized over.</summary>
    [Fact]
    public void LocalSeatParkAndResumeKeepTheirGrantRows() {
        using var fixture = Fixtures.FreshServer(definition: WithGrace(seconds: 3.0f));

        var seat = WorldPrincipal.Seat(slot: 1);
        // A live acquisition over a subject that is NOT the seat's own body, so the assertion cannot be carried by the
        // boot seed alone. The seeded Drive/body:1 row is checked beside it.
        var acquired = GrantSubject.Body(index: 2);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: seat,
            Slot: 1,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        fixture.Server.Grant(
            actor: WorldPrincipal.Console,
            grant: new WorldGrant(
                Principal: seat,
                Capability: WorldCapability.Control,
                Subject: acquired,
                Exclusive: true
            )
        );

        Assert.True(condition: Holds(fixture: fixture, principal: seat, capability: WorldCapability.Control, subject: acquired));

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Leave(
            Principal: seat,
            Slot: 1
        )).Accepted);

        Assert.True(condition: fixture.Server.Population.IsSeatParked(slot: 1), userMessage: "a positive grace must park the seat");
        Assert.True(condition: Holds(fixture: fixture, principal: seat, capability: WorldCapability.Control, subject: acquired), userMessage: "a parked seat keeps every row it held — it is the one participant a re-join can resume onto");
        Assert.True(condition: Holds(fixture: fixture, principal: seat, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: 1)));

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: seat,
            Slot: 1,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted, userMessage: "the seeded Drive row is what authorizes the resuming re-join, so losing it would break resume outright");
        Assert.False(condition: fixture.Server.Population.IsSeatParked(slot: 1));
        Assert.True(condition: Holds(fixture: fixture, principal: seat, capability: WorldCapability.Control, subject: acquired));
    }
    /// <summary>The restore-side half of the same law: a restored parked PEER generation is exactly as unresumable as
    /// a live-parked one, so <see cref="WorldServer.RestoreCheckpoint"/> releases its rows and exclusive reservations
    /// the moment the grant table restores — no step, no sweep, no deadline. The body still parks (presence through
    /// the grace window is the park's whole remaining job), and — the control — a LOCAL seat's rows survive the same
    /// restore untouched, because a seat is the one participant a re-join can resume onto.</summary>
    [Fact]
    public void ARestoredParkReleasesItsRowsImmediately_LocalSeatRowsSurviveTheSameRestore() {
        using var fixture = Fixtures.FreshServer(definition: WithGrace(seconds: 3.0f));

        // A live local seat holding an acquisition over a NON-own body, captured alongside the peer.
        var seat = WorldPrincipal.Seat(slot: 1);
        var seatAcquired = GrantSubject.Body(index: 2);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            IdentityName: null,
            Principal: seat,
            Slot: 1,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        fixture.Server.Grant(
            actor: WorldPrincipal.Console,
            grant: new WorldGrant(
                Principal: seat,
                Capability: WorldCapability.Control,
                Subject: seatAcquired,
                Exclusive: true
            )
        );

        var peer = AdmitPeer(fixture: fixture);

        fixture.Step();

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var checkpoint,
            hostRow: EmptyHostRow(),
            reason: out var refusal
        ), userMessage: refusal);
        Assert.NotNull(@object: checkpoint);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint!.Server.DefinitionJson);
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-parked-grant-tests-").FullName;

        using var machines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );

        var (restored, _) = WorldServer.FromCheckpoint(
            checkpoint: checkpoint,
            instanceIdentity: "boot",
            machines: machines,
            profiles: new WorldOwnedWorlds(directory: stateDirectory, machineId: Guid.NewGuid(), template: definition)
        );

        try {
            // The peer's authority is gone at restore itself — before any Step could run a sweep.
            Assert.True(condition: restored.Population.IsParked(index: PeerBodyIndex), userMessage: "a restore parks every captured remote human");
            Assert.Empty(collection: restored.Grants.Held(principal: peer));

            // Its exclusive reservation is free: a rival's identical exclusive acquisition lands immediately.
            var rival = WorldPrincipal.Seat(slot: 2);

            restored.Grant(
                actor: WorldPrincipal.Console,
                grant: new WorldGrant(
                    Principal: rival,
                    Capability: WorldCapability.Control,
                    Subject: GrantSubject.Body(index: PeerBodyIndex),
                    Exclusive: true
                )
            );

            Assert.True(
                condition: restored.Grants.Held(principal: rival).Contains(value: (WorldCapability.Control, GrantSubject.Body(index: PeerBodyIndex))),
                userMessage: "a restored parked generation's exclusive reservation must not refuse a live acquirer"
            );

            // The control: the local seat's restore is unchanged — its acquisition and its seeded Drive row survive.
            Assert.Contains(expected: (WorldCapability.Control, seatAcquired), collection: restored.Grants.Held(principal: seat));
            Assert.Contains(expected: (WorldCapability.Drive, GrantSubject.Body(index: 1)), collection: restored.Grants.Held(principal: seat));
        } finally {
            try {
                Directory.Delete(path: stateDirectory, recursive: true);
            } catch (IOException) {
            }
        }
    }
    /// <summary>The reconnect half: the admission door's resume arm re-dispatches the same
    /// <see cref="WorldServerEvent.PeerAdmitted"/> shape a fresh admission rides, carrying the fresh connection's
    /// minted template rows — so the event alone unparks the retained body (the generation-guarded branch in
    /// <see cref="WorldPopulation.ApplyPeerAdmitted"/>, which is also what unparks a replayed resume) and restores
    /// the generation's admission authority. Denial half: after the disconnect the body is parked and the table is
    /// empty, and stays so however long nothing re-admits. Control half: the resume-shaped event re-mints and
    /// unparks in one apply.</summary>
    [Fact]
    public void AResumedGenerationRemintsItsAdmissionRowsThroughThePeerAdmittedEvent() {
        using var fixture = Fixtures.FreshServer(definition: WithGrace(seconds: 3.0f));

        var peer = AdmitPeer(fixture: fixture);
        var minted = new WorldGrant(
            Principal: peer,
            Capability: WorldCapability.Control,
            Subject: GrantSubject.Body(index: PeerBodyIndex),
            Exclusive: true
        );

        Disconnect(
            fixture: fixture,
            peer: peer
        );
        fixture.Step();

        Assert.True(condition: fixture.Server.Population.IsParked(index: PeerBodyIndex));
        Assert.Empty(collection: fixture.Server.Grants.Held(principal: peer));

        // The resume-shaped event: same entries as the admission, the fresh connection's minted rows on board.
        fixture.Server.ApplyServerEvent(serverEvent: new WorldServerEvent.PeerAdmitted(
            Entries: [
                PeerEntry(peer: peer),
            ],
            MintedGrants: [minted]
        ));

        Assert.False(condition: fixture.Server.Population.IsParked(index: PeerBodyIndex), userMessage: "a re-admission of the parked generation must unpark the retained body — live and on a replay re-drive alike");
        Assert.Contains(expected: (WorldCapability.Control, GrantSubject.Body(index: PeerBodyIndex)), collection: fixture.Server.Grants.Held(principal: peer));
        Assert.True(condition: Controls(fixture: fixture, principal: peer));
    }
    /// <summary>The replay half. The release is a pure function of the <see cref="WorldServerEvent.PeerDisconnected"/>
    /// event's own recorded rows, and replay re-drives that event through the very same
    /// <see cref="WorldServer.ApplyServerEvent"/> door the live path used — so a second world that never saw the live
    /// disconnect, handed only the recorded event, lands on the identical table.</summary>
    [Fact]
    public void AReDrivenDisconnectEventReproducesTheReleaseFromTheEventAlone() {
        using var live = Fixtures.FreshServer(definition: WithGrace(seconds: 3.0f));
        using var shadow = Fixtures.FreshServer(definition: WithGrace(seconds: 3.0f));

        var peer = AdmitPeer(fixture: live);
        var shadowPeer = AdmitPeer(fixture: shadow);

        Assert.Equal(actual: shadowPeer, expected: peer);

        // Captured BEFORE the live apply, exactly as the tape records it: the entries plus the rows the generation
        // held at dispatch.
        var recorded = DisconnectEvent(
            fixture: live,
            peer: peer
        );

        live.Server.ApplyServerEvent(serverEvent: recorded);
        shadow.Server.ApplyServerEvent(serverEvent: recorded);

        Assert.Empty(collection: live.Server.Grants.Held(principal: peer));
        Assert.Equal(
            actual: shadow.Server.Grants.Held(principal: peer),
            expected: live.Server.Grants.Held(principal: peer)
        );
        Assert.Equal(
            actual: shadow.Server.Population.IsParked(index: PeerBodyIndex),
            expected: live.Server.Population.IsParked(index: PeerBodyIndex)
        );
    }
    /// <summary>The recorded stream is what a re-drive gets, so the disconnect has to be ON the tape and the tape has
    /// to re-drive without divergence. Persisted and read back through the real on-disk file, never the in-memory
    /// list.</summary>
    [Fact]
    public void ADisconnectSpanningRecordingReachesTheTapeAndVerifies() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer(definition: WithGrace(seconds: 3.0f));

        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(
            liveServer: fixture.Server,
            profiles: fixture.Server.Profiles,
            transport: transport,
            engines: [],
            addonHostFactory: static (_, _) => new NullAddonHost()
        );
        var name = $"parked-grant-release-{Guid.NewGuid():N}";

        Assert.True(
            condition: tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: $"refused to arm: {refusal}"
        );

        // Armed BEFORE the admission: a recording only reproduces peers it saw admitted, so a peer seated before the
        // arm would diverge at tick 0 for a reason that has nothing to do with the disconnect under test.
        var peer = AdmitPeer(fixture: fixture);

        Disconnect(
            fixture: fixture,
            peer: peer
        );
        fixture.Step();
        tape.NoteTick();

        _ = tape.StopRecording();

        using (var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name))) {
            var snapshot = WorldReplaySnapshot.Read(stream: stream);
            var kinds = snapshot.Ticks
                .SelectMany(selector: static tick => tick.Authority)
                .Select(selector: static entry => entry.GetType().Name)
                .ToHashSet(comparer: StringComparer.Ordinal);

            Assert.Contains(expected: "PeerDisconnected", collection: kinds);
        }

        Assert.Equal(actual: tape.Verify(name: name).DivergedAt, expected: -1);
    }
}
