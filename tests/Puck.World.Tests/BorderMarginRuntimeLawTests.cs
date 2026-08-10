using System.Numerics;

using Xunit;

using Puck.Forge.Authoring;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the border-margin strip's RUNTIME half — <see cref="WorldBorderMarginContactField"/> and its
/// supporting <see cref="WorldBorderMarginBand"/>/<see cref="WorldBorderMarginBands"/> — the collision consumer of
/// the wave-1 margin strip that <see cref="MarginStripValidationLawTests"/> proves the SHAPE of at document-load
/// time. This suite proves the CONTENT: a body standing inside a mapped portal facet's authored margin, with NO
/// ground of its own on this side, gets ground from an injected neighbour's compiled <see cref="WorldSolidField"/> —
/// paired, per this repository's red-first discipline, against the identical configuration with no border-margin
/// source injected at all (the world exactly as it behaves today).
/// </summary>
public sealed class BorderMarginRuntimeLawTests {
    private const string DestinationName = "dest";
    private const string ReferenceName = "dest-ref";
    private const string ReferenceDocument = "neighbour.world.json";
    private const string DoorFace = "door";
    private const string DoorPlacementId = "door-placement";
    private const int SeatSlot = 0;
    // Generous: the door's own creation shape is a unit Box, so this placement scale is also the aperture's
    // half-extent in world units along every axis — wide enough that the body's whole fall (Y) and its fixed X/Z
    // offset from the door's own origin both stay inside the margin band the whole settle.
    private const float DoorScale = 5f;
    private const float MarginDepthValue = 4f;
    private static readonly Vector3 DoorPosition = new(x: 0f, y: 1f, z: 0f);
    private static readonly Vector3 SpawnPosition = new(x: 0f, y: 2f, z: 1f);
    private const int SettleTicks = 240;

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

