using Puck.Commands;

using Xunit;

using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// The laws of observing a world as an unembodied session through <see cref="WorldServer.TryObserveAsSession"/>, the
/// one door a session screen binds through. The world's own admission row decides whether the viewer is admitted and
/// its tier decides what the observer's mirror is delivered; the observation is delivered only while the session
/// holds <c>observe all</c>, and releasing it ends the session. Every case pairs a denial with the same act under one
/// reversed fact.
/// </summary>
public sealed class SessionObservationLawTests {
    /// <summary>The authority the viewer observes from; the wildcard entry admits it.</summary>
    private const string Viewer = "viewer/portal";

    // The collider-bearing fixture document with one authored grant row (which a presentation projection never
    // carries) and, when a tier is given, an admission row granting a session its whole-world view at that tier.
    private static WorldDefinition Document(WorldDisclosureTier? tier) {
        var document = Fixtures.BuildGradientUpDocument(gradientUp: false);

        return document with {
            Admission = ((tier is { } disclosed)
                ? [new WorldAdmissionEntry(
                    Algorithm: string.Empty,
                    Disclosure: disclosed,
                    Domain: WorldAdmissionEntry.AnyAuthority,
                    // A frames-tier row may mint nothing: its observer is handed no document to address a grant against.
                    Grants: ((disclosed == WorldDisclosureTier.Frames)
                        ? []
                        : [new WorldAdmissionGrant(
                            Budget: 64,
                            Capability: WorldCapability.Observe,
                            Subject: GrantSubject.All
                        )]),
                    Mode: WorldAdmissionTrustMode.FederatedAuthority,
                    PublicKey: string.Empty,
                    Subject: null
                )]
                : []),
            GrantsRaw = [new WorldGrant(
                Capability: WorldCapability.Observe,
                Exclusive: false,
                Grantee: Principal.Seat(slot: 0),
                Subject: GrantSubject.Body(index: 0)
            )],
        };
    }
    private static (WorldSessionObservation? Observation, WorldSessionMirror Mirror, string Refusal) Observe(WorldFixture fixture) {
        var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
        var observation = fixture.Server.TryObserveAsSession(
            refusal: out var refusal,
            sink: mirror,
            sourceAuthority: Viewer
        );

        return (observation, mirror, refusal);
    }
    // Steps the world and answers whether the mirror was delivered the step.
    private static bool StepReaches(WorldFixture fixture, WorldSessionMirror mirror) {
        var before = mirror.Tick;

        fixture.Step();
        fixture.Step();

        return (mirror.Tick != before);
    }

