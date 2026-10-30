using System.Numerics;
using System.Text;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>The companion trust boundary as the destination enforces it, never as the sender claims it: a role
/// driver's body authority is derived from its role and admits no grant of its own; one visiting driver runs per
/// verified pin; a followed player binds only when this world verified it; a home slot is claimed only with the custody
/// token the home issued; and the bookkeeping that holds a slot or a binding follows the live document, rollback and
/// the followed player's identity.</summary>
public sealed class CompanionTrustLawTests {
    private const string SourceAuthority = "machine-a/home";
    private const string OtherAuthority = "machine-b/elsewhere";
    private const string PetId = "pet";

    /// <summary>Two companions of one role reserved together with different verified pins each get a driver of their
    /// own, each driving only its own companion.</summary>
    [Fact]
    public void TwoPinsReservedTogetherGetTwoDriversEachBoundToItsOwnCompanion() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var first = Module(text: "first");
        var second = Module(text: "second");
        var firstReservation = fixture.Server.ReserveTransfer(request: Request(companion: Companion(driver: first), incarnation: 41, transferId: 1));
        var secondReservation = fixture.Server.ReserveTransfer(request: Request(companion: Companion(driver: second), incarnation: 42, transferId: 2));

        Assert.True(condition: firstReservation.Accepted, userMessage: firstReservation.Reason);
        Assert.True(condition: secondReservation.Accepted, userMessage: secondReservation.Reason);
        var a = Commit(fixture: fixture, reservation: firstReservation, transferId: 1);
        var b = Commit(fixture: fixture, reservation: secondReservation, transferId: 2);
        var firstDriver = Principal.Addon(name: WorldRole.VisitorAddonName(pin: first, role: "companion"));
        var secondDriver = Principal.Addon(name: WorldRole.VisitorAddonName(pin: second, role: "companion"));