    // The SOURCE side: a mapped portal facet authoring MarginDepthValue, and NO solid geometry of its own at all
    // (empty collision.requirements, no ground placement) — a body here can ONLY ground through the injected
    // border-margin source, never this document's own field, so a grounded verdict is unambiguous evidence the
    // neighbour lookup fired.
    private static WorldDefinition BuildSourceDocument() {
        var creation = BuildDoorCreation();
        var portal = new WorldPlacementPortal(Destination: DestinationName, Arrival: WorldPortalArrival.Mapped, Counterpart: $"{DoorPlacementId}/{DoorFace}", MarginDepth: MarginDepthValue);
        var placement = new WorldPlacement(
            Id: DoorPlacementId,
            CreationId: creation.Id,
            Position: DoorPosition,
            YawDegrees: 0f,
            Scale: DoorScale,
            FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None(), Portal: portal)]
        );
        var spawnPoints = new[] { new WorldSpawnPoint(Id: "seat-1", Position: SpawnPosition) };
        var baseline = Fixtures.BuildDocument();

        return baseline with {
            SpawnPoints = spawnPoints,
            Population = (baseline.Population with { SeatSpawns = ["seat-1", "seat-1", "seat-1", "seat-1"] }),
            Kits = [baseline.Kits[0] with { Collider = new WorldCollider.Capsule(Endpoint: new Vector3(x: 0f, y: 1f, z: 0f), Radius: 0.35f) }],
            Creations = [creation],
            Placements = [placement],
            References = [new WorldReference(Name: WorldSafeName.Parse(candidate: ReferenceName), Document: ReferenceDocument)],
            Destinations = [new WorldDestination(Name: WorldSafeName.Parse(candidate: DestinationName), Reference: ReferenceName, Durability: WorldDestinationDurability.Ephemeral)],
        };
    }

    private sealed class StubNeighbourResolver(WorldDefinition definition) : IWorldNeighbourResolver {
        public WorldNeighbourResolution Resolve(string document) => WorldNeighbourResolution.Resolved(definition: definition);
    }

    // A minimal IWorldBorderMarginNeighbour test double: a real WorldSolidField compiled over a definition that
    // author a solid ground box, and the door's own frame in that (identically-numbered, in this fixture) local
    // coordinate space — so the isometry under test is the identity case (matching source/destination frame and
    // yaw), the SAME configuration the quilt's own sw/nw pairing resolves to (see the campaign's own recon notes).
    private sealed class StubNeighbour(WorldFaceFrame frame, WorldSolidField field) : IWorldBorderMarginNeighbour {
        public WorldDefinition Definition => Fixtures.BuildDocument();
        public int DefinitionRevision => 0;
        public WorldFaceFrame CounterpartFrame => frame;
        public bool TryGetSolidField(out WorldSolidField? solidField, out string reason) {
            solidField = field;
            reason = string.Empty;

            return true;
        }
    }

    private sealed class StubBorderMarginSource(string placementId, string faceName, IWorldBorderMarginNeighbour neighbour) : IWorldBorderMarginSource {
        public bool TryResolve(string queryPlacementId, string queryFaceName, out IWorldBorderMarginNeighbour? resolved) {
            if (string.Equals(a: queryPlacementId, b: placementId, comparisonType: StringComparison.Ordinal) &&
                string.Equals(a: queryFaceName, b: faceName, comparisonType: StringComparison.Ordinal)) {
                resolved = neighbour;

                return true;
            }

            resolved = null;

            return false;
        }
    }

    // Compiles the neighbour's own ground: a big flat solid Box whose top surface sits at world Y 0, positioned so
    // the body's straight-down fall from SpawnPosition (Y 2) lands on it — IDENTICAL local coordinates to the
    // source side, matching the fixture's own identity-isometry framing.
    private static WorldSolidField BuildNeighbourGround() {
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
        var document = new CreationDocument(Schema: CreationDocument.CurrentSchema, Name: "ground", Intent: CreatorIntent.Object, BakeStyle: null, Palette: null, Shapes: [shape], Frames: null);
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "ground");
        var creation = new WorldCreation(Id: "ground", Document: canonical.Document, Hash: canonical.Hash);
        var placement = new WorldPlacement(Id: "ground", CreationId: creation.Id, Position: new Vector3(x: 0f, y: -10f, z: 0f), YawDegrees: 0f, Scale: 20f, Solid: new WorldSolid(Margin: 0f));
        var neighbourDefinition = Fixtures.BuildDocument() with {
            Creations = [creation],
            Placements = [placement],
        };

        Assert.True(condition: WorldSolidField.TryBuild(definition: neighbourDefinition, built: out var field, reason: out var reason), userMessage: reason);

        return field!;
    }

    private static WorldFaceFrame DoorFrame() {
        var definition = BuildSourceDocument();
        var catalog = WorldFaceCatalog.For(definition: definition);

        Assert.True(condition: catalog.TryFind(placementId: DoorPlacementId, faceName: DoorFace, out var row));

        return row.Frame;
    }

    [Fact]
    public void MarginGeometryBudget_SelectsOneDeterministicSubsetForCollisionAndRendering() {
        var baseline = BuildSourceDocument();
        var solids = Enumerable.Range(start: 0, count: (WorldBorderMarginGeometry.MaximumPlacementsPerBand + 2))
            .Select(selector: index => new WorldPlacement(
                Id: $"solid-{index}",
                CreationId: baseline.Creations[0].Id,
                Position: DoorPosition,
                YawDegrees: 0f,
                Scale: 1f,
                Solid: new WorldSolid(Margin: 0f)))
            .ToArray();
        var neighbour = baseline with { Placements = solids };

        var selected = WorldBorderMarginGeometry.Select(
            definition: neighbour,
            frame: DoorFrame(),
            marginDepth: FixedQ4816.FromDouble(value: MarginDepthValue));

        Assert.True(condition: selected.Truncated);
        Assert.Equal(expected: WorldBorderMarginGeometry.MaximumPlacementsPerBand, actual: selected.Placements.Count);
        Assert.Equal(expected: Enumerable.Range(start: 0, count: WorldBorderMarginGeometry.MaximumPlacementsPerBand).Select(selector: index => $"solid-{index}"), actual: selected.Placements.Select(selector: placement => placement.Id));
    }

    // Falls the seated body under gravity alone (no channel presses — mirrors GradientUpContactLawTests' own
    // shape) and reports whether it settled grounded.
    private static bool Settle(IWorldBorderMarginSource? borderMargin) {
        var definition = BuildSourceDocument();
        var neighbours = new StubNeighbourResolver(definition: definition);

        WorldDefinitionValidator.Validate(definition: definition, neighbours: neighbours);

        var population = new WorldPopulation(definition: definition);
        var machines = new WorldMachineHost(screens: definition.Screens, engines: []);
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-world-tests-border-margin-").FullName;
        var profiles = new WorldOwnedWorlds(template: definition, directory: stateDirectory, machineId: Guid.NewGuid());
        var server = new WorldServer(definition: definition, population: population, profiles: profiles, envelope: new WorldRenderEnvelope(), machines: machines);

        using var fixture = new WorldFixture(server: server, machines: machines, stateDirectory: stateDirectory);
        var actor = WorldPrincipal.Seat(slot: SeatSlot);

        _ = server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey));

        // Match the production boot order: the composition root installs the runtime margin source only after the
        // boot seat is already live. This must update that body's retained contact-field reference; injecting before
        // Join would exercise only the later activation path and stay green if production wiring were dead again.
        server.BorderMargin = borderMargin;

        for (var tick = 0; (tick < SettleTicks); tick++) {
            fixture.Step();
        }

        return server.Body(index: actor.Index)!.Grounded;
    }

    [Fact]
    public void BodyInMargin_NoBorderSource_NeverGrounds_InjectedNeighbourGrounds() {
        var neighbour = new StubNeighbour(frame: DoorFrame(), field: BuildNeighbourGround());
        var source = new StubBorderMarginSource(placementId: DoorPlacementId, faceName: DoorFace, neighbour: neighbour);

        Laws.RefusalWithControl(
            lawId: "border-margin.neighbour-ground-grounds-the-body",
            deniedOutcome: static () => Settle(borderMargin: null),
            controlOutcome: () => Settle(borderMargin: source));
    }

    [Fact]
    public void OwnedIdentitySeed_DoesNotInheritTheVisitedWorldsBorderClaims() {
        var stateDirectory = Directory.CreateTempSubdirectory(prefix: "puck-world-tests-owned-margin-").FullName;

        try {
            var profiles = new WorldOwnedWorlds(template: BuildSourceDocument(), directory: stateDirectory, machineId: Guid.NewGuid());

            Assert.NotEmpty(collection: profiles.All);

            foreach (var identity in profiles.All) {
                Assert.NotNull(@object: identity.Document);

                foreach (var placement in identity.Document!.Placements) {
                    foreach (var face in (placement.FaceSources ?? [])) {
                        Assert.Null(@object: face.Portal?.MarginDepth);
                    }
                }
            }
        } finally {
            Directory.Delete(path: stateDirectory, recursive: true);
        }
    }

    [Fact]
    public void Band_Contains_MatchesTheGeometricExpectation() {
        var frame = DoorFrame();
        var band = new WorldBorderMarginBand(PlacementId: DoorPlacementId, FaceName: DoorFace, Frame: frame, Depth: FixedQ4816.FromDouble(value: MarginDepthValue));

        // On the source/front side of the face, well inside the aperture — the point this whole strip covers.
        Assert.True(condition: band.Contains(position: FixedVector3.FromVector3(value: SpawnPosition)));

        // Past the face plane (already on the far/crossed side) — never this source-side band.
        Assert.False(condition: band.Contains(position: FixedVector3.FromVector3(value: new Vector3(x: 0f, y: 1f, z: -1f))));

        // Deeper than the authored depth — outside the strip.
        Assert.False(condition: band.Contains(position: FixedVector3.FromVector3(value: new Vector3(x: 0f, y: 1f, z: (MarginDepthValue + 1f)))));
    }

    [Fact]
    public void CollectFrom_FindsTheAuthoredBand_AndNothingWhenUnauthored() {
        var withMargin = WorldBorderMarginBands.CollectFrom(definition: BuildSourceDocument());

        Assert.Single(collection: withMargin);
        Assert.Equal(expected: DoorPlacementId, actual: withMargin[0].PlacementId);
        Assert.Equal(expected: DoorFace, actual: withMargin[0].FaceName);

        var withoutMargin = WorldBorderMarginBands.CollectFrom(definition: Fixtures.BuildDocument());

        Assert.Empty(collection: withoutMargin);
    }
}
