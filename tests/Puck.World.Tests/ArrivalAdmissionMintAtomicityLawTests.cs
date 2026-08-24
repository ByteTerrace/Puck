using Puck.World.Protocol;
using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// An admitted traveler and the grants its admission verdict mints are one step from every observer: a commit that
/// answers accepted has already installed every row the verdict authorizes over the body it published.
/// </summary>
/// <remarks>
/// The two roles are driven explicitly because the invariant is a threading one. <c>WorldServer.Submit</c> is
/// reached from the tick thread (<c>WorldTcpHost.RunOnTickThreadAsync</c> → <c>DrainPending</c>, and the composition
/// root's console verbs); an authenticated federation commit is reached from a socket worker under
/// <see cref="WorldServer.ExecuteAuthorityOperation{T}"/>. Both reach the one ordered domain, and a submission's
/// completion callback runs inside its drain — the seam that lets a law hold the drain open across a commit.
/// </remarks>
public sealed class ArrivalAdmissionMintAtomicityLawTests {
    private const string SourceAuthority = "player-world/source";
    private const ulong TransferId = 8_101UL;

    /// <summary>How long the committing role waits for the drain to be open before giving up — generous, because it
    /// only bounds a hang, never a verdict.</summary>
    private static readonly TimeSpan DrainOpenBudget = TimeSpan.FromSeconds(value: 20);
    /// <summary>How long the drain is held open. Long enough that a commit which does not serialize against it
    /// finishes inside the window every time (a commit is microseconds of in-memory work), and short enough that a
    /// commit which does serialize simply waits this long on the authority gate and then proceeds.</summary>
    private static readonly TimeSpan DrainHold = TimeSpan.FromMilliseconds(value: 250);