        fixture.Step();
        Assert.True(condition: Drives(fixture: fixture, body: a, driver: firstDriver));
        Assert.False(condition: Drives(fixture: fixture, body: b, driver: firstDriver));
        Assert.True(condition: Drives(fixture: fixture, body: b, driver: secondDriver));
        Assert.False(condition: Drives(fixture: fixture, body: a, driver: secondDriver));
    }

    /// <summary>A role driver holds no grant of its own over bodies or control: a re-grant of Drive over another body
    /// after reconciliation, and a Control grant, are refused and confer nothing; an Observe row over a state row is
    /// still its own to hold.</summary>
    [Fact]
    public void ARoleDriverHoldsNoBodyOrControlGrantOfItsOwn() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var pin = Module(text: "re-grant");
        var driver = Principal.Addon(name: WorldRole.VisitorAddonName(pin: pin, role: "companion"));
        var landed = Land(fixture: fixture, companion: Companion(driver: pin), incarnation: 43, transferId: 1);

        fixture.Step();
        Assert.True(condition: Drives(fixture: fixture, body: landed, driver: driver));
        Assert.False(condition: fixture.Server.GrantTable.TryGrant(
            grant: new WorldGrant(Budget: 4, Capability: WorldCapability.Drive, Exclusive: false, Grantee: driver, Subject: GrantSubject.Body(index: 0)),
            reason: out _
        ));
        Assert.False(condition: fixture.Server.GrantTable.TryGrant(
            grant: new WorldGrant(Capability: WorldCapability.Control, Exclusive: false, Grantee: driver, Subject: GrantSubject.Body(index: 0)),
            reason: out _
        ));
        fixture.Step();
        Assert.False(condition: Drives(fixture: fixture, body: 0, driver: driver));
        Assert.False(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Control, principal: driver, subject: GrantSubject.Body(index: 0)).IsAllowed);
        Assert.False(condition: fixture.Server.Grants.Allows(capability: WorldCapability.Control, principal: driver, subject: GrantSubject.All).IsAllowed);
        Assert.True(condition: fixture.Server.GrantTable.TryGrant(
            grant: new WorldGrant(Budget: 4, Capability: WorldCapability.Observe, Exclusive: false, Grantee: driver, Subject: GrantSubject.State(name: "senses")),
            reason: out var stateRefusal
        ), userMessage: stateRefusal);
    }

    /// <summary>A companion's followed player binds only when this world verified it: a player another source delivered
    /// is not one this sender may name, so the driver observes no one; the same player delivered by the companion's own
    /// source binds and is observed.</summary>
    [Fact]
    public void AFollowedPlayerBindsOnlyWhenTheSameSourceDeliveredIt() {
        using var fixture = Fixtures.FreshServer(definition: Document(otherAdmission: true));
        var pin = Module(text: "follows");
        var driver = Principal.Addon(name: WorldRole.VisitorAddonName(pin: pin, role: "companion"));
        var stranger = Land(fixture: fixture, companion: null, incarnation: 50, source: OtherAuthority, transferId: 1);
        var claimant = Land(fixture: fixture, companion: Companion(driver: pin, follows: Incarnation(index: 50)), incarnation: 51, transferId: 2);

        fixture.Step();
        Assert.Equal(expected: -1, actual: fixture.Server.Population.Companion(index: claimant)!.Value.Follows);
        Assert.False(condition: Observes(fixture: fixture, body: stranger, driver: driver));

        var player = Land(fixture: fixture, companion: null, incarnation: 52, transferId: 3);

        // The source acknowledges its player's commit before the companion's crossing, as a host's completion does.
        fixture.Server.AcknowledgeTransfer(sourceAuthority: SourceAuthority, transferId: 3);
        var companion = Land(fixture: fixture, companion: Companion(driver: pin, follows: Incarnation(index: 52)), incarnation: 53, transferId: 4);

        fixture.Step();
        Assert.Equal(expected: player, actual: fixture.Server.Population.Companion(index: companion)!.Value.Follows);
        Assert.True(condition: Observes(fixture: fixture, body: player, driver: driver));
    }

    /// <summary>A companion's player is followed by identity, not slot: when another traveller takes the slot its
    /// player left, the companion follows no one and its driver cannot observe the newcomer.</summary>
    [Fact]
    public void ACompanionStopsFollowingWhenAnotherTravellerTakesItsPlayersSlot() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var pin = Module(text: "slot reuse");
        var driver = Principal.Addon(name: WorldRole.VisitorAddonName(pin: pin, role: "companion"));
        var player = Land(fixture: fixture, companion: null, incarnation: 60, transferId: 1);
        var companion = Land(fixture: fixture, companion: Companion(driver: pin, follows: Incarnation(index: 60)), incarnation: 61, transferId: 2);

        fixture.Step();
        Assert.True(condition: Observes(fixture: fixture, body: player, driver: driver));
        Assert.NotNull(@object: fixture.Server.ExecuteAuthorityOperation(operation: () => fixture.Server.DetachForTransfer(slot: player, transferId: 99)));

        var newcomer = Land(fixture: fixture, companion: null, incarnation: 62, preferredSlot: player, transferId: 3);

        Assert.Equal(expected: player, actual: newcomer);
        fixture.Step();
        Assert.Equal(expected: -1, actual: fixture.Server.Population.FollowedBody(binding: fixture.Server.Population.Companion(index: companion)!.Value));
        Assert.False(condition: Observes(fixture: fixture, body: newcomer, driver: driver));
    }

    /// <summary>Home takes its companion back only with the custody token it issued as the companion left: a traveller
    /// naming the away incarnation without it, or with a forged one, is refused at reservation and holds nothing; the
    /// real token re-adopts it into the slot its placement held.</summary>
    [Fact]
    public void HomeReadoptsItsCompanionOnlyWithTheCustodyTokenItIssued() {
        using var fixture = Fixtures.FreshServer(definition: HomeDocument(follows: 0));
        var pet = Pet(fixture: fixture);
        var departing = fixture.Server.ExecuteAuthorityOperation(operation: () => fixture.Server.DetachForTransfer(slot: pet, transferId: 7))!;
        var incarnation = departing.Mobility.Incarnation;
        var genuine = fixture.Server.Population.CustodyToken(epoch: departing.Mobility.Epoch, incarnation: incarnation);
        var returning = departing.Mobility.Advance() with { DepartedFrom = new WorldEntityAddress(Authority: SourceAuthority, Generation: 1, Index: 4) };

        Assert.True(condition: fixture.Server.Population.IsAwaySlot(slot: pet));
        foreach (var custody in new[] { null, "0000", new string(c: 'a', count: genuine.Length) }) {
            var refused = fixture.Server.ReserveTransfer(request: Request(companion: (Companion(driver: null) with { Custody = custody }), mobility: returning, transferId: 20));

            Assert.False(condition: refused.Accepted);
            Assert.True(condition: refused.CompanionRefused);
            Assert.Contains(expectedSubstring: "custody token", actualString: refused.Reason);
        }

        var plain = fixture.Server.ReserveTransfer(request: Request(companion: null, mobility: returning, transferId: 21));

        Assert.False(condition: plain.Accepted);
        var accepted = fixture.Server.ReserveTransfer(request: Request(companion: (Companion(driver: null) with { Custody = genuine }), mobility: returning, transferId: 22));

        Assert.True(condition: accepted.Accepted, userMessage: accepted.Reason);
        Assert.Equal(expected: pet, actual: accepted.BodyIndices.Single());
        _ = Commit(fixture: fixture, reservation: accepted, transferId: 22);
        Assert.Equal(expected: PetId, actual: fixture.Server.Population.InhabitantPlacementId(index: pet));
        Assert.False(condition: fixture.Server.Population.IsAwaySlot(slot: pet));
    }

    /// <summary>A structural install re-derives a home companion's binding: moving the seat its placement follows moves
    /// what it follows.</summary>
    [Fact]
    public void AReloadRebindsAHomeCompanionToItsPlacementsFollowedSeat() {
        using var fixture = Fixtures.FreshServer(definition: HomeDocument(follows: 0));
        var pet = Pet(fixture: fixture);

        Assert.Equal(expected: 0, actual: fixture.Server.Population.Companion(index: pet)!.Value.Follows);
        Apply(fixture: fixture, mutation: new WorldMutation.UpsertPlacement(Placement: PetPlacement(fixture: fixture, follows: 1), Principal: Principal.Console));
        Assert.Equal(expected: 1, actual: fixture.Server.Population.Companion(index: pet)!.Value.Follows);
    }

    /// <summary>An away companion whose home placement no longer inhabits its role is forgotten: the slot it held is
    /// free again and no longer counted.</summary>
    [Fact]
    public void AnAwayCompanionWhosePlacementIsGoneNoLongerHoldsItsSlot() {
        using var fixture = Fixtures.FreshServer(definition: HomeDocument(follows: 0));
        var pet = Pet(fixture: fixture);

        Assert.NotNull(@object: fixture.Server.ExecuteAuthorityOperation(operation: () => fixture.Server.DetachForTransfer(slot: pet, transferId: 8)));
        Assert.True(condition: fixture.Server.Population.IsAwaySlot(slot: pet));
        Apply(fixture: fixture, mutation: new WorldMutation.RemovePlacement(Id: PetId, Principal: Principal.Console));
        Assert.False(condition: fixture.Server.Population.IsAwaySlot(slot: pet));
        Assert.Empty(collection: fixture.Server.Population.AwayCompanions);
    }

    /// <summary>A read of another instance's body answers only a principal that may observe it there; the console,
    /// which may, reads the pose.</summary>
    [Fact]
    public void AnObservedPoseReadRefusesAPrincipalThatCannotObserveTheBody() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var body = Land(fixture: fixture, companion: null, incarnation: 70, transferId: 1);
        var refused = fixture.Server.AnswerObservedWhere(body: body, principal: Principal.Addon(name: "stranger"));
        var allowed = fixture.Server.AnswerObservedWhere(body: body, principal: Principal.Console);

        Assert.True(condition: refused.Refused);
        Assert.Contains(expectedSubstring: "cannot observe", actualString: refused.Text);
        Assert.False(condition: allowed.Refused);
        Assert.Contains(expectedSubstring: $"body:{body}", actualString: allowed.Text);
    }

    /// <summary>A crossing that rolls back returns a companion as the companion it left as, with its binding intact.</summary>
    [Fact]
    public void ARolledBackCrossingRestoresTheCompanionBinding() {
        using var scenario = new Rows();
        var pin = Module(text: "rollback");
        var companion = Land(fixture: scenario.Away, companion: Companion(driver: pin, home: scenario.Home.Server.AuthorityIdentity), incarnation: 80, transferId: 1);
        var bound = scenario.Away.Server.Population.Companion(index: companion);

        scenario.Host.Admit(row: scenario.Home.Instance);
        _ = scenario.Host.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "home"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "away",
            sourceSlot: companion,
            testForceJoinRefusalOrdinal: 0
        );
        scenario.Host.DrainPendingTransfers();
        Assert.NotNull(@object: bound);
        Assert.Equal(expected: bound, actual: scenario.Away.Server.Population.Companion(index: companion));
    }

    /// <summary>A separated companion whose home is not running waits for it rather than for ever: once its home
    /// starts it goes home.</summary>
    [Fact]
    public void AStrandedCompanionGoesHomeOnceItsHomeIsRunning() {
        using var scenario = new Rows();
        var pin = Module(text: "stranded");
        var companion = Land(fixture: scenario.Away, companion: Companion(driver: pin, home: scenario.Home.Server.AuthorityIdentity), incarnation: 90, transferId: 1);

        scenario.Host.DrainPendingTransfers();
        scenario.Host.DrainPendingTransfers();
        Assert.NotNull(@object: scenario.Away.Server.Population.Companion(index: companion));
        scenario.Host.Admit(row: scenario.Home.Instance);
        for (var drain = 0; (drain < 3); drain++) {
            scenario.Host.DrainPendingTransfers();
        }
        Assert.Null(@object: scenario.Away.Server.Population.Companion(index: companion));
        Assert.Contains(collection: Enumerable.Range(count: scenario.Home.Server.Population.Capacity, start: 0), filter: slot => scenario.Home.Server.Population.Companion(index: slot) is not null);
    }

    // Two rows of one host: the away world a companion visits and its home, which a law admits when it needs it.
    private sealed class Rows : IDisposable {
        private readonly TemporaryDirectory m_root = new(prefix: "puck-companion-trust-host-");

        public Rows() {
            Host = new WorldInstanceHost(
                admitsSpawn: true,
                applicationStopping: CancellationToken.None,
                machineHostFactory: Fixtures.MachineHostFactory,
                machineId: Guid.NewGuid(),
                resolver: new WorldSessionResolver(),
                seats: WorldEmbodiedSeats.None,
                stateRoot: new WorldStateRoot(path: m_root.RootPath)
            );
            AwayRow = HostRow.Build(definition: Document(admitAll: true), name: "away");
            Home = HostRow.Build(definition: Document(admitAll: true), name: "home");
            Host.Admit(row: AwayRow.Instance);
            Away = new RowFixture(server: AwayRow.Server);
        }

        public HostRow AwayRow { get; }
        public RowFixture Away { get; }
        public HostRow Home { get; }
        public WorldInstanceHost Host { get; }

        public void Dispose() {
            Host.Dispose();
            AwayRow.Dispose();
            Home.Dispose();
            m_root.Dispose();
        }
    }
    // The reserve/commit door of a row, shaped like a fixture's.
    private sealed class RowFixture(WorldServer server) : ILandingTarget {
        public WorldServer Server { get; } = server;
    }
    private interface ILandingTarget {
        WorldServer Server { get; }
    }
    private sealed class FixtureTarget(WorldFixture fixture) : ILandingTarget {
        public WorldServer Server => fixture.Server;
    }

    private static string Module(string text) {
        var bytes = Encoding.UTF8.GetBytes(s: $"(module) ;; {nameof(CompanionTrustLawTests)} {text}");

        return WorldProjectionContent.Shared.Put(content: bytes).ToString();
    }
    private static WorldEntityAddress Incarnation(int index) => new(Authority: "origin/world", Generation: 1, Index: index);
    private static WorldTransferCompanion Companion(string? driver, WorldEntityAddress? follows = null, string home = "origin/world") =>
        new(Driver: driver, Follows: follows, Home: home, Role: "companion");
    private static bool Drives(WorldFixture fixture, int body, Principal driver) =>
        fixture.Server.Grants.Allows(capability: WorldCapability.Drive, principal: driver, subject: GrantSubject.Body(index: body)).IsAllowed;
    private static bool Observes(WorldFixture fixture, int body, Principal driver) =>
        fixture.Server.Grants.Allows(capability: WorldCapability.Observe, principal: driver, subject: GrantSubject.Body(index: body)).IsAllowed;
    private static void Apply(WorldFixture fixture, WorldMutation mutation) => Assert.True(condition: fixture.Server.TryApplyMutation(
        connectionId: SubmissionEnvelope.LocalConnectionId,
        correlationId: 0,
        engineTick: fixture.Server.CompletedEngineTicks,
        mutation: mutation,
        preMetered: false,
        tick: (fixture.Server.NextInputTick - 1UL)
    ));
    private static int Pet(WorldFixture fixture) {
        var bodies = new List<int>();

        fixture.Server.Population.CollectInhabitants(into: bodies, placementId: PetId);
        return bodies.Single();
    }
    private static WorldPlacement PetPlacement(WorldFixture fixture, int follows) {
        var camera = fixture.Server.Definition.Placements[0];

        return camera with {
            Id = PetId,
            Inhabit = camera.Inhabit! with { Follows = follows, Role = "companion" },
            Position = new DocumentVector3(value: new Vector3(x: 3f, y: 0f, z: 3f)),
        };
    }
    // A world that accepts visiting companions and admits arrivals from the companions' source (and, when asked,
    // from another source too, or from anywhere).
    private static WorldDefinition Document(bool otherAdmission = false, bool admitAll = false) {
        var document = Fixtures.BuildDocument();

        return document with {
            PopulationRaw = document.Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 6),
                NetworkPlayers = 6,
            },
            Admission = (admitAll
                ? [Arrivals(domain: "*")]
                : (otherAdmission ? [Arrivals(domain: SourceAuthority), Arrivals(domain: OtherAuthority)] : [Arrivals(domain: SourceAuthority)])),
            RolesRaw = [new WorldRole(Name: "companion", Accept: true)],
        };
    }
    // A home world whose `pet` placement inhabits one companion of the role, following a local seat.
    private static WorldDefinition HomeDocument(int follows) {
        var document = Fixtures.BuildCameraBodyDocument();
        var camera = document.Placements[0];
        var pet = camera with {
            Id = PetId,
            Inhabit = camera.Inhabit! with { Follows = follows, Role = "companion" },
            Position = new DocumentVector3(value: new Vector3(x: 3f, y: 0f, z: 3f)),
        };

        return document with {
            PopulationRaw = document.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 3), NetworkPlayers = 2 },
            PlacementsRaw = (document.PlacementsRaw! with { Rows = [camera, pet] }),
            Admission = [Arrivals(domain: SourceAuthority)],
            RolesRaw = [new WorldRole(Name: "companion", Accept: true)],
        };
    }
    private static WorldAdmissionEntry Arrivals(string domain) => new(
        Domain: domain,
        Mode: WorldAdmissionTrustMode.FederatedAuthority,
        Grants: [new WorldAdmissionGrant(Capability: WorldCapability.Observe, Budget: 8)]
    );
    private static int Land(WorldFixture fixture, WorldTransferCompanion? companion, int incarnation, ulong transferId, string source = SourceAuthority, int? preferredSlot = null) =>
        Land(target: new FixtureTarget(fixture: fixture), companion: companion, incarnation: incarnation, transferId: transferId, source: source, preferredSlot: preferredSlot);
    private static int Land(RowFixture fixture, WorldTransferCompanion? companion, int incarnation, ulong transferId) =>
        Land(target: fixture, companion: companion, incarnation: incarnation, transferId: transferId, source: SourceAuthority, preferredSlot: null);
    private static int Land(ILandingTarget target, WorldTransferCompanion? companion, int incarnation, ulong transferId, string source, int? preferredSlot) {
        var reservation = target.Server.ReserveTransfer(request: Request(companion: companion, incarnation: incarnation, preferredSlot: preferredSlot, source: source, transferId: transferId));

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);
        return Commit(reservation: reservation, server: target.Server, source: source, transferId: transferId);
    }
    private static int Commit(WorldFixture fixture, WorldTransferReservationReply reservation, ulong transferId) =>
        Commit(reservation: reservation, server: fixture.Server, source: SourceAuthority, transferId: transferId);
    private static int Commit(WorldServer server, WorldTransferReservationReply reservation, ulong transferId, string source) {
        Assert.True(
            condition: (server.CommitTransfer(
                sourceAuthority: source,
                transferId: transferId,
                members: [new WorldTransferCommitMember(
                    Profile: null,
                    Arrival: WorldTransferArrival.Spawn,
                    BodyMotionProgramName: "grounded",
                    Position: default,
                    YawRadians: default,
                    PlanarVelocity: default,
                    VerticalVelocity: default
                )],
                reason: out var reason
            ) == WorldTransferStatus.Committed),
            userMessage: reason
        );

        return reservation.BodyIndices.Single();
    }
    private static WorldTransferReservationRequest Request(WorldTransferCompanion? companion, ulong transferId, int incarnation = 0, WorldMobilityIdentity? mobility = null, string source = SourceAuthority, int? preferredSlot = null) => new(
        TransferId: transferId,
        SourceAuthority: source,
        SourceRateHz: 240,
        SourceTick: 0,
        DeadlineSourceTick: 60,
        Border: "door",
        BorderCapacity: null,
        PartyAllOrNothing: true,
        PeerAdmission: true,
        Members: [new WorldTransferReservationMember(
            Principal: Principal.World,
            PreferredSlot: (preferredSlot ?? WorldBodiesLimits.LocalSeatCount),
            Identity: null,
            Source: IntentSource.Idle,
            BodyColor: default,
            CatalogRig: 0,
            Mobility: (mobility ?? new WorldMobilityIdentity(
                DepartedFrom: new WorldEntityAddress(Authority: "origin/world", Generation: 3, Index: incarnation),
                Epoch: 0,
                Incarnation: Incarnation(index: incarnation)
            )),
            Companion: companion
        )]
    );
}
