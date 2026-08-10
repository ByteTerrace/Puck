using System.Numerics;

using Xunit;

using Puck.Forge.Authoring;
using Puck.SdfVm;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the border-margin strip (<see cref="WorldPlacementPortal.MarginDepth"/>) — the cross-document
/// proof <see cref="WorldDefinitionValidator"/> runs through an injected <see cref="IWorldNeighbourResolver"/> when a
/// mapped portal facet authors one. Every refusal below is paired with a control that differs in exactly one
/// authored or resolved fact, per this repository's red-first discipline.
/// </summary>
public sealed class MarginStripValidationLawTests {
    private const string DestinationName = "dest";
    private const string ReferenceName = "dest-ref";
    private const string ReferenceDocument = "neighbour.world.json";
    private const string DoorFace = "door";
    private const string DoorPlacementId = "door-placement";

    // A Capsule collider (the same shape docs/verification's own fixtures author) so ColliderReach has a non-zero
    // term to prove — reach alone (0.35) already exceeds a deliberately-shallow "below floor" authored value.
    private static readonly WorldCollider DoorCollider = new WorldCollider.Capsule(Endpoint: new Vector3(x: 0f, y: 1f, z: 0f), Radius: 0.35f);

    // Mirrors PortalArrivalValidationLawTests.BuildDoorCreation: a minimal creation declaring ONE box-shaped face
    // (the aperture ValidateFaceSources' own format checks require of a portal-bearing face), so a document built
    // from it clears every OTHER validation pass and a refusal below can only be about marginDepth.
    private static WorldCreation BuildDoorCreation() {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: AvatarPrimitive.Box,
            Position: Vector3.Zero,
            Rotation: Quaternion.Identity,
            Scale: Vector3.One,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "door",
            Intent: CreatorIntent.Object,
            BakeStyle: null,
            Palette: null,
            Shapes: [shape],
            Frames: null,
            Behavior: new CreationBehaviorDocument(Locomotion: null, Faces: [new CreationFaceDocument(Name: DoorFace, ShapeId: 0, DefaultSource: null)])
        );
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "door");

        return new WorldCreation(Id: "door", Document: canonical.Document, Hash: canonical.Hash);
    }

    // A document authoring one mapped portal facet at DoorPlacementId/DoorFace, with a Capsule collider on its one
    // kit (WorldFacePortalPolicy.SpeedCeiling and TryColliderReach both read the SAME kit table). Used for BOTH the
    // local document under test and the neighbour a stub resolver hands back — the counterpart string
    // "door-placement/door" resolves against either one's OWN placement of that id/face, never a shared object.
    // placementScale controls the derived face's HalfWidth/HalfHeight (WorldFaceFrame scales the shape's own Scale
    // by the placement's) — varied by the dimension-parity laws below, 1f (matching) everywhere else. collider
    // overrides the kit's default Capsule — varied by the unreadable-collider laws below.
    private static WorldDefinition BuildBorderDocument(float? marginDepth, float placementScale = 1f, WorldCollider? collider = null, WorldDestinationScope scope = WorldDestinationScope.Global) {
        var creation = BuildDoorCreation();
        var portal = new WorldPlacementPortal(Destination: DestinationName, Arrival: WorldPortalArrival.Mapped, Counterpart: $"{DoorPlacementId}/{DoorFace}", MarginDepth: marginDepth);
        var placement = new WorldPlacement(
            Id: DoorPlacementId,
            CreationId: creation.Id,
            Position: Vector3.Zero,
            YawDegrees: 0f,
            Scale: placementScale,
            FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None(), Portal: portal)]
        );
        var baseline = Fixtures.BuildDocument();

        return baseline with {
            Kits = [baseline.Kits[0] with { Collider = (collider ?? DoorCollider) }],
            Creations = [creation],
            Placements = [placement],
            References = [new WorldReference(Name: WorldSafeName.Parse(candidate: ReferenceName), Document: ReferenceDocument)],
            Destinations = [new WorldDestination(Name: WorldSafeName.Parse(candidate: DestinationName), Reference: ReferenceName, Durability: WorldDestinationDurability.Ephemeral, Scope: scope)],
        };
    }

    private sealed class StubNeighbourResolver(Func<string, WorldNeighbourResolution> resolve) : IWorldNeighbourResolver {
        public int CallCount { get; private set; }

        public WorldNeighbourResolution Resolve(string document) {
            CallCount++;

            return resolve(document);
        }
    }

    private static void EnqueueLoad(WorldFixture fixture, WorldDefinition definition) {
        var bytes = WorldDefinitionSerialization.Serialize(definition: definition);

        fixture.Server.EnqueueRebuild(
            request: new WorldRebuildRequest(
                Kind: WorldRebuildKind.Load,
                Definition: definition,
                PathHint: "margin-law.world.json",
                Force: true,
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: bytes)),
            principal: WorldPrincipal.Console);
    }

    [Fact]
    public void MarginDepthAuthored_NoResolverSupplied_RefusesByName() {
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f), reason: out var reason, neighbours: null));
        Assert.Contains(expectedSubstring: "no neighbour resolver was supplied", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MarginDepthAuthored_NoResolverSupplied_RefusesByName_ControlParsesClean() {
        Laws.RefusalWithControl(
            lawId: "margin-strip.no-resolver",
            deniedOutcome: static () => WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f), reason: out _, neighbours: null),
            controlOutcome: static () => WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: null), reason: out _, neighbours: null));
    }

    [Fact]
    public void ViewerScopedMarginDepth_RefusesUntilContactResolutionIsPerBody() {
        var resolver = new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f, scope: WorldDestinationScope.User), reason: out var reason, neighbours: resolver));
        Assert.Contains(expectedSubstring: "one shared authority-side contact field cannot select a viewer-specific neighbour", actualString: reason, comparisonType: StringComparison.Ordinal);

        Laws.RefusalWithControl(
            lawId: "margin-strip.viewer-scope",
            deniedOutcome: () => WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f, scope: WorldDestinationScope.User), reason: out _, neighbours: resolver),
            controlOutcome: () => WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f), reason: out _, neighbours: resolver));
    }

    [Fact]
    public void UnreachableNeighbour_RefusesByName() {
        var resolver = new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Unavailable(reason: "simulated transport failure"));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f), reason: out var reason, neighbours: resolver));
        Assert.Contains(expectedSubstring: "could not be reached", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "simulated transport failure", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void UnreachableNeighbour_RefusesByName_ControlParsesClean() {
        Laws.RefusalWithControl(
            lawId: "margin-strip.unreachable-neighbour",
            deniedOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 2f),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Unavailable(reason: "simulated"))),
            controlOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 2f),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)))));
    }

    [Fact]
    public void CounterpartThatDoesNotMapBackToSourceFace_RefusesByName() {
        var neighbour = BuildBorderDocument(marginDepth: 2f);
        var placement = neighbour.Placements[0];
        var face = placement.FaceSources![0];
        var nonreciprocal = neighbour with {
            Placements = [placement with {
                FaceSources = [face with { Portal = face.Portal! with { Counterpart = "somewhere-else/door" } }],
            }],
        };
        var resolver = new StubNeighbourResolver(resolve: _ => WorldNeighbourResolution.Resolved(definition: nonreciprocal));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f), reason: out var reason, neighbours: resolver));
        Assert.Contains(expectedSubstring: "cannot be proven reciprocal", actualString: reason, comparisonType: StringComparison.Ordinal);

        Laws.RefusalWithControl(
            lawId: "margin-strip.reciprocal-face",
            deniedOutcome: () => WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f), reason: out _, neighbours: resolver),
            controlOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 2f),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)))));
    }

    [Fact]
    public void BelowDerivedFloor_RefusesByName() {
        // 0.1 sits below the collider reach alone (0.35 Capsule radius) — the floor is provably taller before the
        // speed/rate term even contributes.
        var resolver = new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 0.1f)));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 0.1f), reason: out var reason, neighbours: resolver));
        Assert.Contains(expectedSubstring: "below the derived floor", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void BelowDerivedFloor_RefusesByName_ControlParsesClean() {
        Laws.RefusalWithControl(
            lawId: "margin-strip.below-floor",
            deniedOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 0.1f),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 0.1f)))),
            controlOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 2f),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)))));
    }

    [Fact]
    public void MismatchedNeighbourDepth_RefusesByName() {
        var resolver = new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 3f)));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f), reason: out var reason, neighbours: resolver));
        Assert.Contains(expectedSubstring: "must be bit-identical", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MismatchedNeighbourDepth_RefusesByName_ControlParsesClean() {
        Laws.RefusalWithControl(
            lawId: "margin-strip.mismatched-depth",
            deniedOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 2f),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 3f)))),
            controlOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 2f),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)))));
    }

    [Fact]
    public void NeighbourAuthorsNoMarginDepth_RefusesByName() {
        var resolver = new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: null)));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f), reason: out var reason, neighbours: resolver));
        Assert.Contains(expectedSubstring: "authors no marginDepth of its own", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void NeighbourAuthorsNoMarginDepth_RefusesByName_ControlParsesClean() {
        Laws.RefusalWithControl(
            lawId: "margin-strip.neighbour-silent",
            deniedOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 2f),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: null)))),
            controlOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 2f),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)))));
    }

    [Fact]
    public void MarginDepthWithoutMappedArrival_RefusesByName() {
        var portal = new WorldPlacementPortal(Destination: DestinationName, Arrival: WorldPortalArrival.Spawn, Counterpart: null, MarginDepth: 2f);
        var creation = BuildDoorCreation();
        var placement = new WorldPlacement(Id: DoorPlacementId, CreationId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None(), Portal: portal)]);
        var baseline = Fixtures.BuildDocument();
        var document = baseline with {
            Creations = [creation],
            Placements = [placement],
            References = [new WorldReference(Name: WorldSafeName.Parse(candidate: ReferenceName), Document: ReferenceDocument)],
            Destinations = [new WorldDestination(Name: WorldSafeName.Parse(candidate: DestinationName), Reference: ReferenceName, Durability: WorldDestinationDurability.Ephemeral)],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: document, reason: out var reason, neighbours: null));
        Assert.Contains(expectedSubstring: "marginDepth", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "arrival is not 'mapped'", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MarginDepthWithoutMappedArrival_RefusesByName_ControlParsesClean() {
        Laws.RefusalWithControl(
            lawId: "margin-strip.marginDepth-without-mapped",
            deniedOutcome: static () => {
                var portal = new WorldPlacementPortal(Destination: DestinationName, Arrival: WorldPortalArrival.Spawn, Counterpart: null, MarginDepth: 2f);
                var creation = BuildDoorCreation();
                var placement = new WorldPlacement(Id: DoorPlacementId, CreationId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None(), Portal: portal)]);

                return WorldDefinitionValidator.TryValidate(definition: Fixtures.BuildDocument() with {
                    Creations = [creation],
                    Placements = [placement],
                    References = [new WorldReference(Name: WorldSafeName.Parse(candidate: ReferenceName), Document: ReferenceDocument)],
                    Destinations = [new WorldDestination(Name: WorldSafeName.Parse(candidate: DestinationName), Reference: ReferenceName, Durability: WorldDestinationDurability.Ephemeral)],
                }, reason: out _, neighbours: null);
            },
            controlOutcome: static () => WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: null), reason: out _, neighbours: null));
    }

    // The arrival isometry applies (-SeamU, SeamV) onto the
    // counterpart's own frame — a 4-wide local face crossed at u=3 against a 1-wide counterpart lands the arrival
    // at u=3 on a face that is not 3 units wide, off the door (or off any strip) entirely. Nothing before this
    // refused a counterpart whose half-extents simply differ from the local face's own. placementScale is the
    // lever: the SAME door creation scaled 4x locally against 1x on the neighbour reproduces exactly that gap.
    [Fact]
    public void CounterpartDimensionMismatch_RefusesByName() {
        var resolver = new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f, placementScale: 1f)));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f, placementScale: 4f), reason: out var reason, neighbours: resolver));
        Assert.Contains(expectedSubstring: "shared strip needs matching door dimensions", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void CounterpartDimensionMismatch_RefusesByName_ControlMatchingDimensionsValidates() {
        Laws.RefusalWithControl(
            lawId: "margin-strip.dimension-mismatch",
            deniedOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 2f, placementScale: 4f),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f, placementScale: 1f)))),
            controlOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 2f, placementScale: 1f),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f, placementScale: 1f)))));
    }

    // ADVERSARIAL REVIEW FINDING P2 #2: a kit whose collider is FromCreation (or any future arm the reach walk does
    // not know how to measure) used to fold silently to zero reach — the door-not-type class, and the worst
    // possible direction for a safety floor. It must refuse the marginDepth claim by name instead.
    [Fact]
    public void UnreadableColliderKind_RefusesByName() {
        var resolver = new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f, collider: new WorldCollider.FromCreation(CreationId: "door")), reason: out var reason, neighbours: resolver));
        Assert.Contains(expectedSubstring: "declares a collider this floor cannot measure", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableColliderKind_RefusesByName_ControlWithReadableColliderValidates() {
        Laws.RefusalWithControl(
            lawId: "margin-strip.unreadable-collider",
            deniedOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 2f, collider: new WorldCollider.FromCreation(CreationId: "door")),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)))),
            controlOutcome: static () => WorldDefinitionValidator.TryValidate(
                definition: BuildBorderDocument(marginDepth: 2f),
                reason: out _,
                neighbours: new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)))));
    }

    [Fact]
    public void WellFormedMarginStrip_ValidatesClean() {
        var resolver = new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)));

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: BuildBorderDocument(marginDepth: 2f), reason: out var reason, neighbours: resolver), userMessage: reason);
    }

    [Fact]
    public void RebuildProvesTheNeighbourBeforeEnqueue_AndDoesNotReadItFromStep() {
        using var fixture = Fixtures.FreshServer(definition: BuildBorderDocument(marginDepth: null));
        var resolver = new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)));
        fixture.Server.Neighbours = resolver;

        EnqueueLoad(fixture: fixture, definition: BuildBorderDocument(marginDepth: 2f));

        Assert.Equal(expected: 1, actual: resolver.CallCount);

        fixture.Step();

        Assert.Equal(expected: 1, actual: resolver.CallCount);
        Assert.Contains(collection: fixture.Server.Definition.Placements, filter: static placement => placement.Id == DoorPlacementId);
    }

    [Fact]
    public void RebuildWithPathHint_UsesCandidateRelativeResolverInsteadOfCurrentOriginResolver() {
        using var fixture = Fixtures.FreshServer(definition: BuildBorderDocument(marginDepth: null));
        var currentOrigin = new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Unavailable(reason: "old directory"));
        var candidateOrigin = new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)));
        string? requestedPath = null;
        fixture.Server.Neighbours = currentOrigin;
        fixture.Server.RebuildNeighbours = path => {
            requestedPath = path;

            return candidateOrigin;
        };

        EnqueueLoad(fixture: fixture, definition: BuildBorderDocument(marginDepth: 2f));

        Assert.Equal(expected: "margin-law.world.json", actual: requestedPath);
        Assert.Equal(expected: 0, actual: currentOrigin.CallCount);
        Assert.Equal(expected: 1, actual: candidateOrigin.CallCount);

        fixture.Step();

        Assert.Contains(collection: fixture.Server.Definition.Placements, filter: static placement => placement.Id == DoorPlacementId);
    }

    [Fact]
    public void UnrelatedMutationOnAProvenMarginDocument_RevalidatesLocallyAndApplies() {
        using var fixture = Fixtures.FreshServer(definition: BuildBorderDocument(marginDepth: null));
        var resolver = new StubNeighbourResolver(resolve: static _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: 2f)));
        fixture.Server.Neighbours = resolver;
        EnqueueLoad(fixture: fixture, definition: BuildBorderDocument(marginDepth: 2f));
        fixture.Step();
        var echoes = new List<WorldEditEcho>();
        fixture.Server.EchoTap = echoes.Add;

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetRenderDefaults(Principal: WorldPrincipal.Console, Render: fixture.Server.Definition.Render));
        fixture.Step();

        Assert.Contains(collection: echoes, filter: static echo => (echo.Mutation is WorldMutation.SetRenderDefaults) && !echo.Rejected);
        Assert.Equal(expected: 1, actual: resolver.CallCount);
    }

    [Fact]
    public void MarginProofInputMutation_RefusesByName_ControlWithoutMarginApplies() {
        static bool Apply(float? marginDepth, out string message) {
            using var fixture = Fixtures.FreshServer(definition: BuildBorderDocument(marginDepth: null));
            var resolver = new StubNeighbourResolver(resolve: _ => WorldNeighbourResolution.Resolved(definition: BuildBorderDocument(marginDepth: marginDepth)));
            fixture.Server.Neighbours = resolver;
            WorldEditEcho? loadOutcome = null;
            fixture.Server.EchoTap = echo => loadOutcome = echo;
            EnqueueLoad(fixture: fixture, definition: BuildBorderDocument(marginDepth: marginDepth));
            fixture.Step();
            Assert.True(condition: loadOutcome is { Rejected: false }, userMessage: loadOutcome?.Message);
            Assert.Equal(expected: marginDepth, actual: fixture.Server.Definition.Placements[0].FaceSources![0].Portal!.MarginDepth);
            WorldEditEcho? outcome = null;
            fixture.Server.EchoTap = echo => outcome = echo;
            var changed = fixture.Server.Definition.Motion with { MoveSpeed = (fixture.Server.Definition.Motion.MoveSpeed + 1f) };

            fixture.Server.EnqueueMutation(mutation: new WorldMutation.SetMotion(Principal: WorldPrincipal.Console, Motion: changed));
            fixture.Step();

            message = outcome?.Message ?? string.Empty;

            return outcome is { Rejected: false };
        }

        Assert.True(condition: !Apply(marginDepth: 2f, message: out var reason), userMessage: reason);
        Assert.Contains(expectedSubstring: "changes a border-margin proof input", actualString: reason, comparisonType: StringComparison.Ordinal);
        Laws.RefusalWithControl(
            lawId: "margin-strip.tick-proof-input-change",
            deniedOutcome: () => Apply(marginDepth: 2f, message: out _),
            controlOutcome: () => Apply(marginDepth: null, message: out _));
    }

    // Two mapped facets on ONE local document, both naming destinations whose references share the SAME document
    // string — the resolver must be asked for that document exactly once, per the owner ruling that a quilt's several
    // borders naming one neighbour must not multiply into several fetches.
    [Fact]
    public void SameNeighbourDocument_ResolvedOnce_AcrossTwoPortalFacets() {
        const string placementA = "border-a";
        const string placementB = "border-b";
        var creationA = BuildDoorCreation() with { Id = placementA };
        var creationB = BuildDoorCreation() with { Id = placementB };
        var portalA = new WorldPlacementPortal(Destination: "dest-a", Arrival: WorldPortalArrival.Mapped, Counterpart: $"{placementA}/{DoorFace}", MarginDepth: 2f);
        var portalB = new WorldPlacementPortal(Destination: "dest-b", Arrival: WorldPortalArrival.Mapped, Counterpart: $"{placementB}/{DoorFace}", MarginDepth: 2f);
        var placementRowA = new WorldPlacement(Id: placementA, CreationId: creationA.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None(), Portal: portalA)]);
        var placementRowB = new WorldPlacement(Id: placementB, CreationId: creationB.Id, Position: new Vector3(x: 4f, y: 0f, z: 0f), YawDegrees: 0f, Scale: 1f, FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None(), Portal: portalB)]);
        var baseline = Fixtures.BuildDocument();
        var document = baseline with {
            Kits = [baseline.Kits[0] with { Collider = DoorCollider }],
            Creations = [creationA, creationB],
            Placements = [placementRowA, placementRowB],
            References = [new WorldReference(Name: WorldSafeName.Parse(candidate: ReferenceName), Document: ReferenceDocument)],
            Destinations = [
                new WorldDestination(Name: WorldSafeName.Parse(candidate: "dest-a"), Reference: ReferenceName, Durability: WorldDestinationDurability.Ephemeral),
                new WorldDestination(Name: WorldSafeName.Parse(candidate: "dest-b"), Reference: ReferenceName, Durability: WorldDestinationDurability.Ephemeral),
            ],
        };

        // The neighbour needs the TWO counterpart faces this local document's portals name, through a REAL face
        // catalog entry (WorldDefinitionValidator's dimension-parity check resolves both sides' frames): the SAME
        // door creations the local document uses, so HalfWidth/HalfHeight match by construction. This resolved
        // definition is never itself run through TryValidate, so nothing else about it needs to be valid.
        var neighbourPlacementA = new WorldPlacement(Id: placementA, CreationId: creationA.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None(), Portal: new WorldPlacementPortal(Destination: "back", Arrival: WorldPortalArrival.Mapped, Counterpart: $"{placementA}/{DoorFace}", MarginDepth: 2f))]);
        var neighbourPlacementB = new WorldPlacement(Id: placementB, CreationId: creationB.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None(), Portal: new WorldPlacementPortal(Destination: "back", Arrival: WorldPortalArrival.Mapped, Counterpart: $"{placementB}/{DoorFace}", MarginDepth: 2f))]);
        var neighbour = baseline with {
            Kits = [baseline.Kits[0] with { Collider = DoorCollider }],
            Creations = [creationA, creationB],
            Placements = [neighbourPlacementA, neighbourPlacementB],
        };
        var resolver = new StubNeighbourResolver(resolve: _ => WorldNeighbourResolution.Resolved(definition: neighbour));

        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: document, reason: out var reason, neighbours: resolver), userMessage: reason);
        Assert.Equal(expected: 1, actual: resolver.CallCount);
    }
}
