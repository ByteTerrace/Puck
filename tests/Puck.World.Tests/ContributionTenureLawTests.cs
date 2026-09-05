using System.Numerics;
using Puck.Assets.Documents;
using Puck.World.Authoring;
using Puck.SignedDistance;
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

    private static readonly ulong s_livenessGraceTicks = WorldSimulationTickConversion.DurationTicks(
        ratePerSecond: 240U,
        seconds: LivenessGraceSeconds
    );

    private static WorldPrototype Creation(string id) {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: id,
            Palette: null,
            Shapes: [
                new ShapeDocument(
                    Id: 0,
                    Name: null,
                    Type: SdfSolidPrimitive.Sphere,
                    Position: Vector3.Zero,
                    Rotation: Quaternion.Identity,
                    Scale: new Vector3(value: 1f),
                    Material: 0,
                    Blend: SdfBlendOp.Union,
                    Smooth: 0f,
                    Group: 0
                ),
            ],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: id
        );

        return new WorldPrototype(
            Id: id,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    // The base fixture: two creations (the host's empty plinth and the partner's statue), one authored adjacency whose
    // liveness grace is short enough to drop inside a handful of fixture steps, and one EMPTY presence slot.
    private static WorldDefinition Document(WorldPlacementInhabit? inhabit = null) {
        var document = Fixtures.BuildDocument();

        return (document with {
            CreationsRaw = [
                Creation(id: SlotCreation),
                Creation(id: ContributedCreation),
            ],
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: SlotId,
                    PrototypeId: SlotCreation,
                    Position: new DocumentVector3(value: new Vector3(x: 3f, y: 0f, z: 4f)),
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
                    Document: "peer.world.json",
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
                        Center: new DocumentVector3(value: new Vector3(x: 0f, y: 0f, z: -12f)),
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
    private static WorldPlacementContribution Facet(WorldFixture fixture) => Slot(fixture: fixture).Contribution!;
    // Drives the link to dropped: no delivery is observed, so the feed's staleness climbs past the authored liveness
    // grace. One step of headroom past the grace so the drop edge has certainly been crossed.
    private static void DropLink(WorldFixture fixture) {
        for (var index = 0UL; (index <= (s_livenessGraceTicks + 1UL)); index++) {
            fixture.Step();
        }
    }
    // Fills the slot the way a partner does — an ordinary whole-row UpsertPlacement re-pointing prototypeId, carrying
    // NO stamped half. `actor` is the identity the ingress would have stamped on the envelope.
    private static void Fill(WorldFixture fixture, WorldPrincipal actor) {
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
            actor: WorldPrincipal.Console,
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
        var possessor = WorldPrincipal.Seat(slot: 0);

        var possession = new WorldGrant(
            Principal: possessor,
            Capability: WorldCapability.Drive,
            Subject: GrantSubject.Body(index: body),
            Exclusive: false
        );

        fixture.Server.Grant(
            actor: WorldPrincipal.Console,
            grant: possession
        );

        for (var index = 0; (index < 400); index++) {
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
            actor: WorldPrincipal.Console,
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
            actor: WorldPrincipal.Console,
            fixture: fixture
        );

        // DENIAL: a live link, refreshed every tick well past the liveness grace, arms nothing.
        RefreshLink(
            deliveredTick: ref deliveredTick,
            fixture: fixture,
            steps: (checked((int)s_livenessGraceTicks) + 8)
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
            actor: WorldPrincipal.Console,
            fixture: fixture
        );

        var filled = Slot(fixture: fixture);

        DropLink(fixture: fixture);

        for (var index = 0; (index < 400); index++) {
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
            Principal: WorldPrincipal.Console
        ));
        endowed.Step();
        DropLink(fixture: endowed);

        for (var index = 0; (index < 400); index++) {
            endowed.Step();
        }

        Assert.Equal(
            actual: Slot(fixture: endowed).PrototypeId,
            expected: ContributedCreation
        );
        Assert.NotNull(@object: Facet(fixture: endowed).Contributor);
        Assert.Null(@object: Facet(fixture: endowed).RetractDeadlineTick);
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

    /// <summary>DENIAL: a submission that names <c>contributor</c> is refused and changes nothing. CONTROL: the same
    /// submission without it applies and stamps the ACTING principal — which is a different identity from the one
    /// the denied payload tried to name.</summary>
    [Fact]
    public void FillStampsTheActingPrincipalNeverThePayload() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        var slot = Slot(fixture: fixture);
        var actor = WorldPrincipal.Console;
        var impersonated = WorldPrincipal.Seat(slot: 1);

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