    [Fact]
    public void AnObservation_AdmitsExactlyOneSession_AndReleasingItEndsThatSession() {
        using var fixture = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Replica));

        var (observation, mirror, refusal) = Observe(fixture: fixture);

        Assert.NotNull(@object: observation);
        Assert.True(
            condition: fixture.Server.IsLiveSession(principal: observation.Session),
            userMessage: refusal
        );

        // Exactly one: the next admission takes the next ordinal.
        Assert.True(
            condition: fixture.Server.TryAdmitSession(
                refusal: out refusal,
                session: out var next,
                sourceAuthority: Viewer,
                tier: out _
            ),
            userMessage: refusal
        );
        Assert.Equal(
            actual: next.Index,
            expected: (observation.Session.Index + 1)
        );
        Assert.True(condition: StepReaches(
            fixture: fixture,
            mirror: mirror
        ));

        observation.Dispose();

        Assert.True(condition: observation.Ended);
        Assert.False(condition: fixture.Server.IsLiveSession(principal: observation.Session));
        Assert.Empty(collection: fixture.Server.GrantRows(principal: observation.Session));
        Assert.False(
            condition: StepReaches(
                fixture: fixture,
                mirror: mirror
            ),
            userMessage: "a released observation is still delivered"
        );
    }
    [Fact]
    public void AFramesOnlyAdmission_GivesTheMirrorNoDefinition_AReplicaOneGivesTheDocument() {
        bool MirrorHoldsTheDocument(WorldDisclosureTier tier) {
            using var fixture = Fixtures.FreshServer(definition: Document(tier: tier));

            var (observation, mirror, refusal) = Observe(fixture: fixture);

            Assert.True(
                condition: (observation is not null),
                userMessage: refusal
            );
            fixture.Step();

            return (
                !ReferenceEquals(
                objA: mirror.Definition,
                objB: WorldProjection.Undisclosed
            ) &&
                (mirror.Definition.Placements.Count == fixture.Server.Definition.Placements.Count) &&
                (mirror.Definition.Placements.Count > 0)
            );
        }

        Laws.RefusalWithControl(
            lawId: "session.frames-tier-discloses-no-document",
            deniedOutcome: () => MirrorHoldsTheDocument(tier: WorldDisclosureTier.Frames),
            controlOutcome: () => MirrorHoldsTheDocument(tier: WorldDisclosureTier.Replica)
        );
    }
    [Fact]
    public void APresentationAdmission_DeliversTheProjection_NotTheDocumentsGrants() {
        int GrantRowsTheMirrorHolds(WorldDisclosureTier tier) {
            using var fixture = Fixtures.FreshServer(definition: Document(tier: tier));

            var (observation, mirror, refusal) = Observe(fixture: fixture);

            Assert.True(
                condition: (observation is not null),
                userMessage: refusal
            );

            return mirror.Definition.Grants.Count;
        }

        Assert.Equal(
            actual: GrantRowsTheMirrorHolds(tier: WorldDisclosureTier.Presentation),
            expected: 0
        );
        Assert.Equal(
            actual: GrantRowsTheMirrorHolds(tier: WorldDisclosureTier.Replica),
            expected: 1
        );
    }
    [Fact]
    public void ARevokedObserve_GetsNoFurtherUpdates_ARegrantedOneCatchesUp() {
        using var fixture = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Replica));

        var (observation, mirror, refusal) = Observe(fixture: fixture);

        Assert.True(
            condition: (observation is not null),
            userMessage: refusal
        );

        var view = Assert.Single(
            collection: fixture.Server.GrantRows(principal: observation!.Session),
            predicate: static row => ((row.Capability == WorldCapability.Observe) && (row.Subject == GrantSubject.All))
        );

        Laws.RefusalWithControl(
            lawId: "session.observation-follows-observe-all",
            deniedOutcome: () => {
                fixture.Server.Revoke(
                    actor: Principal.Console,
                    grant: view
                );

                return StepReaches(
                    fixture: fixture,
                    mirror: mirror
                );
            },
            controlOutcome: () => {
                fixture.Server.Grant(
                    actor: Principal.Console,
                    grant: view
                );

                return StepReaches(
                    fixture: fixture,
                    mirror: mirror
                );
            }
        );
    }
    [Fact]
    public void AWorldThatAdmitsNoViewer_RefusesTheObservationByName_OneThatDoesAdmitsIt() {
        string? RefusalFor(WorldDisclosureTier? tier) {
            using var fixture = Fixtures.FreshServer(definition: Document(tier: tier));

            var (observation, mirror, refusal) = Observe(fixture: fixture);

            if (observation is null) {
                Assert.Same(
                    actual: mirror.Definition,
                    expected: WorldProjection.Undisclosed
                );

                return refusal;
            }

            return null;
        }

        Assert.Contains(
            expectedSubstring: nameof(WorldAdmissionRefusal.NoAdmissionEntries),
            actualString: RefusalFor(tier: null)
        );
        Assert.Null(@object: RefusalFor(tier: WorldDisclosureTier.Replica));
    }
    [Fact]
    public void TheObserverPolicy_IsReadLive_SoAnEditThatDisclosesNoBodyReachesTheNextDelivery() {
        using var fixture = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Replica));

        _ = fixture.JoinSeat();

        var (observation, mirror, refusal) = Observe(fixture: fixture);

        Assert.True(
            condition: (observation is not null),
            userMessage: refusal
        );

        bool SeesSeatBodyUnder(WorldObserverDisclosureMode mode) {
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetPopulationDefaults(
                Population: (fixture.Server.Definition.Population with { Disclosure = new WorldObserverDisclosure(Mode: mode) }),
                Principal: Principal.Console
            ));
            fixture.Step();
            fixture.Step();

            return mirror.IsActive(index: 0);
        }

        Laws.RefusalWithControl(
            lawId: "session.observer-policy-is-live",
            deniedOutcome: () => SeesSeatBodyUnder(mode: WorldObserverDisclosureMode.SelfOnly),
            controlOutcome: () => SeesSeatBodyUnder(mode: WorldObserverDisclosureMode.All)
        );
    }
    [Fact]
    public void WhatAConsumerMeasuresForTheObserver_IsWhatItsTierDiscloses_WhetherOrNotItObservesNow() {
        (bool Observing, bool Withheld) Measures(WorldDisclosureTier tier) {
            using var fixture = Fixtures.FreshServer(definition: Document(tier: tier));

            var (observation, _, refusal) = Observe(fixture: fixture);

            Assert.True(
                condition: (observation is not null),
                userMessage: refusal
            );

            var observing = (observation!.Disclose(candidate: fixture.Server.Definition) is not null);

            // Withheld: a candidate admitted now must still fit the renderer once observation resumes.
            foreach (var row in fixture.Server.GrantRows(principal: observation.Session)) {
                fixture.Server.Revoke(
                    actor: Principal.Console,
                    grant: row
                );
            }

            return (observing, (observation.Disclose(candidate: fixture.Server.Definition) is not null));
        }

        Assert.Equal(
            actual: Measures(tier: WorldDisclosureTier.Frames),
            expected: (false, false)
        );

        // A candidate that re-tiers the viewer (a rebuild) is measured as the session admitted next would see it.
        using var framed = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Frames));

        var (observation, _, refusal) = Observe(fixture: framed);

        Assert.True(
            condition: (observation is not null),
            userMessage: refusal
        );
        Assert.NotNull(@object: observation!.Disclose(candidate: Document(tier: WorldDisclosureTier.Replica)));
        Assert.Null(@object: observation.Disclose(candidate: Document(tier: null)));
        Assert.Equal(
            actual: Measures(tier: WorldDisclosureTier.Replica),
            expected: (true, true)
        );
    }
    [Fact]
    public void AnAttachFromInsideADelivery_IsRefused_AndEndsTheSessionItAdmitted() {
        using var fixture = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Replica));
        var nested = new NestedObserver(server: fixture.Server);

        using (fixture.Server.ExecuteAuthorityOperation(operation: () => fixture.Server.AttachSink(sink: nested))) {
            fixture.Step();
        }

        Assert.True(condition: nested.Attempted);
        Assert.Null(@object: nested.Observation);
        Assert.False(
            condition: string.IsNullOrEmpty(value: nested.Refusal),
            userMessage: "the nested attach was refused without a reason"
        );
        Assert.False(condition: fixture.Server.IsLiveSession(principal: Principal.Session(
            epoch: 1,
            ordinal: 0
        )));

        // Outside a delivery the same door admits, onto the ordinal the refused attempt released.
        var (after, _, refusal) = Observe(fixture: fixture);

        Assert.True(
            condition: (after is not null),
            userMessage: refusal
        );
        Assert.Equal(
            actual: after!.Session.Index,
            expected: 0
        );
    }
    [Fact]
    public void AnObserverThatFaultsOnItsPrimer_IsRefused_AndLeavesNoLiveSession() {
        using var fixture = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Replica));
        var observation = fixture.Server.TryObserveAsSession(
            refusal: out var refusal,
            sink: new FaultingSink(),
            sourceAuthority: Viewer
        );

        Assert.Null(@object: observation);
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "faulted"
        );
        Assert.False(condition: fixture.Server.IsLiveSession(principal: Principal.Session(
            epoch: 1,
            ordinal: 0
        )));
        Assert.NotNull(@object: Observe(fixture: fixture).Observation);
    }
    [Fact]
    public void ARedrivenAdmissionOverAnOrdinalANewerSessionHolds_EndsThatSessionsObservation() {
        using var fixture = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Replica));

        var (first, _, refusal) = Observe(fixture: fixture);

        Assert.True(
            condition: (first is not null),
            userMessage: refusal
        );

        var recorded = first!.Session;

        first.Dispose();

        var (newer, _, _) = Observe(fixture: fixture);

        Assert.NotNull(@object: newer);
        Assert.Equal(
            actual: newer.Session.Index,
            expected: recorded.Index
        );
        Assert.False(condition: newer.Ended);

        // What a replay drive does with a tape that recorded the first session.
        fixture.Server.ApplyServerEvent(serverEvent: new WorldServerEvent.SessionAdmitted(
            MintedGrants: [],
            Session: recorded,
            Templates: []
        ));

        Assert.True(condition: newer.Ended);
        Assert.False(condition: fixture.Server.IsLiveSession(principal: newer.Session));
        Assert.Empty(collection: fixture.Server.GrantRows(principal: newer.Session));
    }
    [Fact]
    public void ARebuild_EndsTheObservation_SoItsHolderMustBeAdmittedAgain() {
        using var fixture = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Replica));

        var (observation, mirror, refusal) = Observe(fixture: fixture);

        Assert.True(
            condition: (observation is not null),
            userMessage: refusal
        );
        Assert.False(condition: observation!.Ended);

        fixture.Server.EnqueueRebuild(
            principal: Principal.Console,
            request: new WorldRebuildRequest(
                Kind: WorldRebuildKind.Reset,
                Definition: null,
                PathHint: null,
                Force: false
            )
        );
        fixture.Step();

        Assert.True(condition: observation.Ended);
        Assert.False(condition: StepReaches(
            fixture: fixture,
            mirror: mirror
        ));

        var (again, _, _) = Observe(fixture: fixture);

        Assert.NotNull(@object: again);
        Assert.NotEqual(
            actual: again.Session,
            expected: observation.Session
        );
    }

    // An observer that throws on everything it is handed.
    private sealed class FaultingSink : IClientSink {
        public void DeliverAnswer(in QueryAnswer answer) => throw new InvalidOperationException(message: "faulting observer");
        public void DeliverComposition(WorldComposition composition) => throw new InvalidOperationException(message: "faulting observer");
        public void DeliverDefinition(WorldDefinition definition) => throw new InvalidOperationException(message: "faulting observer");
        public void DeliverSessionLever(WorldSessionLever lever) => throw new InvalidOperationException(message: "faulting observer");
        public void DeliverSnapshot(in WorldSnapshot snapshot) => throw new InvalidOperationException(message: "faulting observer");
        public void DeliverState(WorldDefinition definition, in WorldStateStamp stamp) => throw new InvalidOperationException(message: "faulting observer");
    }
    // A sink that, on its first tick delivery (its attach primer is not a fan-out), tries to observe the same world as a
    // session from inside the delivery.
    private sealed class NestedObserver(WorldServer server) : IClientSink {
        private bool m_primed;

        public bool Attempted { get; private set; }
        public WorldSessionObservation? Observation { get; private set; }
        public string Refusal { get; private set; } = string.Empty;

        public void DeliverAnswer(in QueryAnswer answer) {
        }
        public void DeliverComposition(WorldComposition composition) {
        }
        public void DeliverDefinition(WorldDefinition definition) {
        }
        public void DeliverSessionLever(WorldSessionLever lever) {
        }
        public void DeliverSnapshot(in WorldSnapshot snapshot) {
            if (!m_primed) {
                m_primed = true;

                return;
            }

            if (Attempted) {
                return;
            }

            Attempted = true;
            Observation = server.TryObserveAsSession(
                refusal: out var refusal,
                sink: new WorldSessionMirror(placeholder: WorldProjection.Undisclosed),
                sourceAuthority: Viewer
            );
            Refusal = refusal;
        }
        public void DeliverState(WorldDefinition definition, in WorldStateStamp stamp) {
        }
    }
}
