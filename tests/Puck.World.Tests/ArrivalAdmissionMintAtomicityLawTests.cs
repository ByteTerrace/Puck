using Puck.Commands;
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
/// reached from the tick thread (<c>WorldPeerHost.RunOnTickThreadAsync</c> → <c>DrainPending</c>, and the composition
/// root's console verbs); an authenticated federation commit is reached from a socket worker under
/// <see cref="WorldServer.ExecuteAuthorityOperation{T}"/>. Both reach the one ordered domain, and a submission's
/// completion callback runs inside its drain — the seam that lets a law hold the drain open across a commit.
/// <see cref="WorldServer.AuthorityGateContended"/> reports the committer meeting the held gate, which is what
/// releases the drain: the commit is attempted while the drain is open, whatever the machine's load.
/// </remarks>
public sealed class ArrivalAdmissionMintAtomicityLawTests {
    private const string SourceAuthority = "player-world/source";
    private const ulong TransferId = 8_101UL;

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
                    Principal: Principal.Console,
                    PreferredSlot: WorldBodiesLimits.LocalSeatCount,
                    Identity: null,
                    Source: IntentSource.Live,
                    BodyColor: default,
                    CatalogRig: 4,
                    Mobility: new WorldMobilityIdentity(
                        DepartedFrom: new WorldEntityAddress(
                            Authority: "origin/world",
                            Generation: 7,
                            Index: WorldBodiesLimits.LocalSeatCount
                        ),
                        Epoch: 0,
                        Incarnation: new WorldEntityAddress(
                            Authority: "origin/world",
                            Generation: 7,
                            Index: WorldBodiesLimits.LocalSeatCount
                        )
                    )
                )]
        );

    [Fact]
    public async Task AConcurrentCommitPublishesNoTravelerBeforeItsVerdictGrantsAreInstalled() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 2));
        var reservation = fixture.Server.ReserveTransfer(request: ArrivalReservation());

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
        );

        var bodyIndex = Assert.Single(collection: reservation.BodyIndices);
        var member = new WorldTransferCommitMember(
            Profile: null,
            HasMappedArrival: false,
            BodyMotionProgramName: "grounded",
            Position: default,
            YawRadians: default,
            PlanarVelocity: default,
            VerticalVelocity: default
        );

        var cancellationToken = TestContext.Current.CancellationToken;
        using var drainOpen = new ManualResetEventSlim(initialState: false);
        // Set when the committer meets the drain's held gate, or finishes without ever meeting it.
        using var committerArrived = new ManualResetEventSlim(initialState: false);
        var contentions = 0;

        fixture.Server.AuthorityGateContended = () => {
            _ = Interlocked.Increment(location: ref contentions);
            committerArrived.Set();
        };

        var accepted = false;
        var commitReason = string.Empty;
        var resolvedPrincipal = false;
        var principal = default(Principal);
        var active = false;
        var observeVerdict = default(GrantVerdict);
        var driveVerdict = default(GrantVerdict);
        Exception? committerFault = null;

        // The socket-worker role: an authenticated authority's commit, which runs under the authority gate and never
        // waits for this host's next tick.
        // A task captures even a cleanup fault if the test is cancelled and releases its events. An unhandled
        // exception on a raw background thread would terminate the entire test process instead of naming a law.
        var committer = Task.Factory.StartNew(
            action: () => {
                try {
                    drainOpen.Wait(cancellationToken: cancellationToken);

                    accepted = fixture.Server.CommitTransfer(
                        members: [member],
                        reason: out commitReason,
                        sourceAuthority: SourceAuthority,
                        transferId: TransferId
                    );
                    // Read the traveler the instant the destination called it committed, exactly as a routed read-back
                    // does: through the same authority gate, asking the same grant table WorldServer.AnswerSubmittedQuery
                    // asks before it will answer at all.
                    resolvedPrincipal = fixture.Server.TryTransferredPrincipal(
                        ordinal: 0,
                        principal: out principal,
                        sourceAuthority: SourceAuthority,
                        transferId: TransferId
                    );
                    (active, observeVerdict, driveVerdict) = fixture.Server.ExecuteAuthorityOperation(operation: () => (
                        fixture.Server.Population.IsActive(index: bodyIndex),
                        fixture.Server.Grants.Allows(
                        principal: principal,
                        capability: WorldCapability.Observe,
                        subject: GrantSubject.Body(index: bodyIndex)
                    ),
                        fixture.Server.Grants.Allows(
                        principal: principal,
                        capability: WorldCapability.Drive,
                        subject: GrantSubject.Body(index: bodyIndex)
                    )));
                } catch (Exception exception) {
                    committerFault = exception;
                } finally {
                    committerArrived.Set();
                }
            },
            cancellationToken: CancellationToken.None,
            creationOptions: TaskCreationOptions.LongRunning,
            scheduler: TaskScheduler.Default
        );

        // The tick-thread role: one ordinary submission whose completion runs inside the ordered drain, holding it
        // open until the committer has met the held gate.
        fixture.Server.Submit(
            envelope: new SubmissionEnvelope(
                ConnectionId: SubmissionEnvelope.LocalConnectionId,
                SessionGeneration: 0,
                Sequence: 1,
                CorrelationId: 1,
                Principal: Principal.Console,
                Payload: new WorldSubmissionPayload.Query(Value: new WorldQuery.Rules()),
                OperationId: Guid.Empty
            ),
            completion: _ => {
                drainOpen.Set();
                committerArrived.Wait(cancellationToken: cancellationToken);
            }
        );
        await committer.WaitAsync(cancellationToken: cancellationToken);

        Assert.Null(@object: committerFault);
        Assert.True(
            condition: (contentions > 0),
            userMessage: "the committer never met the drain's held authority gate, so the contended commit was not staged"
        );
        Assert.True(
            condition: accepted,
            userMessage: commitReason
        );
        Assert.True(
            condition: resolvedPrincipal,
            userMessage: "a committed transfer resolved no peer principal"
        );
        Assert.True(
            condition: active,
            userMessage: $"the destination called transfer {TransferId} committed but body {bodyIndex} was not active"
        );
        // The discriminating pair: the body is published, and every row its arrival verdict authorizes is already
        // installed. A commit that returns between the population admit and the grant mint publishes a body a routed
        // read-back refuses by name.
        Assert.True(
            condition: observeVerdict.IsAllowed,
            userMessage: $"{principal.Describe()} could not observe body:{bodyIndex} at the instant its commit was accepted ({observeVerdict.DescribeDenial()})"
        );
        Assert.True(
            condition: driveVerdict.IsAllowed,
            userMessage: $"{principal.Describe()} could not drive body:{bodyIndex} at the instant its commit was accepted ({driveVerdict.DescribeDenial()})"
        );
    }
    /// <summary>The control leg: the same commit with nothing holding the ordered drain open. Without it the
    /// contended law cannot separate "admission mints grants at all" from "admission mints them atomically" — both
    /// failures read identically at its assertions.</summary>
    [Fact]
    public void AnUncontendedCommitInstallsItsVerdictGrantsBeforeItAnswers() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 2));
        var reservation = fixture.Server.ReserveTransfer(request: ArrivalReservation());

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
        );

        var bodyIndex = Assert.Single(collection: reservation.BodyIndices);
        var member = new WorldTransferCommitMember(
            Profile: null,
            HasMappedArrival: false,
            BodyMotionProgramName: "grounded",
            Position: default,
            YawRadians: default,
            PlanarVelocity: default,
            VerticalVelocity: default
        );

        Assert.True(
            condition: fixture.Server.CommitTransfer(
                members: [member],
                reason: out var reason,
                sourceAuthority: SourceAuthority,
                transferId: TransferId
            ),
            userMessage: reason
        );
        Assert.True(condition: fixture.Server.TryTransferredPrincipal(
            ordinal: 0,
            principal: out var principal,
            sourceAuthority: SourceAuthority,
            transferId: TransferId
        ));
        Assert.True(condition: fixture.Server.Population.IsActive(index: bodyIndex));
        Assert.True(condition: fixture.Server.Grants.Allows(
            principal: principal,
            capability: WorldCapability.Observe,
            subject: GrantSubject.Body(index: bodyIndex)
        ).IsAllowed);
        Assert.True(condition: fixture.Server.Grants.Allows(
            principal: principal,
            capability: WorldCapability.Drive,
            subject: GrantSubject.Body(index: bodyIndex)
        ).IsAllowed);
    }
    /// <summary>The refusal control: a principal the arrival never minted anything for is still refused over the
    /// same body, by the same table. A law whose "allowed" assertions could pass for a principal holding nothing
    /// would be measuring the assertion, not the grant.</summary>
    [Fact]
    public void AnUnrelatedPeerPrincipalStillCannotObserveTheArrivedBody() {
        using var fixture = Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 2));
        var reservation = fixture.Server.ReserveTransfer(request: ArrivalReservation());

        Assert.True(
            condition: reservation.Accepted,
            userMessage: reservation.Reason
        );

        var bodyIndex = Assert.Single(collection: reservation.BodyIndices);
        var member = new WorldTransferCommitMember(
            Profile: null,
            HasMappedArrival: false,
            BodyMotionProgramName: "grounded",
            Position: default,
            YawRadians: default,
            PlanarVelocity: default,
            VerticalVelocity: default
        );

        Assert.True(
            condition: fixture.Server.CommitTransfer(
                members: [member],
                reason: out var reason,
                sourceAuthority: SourceAuthority,
                transferId: TransferId
            ),
            userMessage: reason
        );
        Assert.True(condition: fixture.Server.TryTransferredPrincipal(
            ordinal: 0,
            principal: out var principal,
            sourceAuthority: SourceAuthority,
            transferId: TransferId
        ));

        var stranger = Principal.Peer(
            index: (principal.Index + 1),
            generation: principal.Generation
        );

        Assert.False(condition: fixture.Server.Grants.Allows(
            principal: stranger,
            capability: WorldCapability.Observe,
            subject: GrantSubject.Body(index: bodyIndex)
        ).IsAllowed);
    }
}
