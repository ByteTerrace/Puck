using Puck.Commands;
using System.Numerics;
using Puck.Assets.Documents;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a contribution slot's host-authored half is authored and its server-stamped half is never authorable.
/// Filling a slot stamps the ACTING principal off the envelope, a submission that names <c>contributor</c> or
/// <c>retractDeadlineTick</c> is refused, a presence slot's deadline arms only while its watched link reads dropped
/// and clears the tick it comes back, expiry retracts the piece while the host's frame stands, and a retraction that
/// would tear a possessed inhabitant out of its drive grant defers instead.
/// <para>Every arm pairs a denial with a control that differs in exactly one fact, and no arm lets the acting
/// principal and the named principal be the same identity.</para>
/// </summary>
public sealed class ContributionTenureLawTests {
    private const string ContributedCreation = "statue";
    private const float ContributionGraceSeconds = 0.5f;
    private const string LinkName = "north";
    private const float LivenessGraceSeconds = 0.25f;
    private const string SlotCreation = "plinth";
    private const string SlotId = "plaza-slot";

    // Both graces are authored in seconds, and one fixture step is one simulation tick, so every step count below
    // is derived from the document's own rate rather than written out.
    private static readonly ulong ContributionGraceTicks = WorldSimulationTickConversion.DurationTicks(
        ratePerSecond: ((uint)Fixtures.DefaultRateHz),
        seconds: ContributionGraceSeconds
    );
    private static readonly ulong LivenessGraceTicks = WorldSimulationTickConversion.DurationTicks(
        ratePerSecond: ((uint)Fixtures.DefaultRateHz),
        seconds: LivenessGraceSeconds
    );

