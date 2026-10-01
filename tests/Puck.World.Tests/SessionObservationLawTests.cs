using Puck.Assets.Documents;
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
    public void APresentationSessionCannotQueryTheBodyPoseItsProjectionDoesNotCarry() {
        bool QueryLeaksPose(WorldDisclosureTier tier) {
            using var fixture = Fixtures.FreshServer(definition: Document(tier: tier));

            _ = fixture.JoinSeat();
            var (observation, _, refusal) = Observe(fixture: fixture);

            Assert.NotNull(@object: observation);
            Assert.True(condition: string.IsNullOrEmpty(value: refusal));

            var answer = fixture.Server.AnswerSubmittedQuery(
                principal: observation.Session,
                query: new WorldQuery.PlayerWhere(Index: 0)
            );

            observation.Dispose();

            return !answer.Refused;
        }

        Laws.RefusalWithControl(
            lawId: "session.presentation-query-fidelity",
            deniedOutcome: () => QueryLeaksPose(tier: WorldDisclosureTier.Presentation),
            controlOutcome: () => QueryLeaksPose(tier: WorldDisclosureTier.Replica)
        );
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

            var answer = fixture.Server.AnswerSubmittedQuery(
                principal: observation!.Session,
                query: new WorldQuery.PlayerWhere(Index: 0)
            );

            Assert.Equal(
                expected: mirror.IsActive(index: 0),
                actual: !answer.Refused
            );

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

    // A candidate declaring a vector row and binding the ball's position to it, public or restricted.
    private static WorldDefinition WithHue(StateVisibility? visibility, bool bound) {
        var document = Document(tier: WorldDisclosureTier.Presentation);
        var state = (document.StateRaw ?? new WorldStateSection());
        var creations = document.Creations.Select(selector: creation => {
            if (
                !bound ||
                (creation.Id != "ball")
            ) {
                return creation;
            }

            var position = System.Text.Json.JsonSerializer.Deserialize<DocumentVector3>(
                json: "\"state.hue\"",
                options: DocumentJsonOptions.Shared
            )!;

            return (creation with {
                Document = (creation.Document with { Shapes = [.. creation.Document.Shapes!.Select(selector: shape => (shape with { Position = position }))] }),
                HashRaw = null,
            });
        }).ToList();

        return document with {
            CreationsRaw = creations,
            StateRaw = (state with {
                World = [.. (state.World ?? []), new WorldStateRow(
                    Cells: [new StateCell(
                        Key: StateRow.SlotKey,
                        Value: CellValue.Text(value: "[0,0,0]")
                    )],
                    Kind: CellKind.Text,
                    Name: CellName.Parse(candidate: "hue"),
                    Visibility: visibility
                )],
            }),
        };
    }

    [Fact]
    public void APresentationQueryUsesTheSameRecipientFilteredStateAsItsDelivery() {
        int QueryCells(StateVisibility visibility) {
            using var fixture = Fixtures.FreshServer(definition: WithHue(
                bound: false,
                visibility: visibility
            ));

            var (observation, mirror, refusal) = Observe(fixture: fixture);

            Assert.NotNull(@object: observation);
            Assert.True(condition: string.IsNullOrEmpty(value: refusal));

            var answer = fixture.Server.AnswerSubmittedQuery(
                principal: observation.Session,
                query: new WorldQuery.StateObservations(Row: "hue")
            );

            Assert.False(condition: answer.Refused, userMessage: answer.Text);
            var rows = Assert.IsAssignableFrom<IReadOnlyList<WorldObservedRow>>(@object: answer.Payload);
            var count = rows.Sum(selector: row => row.Cells.Count);

            Assert.Equal(
                expected: mirror.Definition.State.Where(predicate: row => (row.Name.Value == "hue")).Sum(selector: row => (row.Cells?.Count ?? 0)),
                actual: count
            );

            observation.Dispose();

            return count;
        }

        Assert.Equal(expected: 0, actual: QueryCells(visibility: new StateVisibility(Readers: ["seat1"])));
        Assert.Equal(expected: 1, actual: QueryCells(visibility: new StateVisibility()));
    }
    [Fact]
    public void ACandidateIsMeasuredAsTheMostAnySessionCouldReceive_AndOneWhoseStoreCannotBeLaidOutIsRefused() {
        using var fixture = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Presentation));

        var (observation, _, refusal) = Observe(fixture: fixture);

        Assert.True(
            condition: (observation is not null),
            userMessage: refusal
        );

        // A reader list may name a session, so a restricted binding is measured, not refused.
        Assert.NotNull(@object: observation!.Disclose(candidate: WithHue(
            bound: true,
            visibility: new StateVisibility(Readers: ["seat1"])
        )));

        var envelope = new WorldRenderEnvelope();
        var unplaceable = Document(tier: WorldDisclosureTier.Presentation);

        unplaceable = unplaceable with {
            StateRaw = ((unplaceable.StateRaw ?? new WorldStateSection()) with {
                World = [.. (unplaceable.StateRaw?.World ?? []), new WorldStateRow(
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "drift"),
                    Space: "nowhere"
                )],
            }),
        };

        using (envelope.Configure(
            allowGrowth: true,
            instanceCapacity: 0,
            measure: candidate => ((observation.Disclose(candidate: candidate) is not null)
                ? (Words: 0, Instances: 0)
                : (Words: 0, Instances: 0)),
            programWordCapacity: 0
        )) {
            Laws.RefusalWithControl(
                lawId: "session.unplaceable-candidate-is-refused-not-thrown",
                deniedOutcome: () => envelope.TryFit(
                    candidate: unplaceable,
                    reason: out _
                ),
                controlOutcome: () => envelope.TryFit(
                    candidate: WithHue(
                        bound: true,
                        visibility: null
                    ),
                    reason: out _
                )
            );
        }
    }
    [Fact]
    public void DealtChildrenAPublicReaderIsDenied_CountTowardTheFit() {
        const string Store = "store";
        var accounts = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "a"), Value: CellValue.Text(value: "a")),
                new StateCell(Key: CellName.Parse(candidate: "b"), Value: CellValue.Text(value: "b")),
            ],
            Kind: CellKind.Text,
            Name: CellName.Parse(candidate: "accounts"),
            Visibility: new StateVisibility(Readers: ["seat1"])
        );
        var template = new WorldPlacement(
            Deal: new WorldPlacementDeal(Row: "accounts"),
            Distribution: new WorldDistribution(
                Fill: new WorldSequence(
                    Name: WorldSequence.None,
                    Offset: 0,
                    Step: 0f
                ),
                Region: new WorldDistributionRegion.Lattice(
                    CountA: 4,
                    CountB: 1,
                    StepA: new DocumentVector3(value: new System.Numerics.Vector3(x: 2f, y: 0f, z: 0f)),
                    StepB: new DocumentVector3(value: new System.Numerics.Vector3(x: 0f, y: 0f, z: 1f))
                )
            ),
            Id: "stores",
            Position: new DocumentVector3(value: new System.Numerics.Vector3(x: 10f, y: 0f, z: 10f)),
            PrototypeId: Store,
            Scale: 1f,
            Solid: new WorldSolid(Margin: 0f),
            YawDegrees: 0f
        );
        var document = Document(tier: WorldDisclosureTier.Presentation);

        using var fixture = Fixtures.FreshServer(definition: document with {
            CreationsRaw = [.. document.Creations, CreationFixtures.UnitSphere(id: Store)],
            PlacementRowsRaw = [.. document.Placements, template],
            StateRaw = ((document.StateRaw ?? new WorldStateSection()) with { World = [.. (document.StateRaw?.World ?? []), accounts] }),
        });

        fixture.Step();
        fixture.Step();

        var candidate = fixture.Server.Definition;

        var (observation, _, refusal) = Observe(fixture: fixture);

        Assert.True(
            condition: (observation is not null),
            userMessage: refusal
        );

        // What a public reader is handed deals no child from a row it may not read.
        var time = fixture.Server.Time;
        var publicView = WorldProjection.Compose(
            arena: fixture.Server.Arena,
            authority: fixture.Server.AuthorityIdentity,
            definition: candidate,
            revision: 1,
            tier: WorldDisclosureTier.Presentation,
            time: in time
        )!;

        Assert.True(condition: WorldProjection.TryToDefinition(
            definition: out var publicDefinition,
            projection: publicView,
            reason: out var hydrateReason
        ), userMessage: hydrateReason);
        Assert.True(
            condition: (publicDefinition!.Placements.Count < candidate.Placements.Count),
            userMessage: "the fixture deals no child a public reader is denied, so it discriminates nothing"
        );
        Assert.Equal(
            actual: observation!.Disclose(candidate: candidate)!.Placements.Count,
            expected: candidate.Placements.Count
        );
    }
    [Fact]
    public void ADealtChildIsMeasuredAsTheCostliestPrototypeItsDealCanDeal() {
        const string Crowd = "crowd";
        const string Pebble = "pebble";
        const int CrowdShapes = 8;
        var keys = new[] { "a", "b", "c" };
        var crowd = CreationFixtures.Prototype(document: CreationFixtures.Document(
            name: Crowd,
            shapes: [.. Enumerable.Range(count: CrowdShapes, start: 0).Select(selector: index => (CreationFixtures.Shape(type: Puck.SignedDistance.SdfSolidPrimitive.Sphere) with {
                Id = index,
                Position = new System.Numerics.Vector3(x: (index * 2f), y: 0f, z: 0f),
            }))]
        ));
        // The dealt row is public; the variant row selecting the one-shape pebble is restricted, so a reader denied it
        // is dealt the template's eight-shape crowd.
        var accounts = new WorldStateRow(
            Capacity: 4,
            Cells: [.. keys.Select(selector: key => new StateCell(Key: CellName.Parse(candidate: key), Value: CellValue.Text(value: key)))],
            Kind: CellKind.Text,
            Name: CellName.Parse(candidate: "accounts")
        );
        var levels = new WorldStateRow(
            Capacity: 4,
            Cells: [.. keys.Select(selector: key => new StateCell(Key: CellName.Parse(candidate: key), Value: CellValue.Text(value: "1")))],
            Kind: CellKind.Text,
            Name: CellName.Parse(candidate: "levels"),
            Visibility: new StateVisibility(Readers: ["seat1"])
        );
        var template = new WorldPlacement(
            Deal: new WorldPlacementDeal(
                Row: "accounts",
                Variants: new WorldPlacementDealVariants(
                    Map: new Dictionary<string, string>(comparer: StringComparer.Ordinal) { ["1"] = Pebble },
                    Row: "levels"
                )
            ),
            Distribution: new WorldDistribution(
                Fill: new WorldSequence(
                    Name: WorldSequence.None,
                    Offset: 0,
                    Step: 0f
                ),
                Region: new WorldDistributionRegion.Lattice(
                    CountA: 4,
                    CountB: 1,
                    StepA: new DocumentVector3(value: new System.Numerics.Vector3(x: 20f, y: 0f, z: 0f)),
                    StepB: new DocumentVector3(value: new System.Numerics.Vector3(x: 0f, y: 0f, z: 1f))
                )
            ),
            Id: "crowds",
            Position: new DocumentVector3(value: new System.Numerics.Vector3(x: 10f, y: 0f, z: 10f)),
            PrototypeId: Crowd,
            Scale: 1f,
            YawDegrees: 0f
        );
        var document = Document(tier: WorldDisclosureTier.Presentation);

        using var fixture = Fixtures.FreshServer(definition: document with {
            CreationsRaw = [.. document.Creations, crowd, CreationFixtures.UnitSphere(id: Pebble)],
            PlacementRowsRaw = [.. document.Placements, template],
            StateRaw = ((document.StateRaw ?? new WorldStateSection()) with { World = [.. (document.StateRaw?.World ?? []), accounts, levels] }),
        });

        fixture.Step();
        fixture.Step();

        var candidate = fixture.Server.Definition;
        var dealtAsSelected = WorldPlacementStamper.StaticStampInstances(
            creations: candidate.Creations,
            placements: candidate.Placements
        );

        var (observation, _, refusal) = Observe(fixture: fixture);

        Assert.True(
            condition: (observation is not null),
            userMessage: refusal
        );

        var measured = observation!.Disclose(candidate: candidate)!;
        var measuredInstances = WorldPlacementStamper.StaticStampInstances(
            creations: measured.Creations,
            placements: measured.Placements
        );

        // The live deal selected the pebble for every child; the measurement counts each child as the crowd.
        Assert.Equal(
            actual: (measuredInstances - dealtAsSelected),
            expected: (keys.Length * (CrowdShapes - 1))
        );
    }
    [Fact]
    public void AFaultedSession_HoldsNothingFromTheMomentOfTheFault() {
        (bool Live, bool Allowed) AfterAFaultingStep(IClientSink sink) {
            using var fixture = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Replica));
            var observation = fixture.Server.TryObserveAsSession(
                refusal: out var refusal,
                sink: sink,
                sourceAuthority: Viewer
            );

            Assert.True(
                condition: (observation is not null),
                userMessage: refusal
            );
            fixture.Step();

            // Between this step's delivery and the next step's start: the end has not applied yet.
            return (
                fixture.Server.IsLiveSession(principal: observation!.Session),
                fixture.Server.Grants.Allows(
                capability: WorldCapability.Observe,
                principal: observation.Session,
                subject: GrantSubject.All
            ).IsAllowed
            );
        }

        Assert.Equal(
            actual: AfterAFaultingStep(sink: new FaultingAfterPrimerSink()),
            expected: (false, false)
        );
        Assert.Equal(
            actual: AfterAFaultingStep(sink: new WorldSessionMirror(placeholder: WorldProjection.Undisclosed)),
            expected: (true, true)
        );
    }
    [Fact]
    public void APausedWorld_EndsAFaultedSessionAtItsNextAdministrativeDrain() {
        bool RowsLeftAfterTwoDrains(IClientSink sink) {
            using var fixture = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Replica));
            var observation = fixture.Server.TryObserveAsSession(
                refusal: out var refusal,
                sink: sink,
                sourceAuthority: Viewer
            );

            Assert.True(
                condition: (observation is not null),
                userMessage: refusal
            );

            // A console edit applied without a step delivers the definition, which the faulting observer throws on.
            fixture.Server.EnqueueMutation(mutation: new WorldMutation.RemovePlacement(
                Id: "ball",
                Principal: Principal.Console
            ));
            _ = fixture.Server.DrainAdministrative();
            _ = fixture.Server.DrainAdministrative();

            return (fixture.Server.GrantRows(principal: observation!.Session).Count > 0);
        }

        Laws.RefusalWithControl(
            lawId: "session.paused-world-ends-faulted-session",
            deniedOutcome: () => RowsLeftAfterTwoDrains(sink: new FaultingOnDefinitionSink()),
            controlOutcome: () => RowsLeftAfterTwoDrains(sink: new WorldSessionMirror(placeholder: WorldProjection.Undisclosed))
        );
    }
    [Fact]
    public void AnObserverThatFaultsAfterItsPrimer_EndsItsSessionAtTheNextStep() {
        bool LiveAfterTwoSteps(IClientSink sink) {
            using var fixture = Fixtures.FreshServer(definition: Document(tier: WorldDisclosureTier.Replica));
            var observation = fixture.Server.TryObserveAsSession(
                refusal: out var refusal,
                sink: sink,
                sourceAuthority: Viewer
            );

            Assert.True(
                condition: (observation is not null),
                userMessage: refusal
            );
            fixture.Step();
            fixture.Step();

            var live = fixture.Server.IsLiveSession(principal: observation!.Session);

            Assert.Equal(
                actual: (fixture.Server.GrantRows(principal: observation.Session).Count > 0),
                expected: live
            );
            Assert.Equal(
                actual: observation.Ended,
                expected: !live
            );

            return live;
        }

        Laws.RefusalWithControl(
            lawId: "session.delivery-fault-ends-the-session",
            deniedOutcome: () => LiveAfterTwoSteps(sink: new FaultingAfterPrimerSink()),
            controlOutcome: () => LiveAfterTwoSteps(sink: new WorldSessionMirror(placeholder: WorldProjection.Undisclosed))
        );
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
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) => throw new InvalidOperationException(message: "faulting observer");
        public void DeliverSessionLever(WorldSessionLever lever) => throw new InvalidOperationException(message: "faulting observer");
        public void DeliverSnapshot(in WorldSnapshot snapshot) => throw new InvalidOperationException(message: "faulting observer");
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) => throw new InvalidOperationException(message: "faulting observer");
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
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) {
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
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) {
        }
    }
    // An observer that takes its attach primer, then throws on the first tick it is delivered.
    private sealed class FaultingAfterPrimerSink : IClientSink {
        private int m_snapshots;

        public void DeliverAnswer(in QueryAnswer answer) {
        }
        public void DeliverComposition(WorldComposition composition) {
        }
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) {
        }
        public void DeliverSessionLever(WorldSessionLever lever) {
        }
        public void DeliverSnapshot(in WorldSnapshot snapshot) {
            if (++m_snapshots > 1) {
                throw new InvalidOperationException(message: "faulting observer");
            }
        }
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) {
        }
    }
    // An observer that takes its attach primer, then throws on the next definition it is delivered.
    private sealed class FaultingOnDefinitionSink : IClientSink {
        private int m_definitions;

        public void DeliverAnswer(in QueryAnswer answer) {
        }
        public void DeliverComposition(WorldComposition composition) {
        }
        public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version) {
            if (++m_definitions > 1) {
                throw new InvalidOperationException(message: "faulting observer");
            }
        }
        public void DeliverSessionLever(WorldSessionLever lever) {
        }
        public void DeliverSnapshot(in WorldSnapshot snapshot) {
        }
        public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp) {
        }
    }
}