    [Fact]
    public void AConcurrentCommitPublishesNoTravelerBeforeItsVerdictGrantsAreInstalled() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var reservation = fixture.Server.ReserveTransfer(request: ArrivalReservation());

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);

        var bodyIndex = Assert.Single(collection: reservation.BodyIndices);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        using var drainOpen = new ManualResetEventSlim(initialState: false);
        using var committerDone = new ManualResetEventSlim(initialState: false);

        var accepted = false;
        var commitReason = string.Empty;
        var resolvedPrincipal = false;
        var principal = default(WorldPrincipal);
        var active = false;
        var observeVerdict = default(GrantVerdict);
        var driveVerdict = default(GrantVerdict);
        Exception? committerFault = null;

        // The socket-worker role: an authenticated authority's commit, which runs under the authority gate and never
        // waits for this host's next tick.
        var committer = new Thread(start: () => {
            try {
                Assert.True(condition: drainOpen.Wait(timeout: DrainOpenBudget), userMessage: "the ordered-domain drain never opened");

                accepted = fixture.Server.CommitTransfer(members: [member], reason: out commitReason, sourceAuthority: SourceAuthority, transferId: TransferId);
                // Read the traveler the instant the destination called it committed, exactly as a routed read-back
                // does: through the same authority gate, asking the same grant table WorldServer.AnswerSubmittedQuery
                // asks before it will answer at all.
                resolvedPrincipal = fixture.Server.TryTransferredPrincipal(ordinal: 0, principal: out principal, sourceAuthority: SourceAuthority, transferId: TransferId);
                (active, observeVerdict, driveVerdict) = fixture.Server.ExecuteAuthorityOperation(operation: () => (
                    fixture.Server.Population.IsActive(index: bodyIndex),
                    fixture.Server.Grants.Allows(principal: principal, capability: WorldCapability.Observe, subject: GrantSubject.Body(index: bodyIndex)),
                    fixture.Server.Grants.Allows(principal: principal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: bodyIndex))));
            } catch (Exception exception) {
                committerFault = exception;
            } finally {
                committerDone.Set();
            }
        }) {
            IsBackground = true,
            Name = "arrival-commit",
        };

        committer.Start();

        // The tick-thread role: one ordinary submission whose completion runs inside the ordered drain, holding it
        // open across the committer's whole operation.
        fixture.Server.Submit(
            envelope: new SubmissionEnvelope(
                ConnectionId: SubmissionEnvelope.LocalConnectionId,
                SessionGeneration: 0,
                Sequence: 1,
                CorrelationId: 1,
                Principal: WorldPrincipal.Console,
                Payload: new WorldSubmissionPayload.Query(Value: new WorldQuery.Rules())),
            completion: _ => {
                drainOpen.Set();
                committerDone.Wait(timeout: DrainHold);
            });

        Assert.True(condition: committerDone.Wait(timeout: DrainOpenBudget, cancellationToken: TestContext.Current.CancellationToken), userMessage: "the committing authority never finished");
        committer.Join();

        Assert.Null(@object: committerFault);
        Assert.True(condition: accepted, userMessage: commitReason);
        Assert.True(condition: resolvedPrincipal, userMessage: "a committed transfer resolved no peer principal");
        Assert.True(condition: active, userMessage: $"the destination called transfer {TransferId} committed but body {bodyIndex} was not active");
        // The discriminating pair: the body is published, and every row its arrival verdict authorizes is already
        // installed. A commit that returns between the population admit and the grant mint publishes a body a routed
        // read-back refuses by name.
        Assert.True(condition: observeVerdict.IsAllowed, userMessage: $"{principal.Describe()} could not observe body:{bodyIndex} at the instant its commit was accepted ({observeVerdict.DescribeDenial()})");
        Assert.True(condition: driveVerdict.IsAllowed, userMessage: $"{principal.Describe()} could not drive body:{bodyIndex} at the instant its commit was accepted ({driveVerdict.DescribeDenial()})");
    }
    /// <summary>The control leg: the same commit with nothing holding the ordered drain open. Without it the
    /// contended law cannot separate "admission mints grants at all" from "admission mints them atomically" — both
    /// failures read identically at its assertions.</summary>
    [Fact]
    public void AnUncontendedCommitInstallsItsVerdictGrantsBeforeItAnswers() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var reservation = fixture.Server.ReserveTransfer(request: ArrivalReservation());

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);

        var bodyIndex = Assert.Single(collection: reservation.BodyIndices);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: fixture.Server.CommitTransfer(members: [member], reason: out var reason, sourceAuthority: SourceAuthority, transferId: TransferId), userMessage: reason);
        Assert.True(condition: fixture.Server.TryTransferredPrincipal(ordinal: 0, principal: out var principal, sourceAuthority: SourceAuthority, transferId: TransferId));
        Assert.True(condition: fixture.Server.Population.IsActive(index: bodyIndex));
        Assert.True(condition: fixture.Server.Grants.Allows(principal: principal, capability: WorldCapability.Observe, subject: GrantSubject.Body(index: bodyIndex)).IsAllowed);
        Assert.True(condition: fixture.Server.Grants.Allows(principal: principal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: bodyIndex)).IsAllowed);
    }
    /// <summary>The refusal control: a principal the arrival never minted anything for is still refused over the
    /// same body, by the same table. A law whose "allowed" assertions could pass for a principal holding nothing
    /// would be measuring the assertion, not the grant.</summary>
    [Fact]
    public void AnUnrelatedPeerPrincipalStillCannotObserveTheArrivedBody() {
        using var fixture = Fixtures.FreshServer(definition: TransferPopulationDocument());
        var reservation = fixture.Server.ReserveTransfer(request: ArrivalReservation());

        Assert.True(condition: reservation.Accepted, userMessage: reservation.Reason);

        var bodyIndex = Assert.Single(collection: reservation.BodyIndices);
        var member = new WorldTransferCommitMember(Profile: null, HasMappedArrival: false, BodyMotionProgramName: "grounded", Position: default, YawRadians: default, PlanarVelocity: default, VerticalVelocity: default);

        Assert.True(condition: fixture.Server.CommitTransfer(members: [member], reason: out var reason, sourceAuthority: SourceAuthority, transferId: TransferId), userMessage: reason);
        Assert.True(condition: fixture.Server.TryTransferredPrincipal(ordinal: 0, principal: out var principal, sourceAuthority: SourceAuthority, transferId: TransferId));

        var stranger = WorldPrincipal.Peer(index: (principal.Index + 1), generation: principal.Generation);

        Assert.False(condition: fixture.Server.Grants.Allows(principal: stranger, capability: WorldCapability.Observe, subject: GrantSubject.Body(index: bodyIndex)).IsAllowed);
    }

    private static WorldTransferReservationRequest ArrivalReservation() =>
        new(
            TransferId: TransferId,
            SourceAuthority: SourceAuthority,
            SourceRateHz: 240,
            SourceTick: 0,
            DeadlineSourceTick: 60,
            Border: "east",
            BorderCapacity: null,
            PartyAllOrNothing: true,
            PeerAdmission: true,
            Members: [new WorldTransferReservationMember(
                Principal: WorldPrincipal.Console,
                PreferredSlot: WorldBodiesLimits.LocalSeatCount,
                Identity: null,
                Source: IntentSource.Live,
                BodyColor: default,
                CatalogRig: 4,
                Mobility: new WorldMobilityIdentity(Incarnation: new WorldEntityAddress(Authority: "origin/world", Generation: 7, Index: WorldBodiesLimits.LocalSeatCount), Epoch: 0))]);
    private static WorldDefinition TransferPopulationDocument() {
        var document = Fixtures.BuildDocument();

        return document with {
            PopulationRaw = document.Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 2),
                NetworkPlayers = 2,
            },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
    }
}