    // The base fixture: two creations (the host's empty plinth and the partner's statue), one authored adjacency whose
    // liveness grace is short enough to drop inside a handful of fixture steps, and one EMPTY presence slot.
    private static WorldDefinition Document(WorldPlacementInhabit? inhabit = null) {
        var document = Fixtures.BuildDocument();

        return (document with {
            CreationsRaw = [
                CreationFixtures.UnitSphere(id: SlotCreation),
                CreationFixtures.UnitSphere(id: ContributedCreation),
            ],
            PlacementRowsRaw = [
                new WorldPlacement(
                Id: SlotId,
                PrototypeId: SlotCreation,
                Position: new DocumentVector3(value: new Vector3(
                    x: 3f,
                    y: 0f,
                    z: 4f
                )),
                YawDegrees: 45f,
                Scale: 2f,
                Inhabit: inhabit,
                Contribution: new WorldPlacementContribution(
                    Tenure: WorldContributionTenure.Presence,
                    SlotCreationId: SlotCreation,
                    Link: SafeName.Parse(candidate: LinkName),
                    GraceSeconds: ContributionGraceSeconds
                )
            ),
            ],
            PopulationRaw = (document.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1) }),
            References = [
                new WorldReference(
                Name: SafeName.Parse(candidate: "peer"),
                Document: "peer",
                Owner: null,
                World: null
            ),
            ],
            Destinations = [
                new WorldDestination(
                Name: SafeName.Parse(candidate: "peer"),
                Reference: "peer",
                Durability: WorldDestinationDurability.Persisted,
                Scope: WorldDestinationScope.Global
            ),
            ],
            Adjacencies = [
                new WorldAdjacency(
                Name: SafeName.Parse(candidate: LinkName),
                Destination: "peer",
                Counterpart: "south",
                Boundary: new WorldAdjacencyBoundary(
                    Center: new DocumentVector3(value: new Vector3(
                        x: 0f,
                        y: 0f,
                        z: -12f
                    )),
                    OutwardYawDegrees: 0f,
                    OutwardPitchDegrees: 0f,
                    Width: 24f,
                    Height: 16f
                ),
                LivenessGraceSeconds: LivenessGraceSeconds
            ),
            ],
        });
    }
    // Drives the link to dropped: no delivery is observed, so the feed's staleness climbs past the authored liveness
    // grace. One step of headroom past the grace so the drop edge has certainly been crossed.
    private static void DropLink(WorldFixture fixture) {
        for (var index = 0UL; (index <= (LivenessGraceTicks + 1UL)); index++) {
            fixture.Step();
        }
    }
    private static WorldDefinition EndowedDocument() {
        var document = Document();
        var slot = document.Placements[0];

        return (document with {
            PlacementRowsRaw = [
                (slot with {
                Contribution = new WorldPlacementContribution(
                Tenure: WorldContributionTenure.Endowed,
                SlotCreationId: SlotCreation
            ),
            }),
            ],
        });
    }
    private static WorldPlacementContribution Facet(WorldFixture fixture) => Slot(fixture: fixture).Contribution!;
    // Fills the slot the way a partner does — an ordinary whole-row UpsertPlacement re-pointing prototypeId, carrying
    // NO stamped half. `actor` is the identity the ingress would have stamped on the envelope.
    private static void Fill(WorldFixture fixture, Principal actor) {
        var slot = Slot(fixture: fixture);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertPlacement(
            Placement: (slot with { PrototypeId = ContributedCreation }),
            Principal: actor
        ));
        fixture.Step();
    }
    // Keeps the link live: one observed delivery per step, which is what resets the feed's staleness counter.
    private static void RefreshLink(WorldFixture fixture, int steps, ref ulong deliveredTick) {
        for (var index = 0; (index < steps); index++) {
            deliveredTick++;
            _ = fixture.Server.Events.ObserveLinkDelivery(
                adjacencyName: LinkName,
                deliveredTick: deliveredTick
            );
            fixture.Step();
        }
    }
    private static WorldPlacement Slot(WorldFixture fixture) => WorldDefinitionRows.FindPlacement(
        id: SlotId,
        placements: fixture.Server.Definition.Placements
    )!;

    /// <summary>DENIAL: a retraction whose slot carries a possessed inhabitant defers — the deadline stands, the
    /// piece stays, and the drive grant's binding survives. CONTROL: the identical slot with the possession revoked
    /// retracts on the very next sweep.</summary>
    [Fact]
    public void CarrierPossessedDefersRetraction() {
        using var fixture = Fixtures.FreshServer(definition: Document(inhabit: new WorldPlacementInhabit(
            Kit: Fixtures.SeatKitName,
            Look: null,
            Source: IntentSource.Idle,
            Distribution: WorldDistribution.Default
        )));

        Fill(
            actor: Principal.Console,
            fixture: fixture
        );

        Assert.NotNull(@object: Facet(fixture: fixture).Contributor);

        // Arm first, then possess: the arm is itself an UpsertPlacement, so it reconciles the inhabitant set — a
        // grant taken over a pre-arm body index would name a slot the arm had already re-seated.
        DropLink(fixture: fixture);
        fixture.Step();
        Assert.NotNull(@object: Facet(fixture: fixture).RetractDeadlineTick);

        var inhabitants = new List<int>();

        fixture.Server.Population.CollectInhabitants(
            into: inhabitants,
            placementId: SlotId
        );
        Assert.NotEmpty(collection: inhabitants);

        var body = inhabitants[0];
        var possessor = Principal.Seat(slot: 0);

        var possession = new WorldGrant(
            Grantee: possessor,
            Capability: WorldCapability.Drive,
            Subject: GrantSubject.Body(index: body),
            Exclusive: false
        );

        fixture.Server.Grant(
            actor: Principal.Console,
            grant: possession
        );

        for (var index = 0UL; (index <= (ContributionGraceTicks + 2UL)); index++) {
            fixture.Step();
        }

        // DENIAL: the deadline armed and has long passed, yet the piece is still standing and still stamped.
        var deferred = Facet(fixture: fixture);

        Assert.NotNull(@object: deferred.RetractDeadlineTick);
        Assert.True(condition: (deferred.RetractDeadlineTick!.Value < unchecked((long)(fixture.Server.NextInputTick - 1UL))));
        Assert.Equal(
            actual: Slot(fixture: fixture).PrototypeId,
            expected: ContributedCreation
        );
        Assert.NotNull(@object: deferred.Contributor);

        // CONTROL: one fact changes — the possession goes — and the same standing deadline retracts.
        fixture.Server.Revoke(
            actor: Principal.Console,
            grant: possession
        );

        fixture.Step();
        fixture.Step();

        Assert.Equal(
            actual: Slot(fixture: fixture).PrototypeId,
            expected: SlotCreation
        );
        Assert.Null(@object: Facet(fixture: fixture).Contributor);
    }
    /// <summary>DENIAL: a link that keeps delivering never arms a deadline. CONTROL: the same slot, with deliveries
    /// withheld, arms one — and a delivery landing before it expires clears it again.</summary>
    [Fact]
    public void DeadlineArmsOnDropAndDisarmsOnRefresh() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var deliveredTick = 0UL;

        Fill(
            actor: Principal.Console,
            fixture: fixture
        );

        // DENIAL: a live link, refreshed every tick well past the liveness grace, arms nothing.
        RefreshLink(
            deliveredTick: ref deliveredTick,
            fixture: fixture,
            steps: (checked((int)LivenessGraceTicks) + 8)
        );
        Assert.Null(@object: Facet(fixture: fixture).RetractDeadlineTick);

        // CONTROL: one fact changes — the deliveries stop — and the same slot arms.
        DropLink(fixture: fixture);
        fixture.Step();

        var armed = Facet(fixture: fixture).RetractDeadlineTick;

        Assert.NotNull(@object: armed);
        Assert.True(condition: (armed!.Value > unchecked((long)(fixture.Server.NextInputTick - 1UL))));

        // And the reconnect half: a delivery inside the contribution grace clears the stamp outright.
        RefreshLink(
            deliveredTick: ref deliveredTick,
            fixture: fixture,
            steps: 3
        );
        Assert.Null(@object: Facet(fixture: fixture).RetractDeadlineTick);
        Assert.Equal(
            actual: Slot(fixture: fixture).PrototypeId,
            expected: ContributedCreation
        );
        Assert.NotNull(@object: Facet(fixture: fixture).Contributor);
    }
    /// <summary>The expiry contract: the host's frame stands and only the piece goes — id, pose, scale and the whole
    /// authored half survive, prototypeId returns to the authored slotCreationId, the stamped half clears, and the
    /// contributed creation row is released. CONTROL: an <c>endowed</c> slot under the identical dropped link is
    /// untouched.</summary>
    [Fact]
    public void ExpiryRetractsThePieceAndLeavesTheFrame() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        Fill(
            actor: Principal.Console,
            fixture: fixture
        );

        var filled = Slot(fixture: fixture);

        DropLink(fixture: fixture);

        for (var index = 0UL; (index <= (ContributionGraceTicks + 2UL)); index++) {
            fixture.Step();
        }

        var retracted = Slot(fixture: fixture);
        var facet = retracted.Contribution!;

        Assert.Equal(
            actual: retracted.PrototypeId,
            expected: SlotCreation
        );
        Assert.Null(@object: facet.Contributor);
        Assert.Null(@object: facet.RetractDeadlineTick);

        // The frame: everything the HOST authored is byte-identical across the retraction.
        Assert.Equal(
            actual: retracted.Id,
            expected: filled.Id
        );
        Assert.Equal(
            actual: retracted.Position,
            expected: filled.Position
        );
        Assert.Equal(
            actual: retracted.YawDegrees,
            expected: filled.YawDegrees
        );
        Assert.Equal(
            actual: retracted.Scale,
            expected: filled.Scale
        );
        Assert.Equal(
            actual: facet.Tenure,
            expected: WorldContributionTenure.Presence
        );
        Assert.Equal(
            actual: facet.SlotCreationId,
            expected: SlotCreation
        );
        Assert.Equal(
            actual: facet.GraceSeconds,
            expected: ContributionGraceSeconds
        );

        // The contributed creation row is released once nothing names it.
        Assert.Null(@object: WorldDefinitionRows.FindCreation(
            creations: fixture.Server.Definition.Creations,
            id: ContributedCreation
        ));

        // CONTROL: the same dropped link over an ENDOWED slot retracts nothing.
        using var endowed = Fixtures.FreshServer(definition: EndowedDocument());

        endowed.Server.EnqueueMutation(mutation: new WorldMutation.UpsertPlacement(
            Placement: (Slot(fixture: endowed) with { PrototypeId = ContributedCreation }),
            Principal: Principal.Console
        ));
        endowed.Step();
        DropLink(fixture: endowed);

        for (var index = 0; (index < 50); index++) {
            endowed.Step();
        }

        Assert.Equal(
            actual: Slot(fixture: endowed).PrototypeId,
            expected: ContributedCreation
        );
        Assert.NotNull(@object: Facet(fixture: endowed).Contributor);
        Assert.Null(@object: Facet(fixture: endowed).RetractDeadlineTick);
    }
    /// <summary>A retraction releases the contributed creation only when nothing names it
    /// (<see cref="WorldDefinitionRows.EnumerateCreationReferences"/>). DENIAL: a second, responsive placement names
    /// the statue in a <c>respond</c> entry, so the retraction leaves the statue's row standing and nothing is refused,
    /// then or on any tick after; a direct removal is refused naming that entry. CONTROL:
    /// <see cref="ExpiryRetractsThePieceAndLeavesTheFrame"/>, where nothing else names it, releases it.</summary>
    [Fact]
    public void ARetractionKeepsACreationAResponseStillNames() {
        const string Beacon = "beacon";
        var document = Document();
        using var fixture = Fixtures.FreshServer(definition: (document with {
            PlacementRowsRaw = [
                .. document.Placements,
                new WorldPlacement(
                Id: Beacon,
                PrototypeId: SlotCreation,
                Position: new DocumentVector3(value: new Vector3(
                    x: -3f,
                    y: 0f,
                    z: 4f
                )),
                YawDegrees: 0f,
                Scale: 1f,
                Respond: [
                    new WorldPlacementResponse(
                    When: new WorldPlacementResponseCondition.StateCondition(
                        Comparison: ExpressionOp.GreaterOrEqual,
                        State: "lit",
                        Value: 1f
                    ),
                    PrototypeId: ContributedCreation
                ),
                ]
            ),
            ],
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: 0L))],
                Kind: CellKind.Int,
                Name: CellName.Parse(candidate: "lit")
            ),
            ]),
        }));
        var rejections = new List<string>();

        fixture.Server.EchoTap = echo => {
            if (echo.Rejected) {
                rejections.Add(item: echo.Message);
            }
        };
        Fill(
            actor: Principal.Console,
            fixture: fixture
        );
        DropLink(fixture: fixture);

        for (var index = 0UL; (index <= (ContributionGraceTicks + 8UL)); index++) {
            fixture.Step();
        }

        Assert.Equal(
            actual: Slot(fixture: fixture).PrototypeId,
            expected: SlotCreation
        );
        Assert.Null(@object: Facet(fixture: fixture).Contributor);
        Assert.NotNull(@object: WorldDefinitionRows.FindCreation(
            creations: fixture.Server.Definition.Creations,
            id: ContributedCreation
        ));
        Assert.Empty(collection: rejections);

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.RemoveCreation(
            Id: ContributedCreation,
            Principal: Principal.Console
        ));
        fixture.Step();

        Assert.Contains(
            actualString: Assert.Single(collection: rejections),
            expectedSubstring: $"creation '{ContributedCreation}' is still named by placements.{Beacon}.respond[0].prototypeId"
        );
    }
    /// <summary>DENIAL: a quiet tick — an unchanged document, a watched link reading as it did at the last index
    /// rebuild, and nothing due — reads no placement row at all, so the sweep costs one liveness verdict per watched
    /// link and four deadline-table front comparisons. CONTROL: one fact changes — the watched link drops — and the
    /// same sweep reads the slot to arm it.</summary>
    [Fact]
    public void AQuietTickReadsNoPlacement() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        Fill(
            actor: Principal.Console,
            fixture: fixture
        );
        // The fill swapped the document, so the next sweep rebuilds the index; measure from after it.
        fixture.Step();
        fixture.Step();

        var quietFrom = fixture.Server.Tick.TenurePlacementReads;

        // DENIAL: well inside the authored liveness grace, so the link still reads live, the document has settled,
        // and nothing is due.
        for (var index = 0; (index < 4); index++) {
            fixture.Step();
        }

        Assert.Equal(
            actual: fixture.Server.Tick.TenurePlacementReads,
            expected: quietFrom
        );
        Assert.Null(@object: Facet(fixture: fixture).RetractDeadlineTick);

        // CONTROL: the link drops, which is the one thing that makes the sweep reach a placement again.
        DropLink(fixture: fixture);

        Assert.True(condition: (fixture.Server.Tick.TenurePlacementReads > quietFrom));
    }
    /// <summary>DENIAL: a submission that names <c>contributor</c> is refused and changes nothing. CONTROL: the same
    /// submission without it applies and stamps the ACTING principal — which is a different identity from the one
    /// the denied payload tried to name.</summary>
    [Fact]
    public void FillStampsTheActingPrincipalNeverThePayload() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        var slot = Slot(fixture: fixture);
        var actor = Principal.Console;
        var impersonated = Principal.Seat(slot: 1);

        Assert.NotEqual(
            actual: impersonated,
            expected: actor
        );

        // DENIAL.
        var before = fixture.DefinitionBytes();

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertPlacement(
            Placement: (slot with {
                PrototypeId = ContributedCreation,
                Contribution = (slot.Contribution! with { Contributor = impersonated }),
            }),
            Principal: actor
        ));
        fixture.Step();

        Assert.Equal(
            actual: fixture.DefinitionBytes(),
            expected: before
        );

        // DENIAL, the deadline half: naming a deadline is refused on the same terms.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertPlacement(
            Placement: (slot with {
                PrototypeId = ContributedCreation,
                Contribution = (slot.Contribution! with { RetractDeadlineTick = 1L }),
            }),
            Principal: actor
        ));
        fixture.Step();

        Assert.Equal(
            actual: fixture.DefinitionBytes(),
            expected: before
        );

        // CONTROL: the identical fill without the stamped half applies, and the stamp reads the ACTOR.
        Fill(
            actor: actor,
            fixture: fixture
        );

        Assert.Equal(
            actual: Slot(fixture: fixture).PrototypeId,
            expected: ContributedCreation
        );
        Assert.Equal(
            actual: Facet(fixture: fixture).Contributor,
            expected: actor
        );
        Assert.NotEqual(
            actual: Facet(fixture: fixture).Contributor,
            expected: impersonated
        );
    }
}
