using System.Numerics;

using Xunit;

using Puck.Forge.Authoring;
using Puck.SdfVm;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: mapped arrival's AUTHORING and RESOLUTION halves — the two places a
/// <see cref="WorldPlacementPortal"/> facet's <see cref="WorldPlacementPortal.Arrival"/>/<see cref="WorldPlacementPortal.Counterpart"/>
/// pair can be refused, per docs/world-model.md's "Where portal work stands" section: <see cref="WorldDefinitionValidator"/> at BOOT (arrival/counterpart
/// coherence and counterpart SHAPE — <c>"&lt;placementId&gt;/&lt;face&gt;"</c>), and <see cref="WorldPortalCounterpart"/>
/// at TRANSFER TIME (the counterpart's cross-document EXISTENCE against the destination's own delivered
/// definition — deliberately NOT checked at boot, since the destination document is never resolved there). The
/// transfer-time abort itself (<c>Puck.World.WorldInstanceHost.ApplyTransfer</c>) is out of reach for this project
/// (the composition root) — this suite proves the resolver PRIMITIVE that abort reuses, mirroring
/// <see cref="WorldPortalArrivalMathLawTests"/>'s own "prove the primitive" shape; the abort itself is verified by
/// RUNNING <c>Puck.World</c> (CLAUDE.md rule 3).
/// </summary>
public sealed class PortalArrivalValidationLawTests {
    private const string DestinationName = "dest";
    private const string ReferenceName = "dest-ref";
    private const string DoorFace = "door";
    private const string DoorPlacementId = "door-placement";

    // A minimal creation declaring ONE face ("door") — the authored anchor ValidateFaceSources' faceNames set (and,
    // separately, WorldPortalCounterpart's own placement/face resolution) checks a portal-bearing placement against.
    // Mirrors Fixtures.BuildBallCreation's own canonicalize-at-build shape (a real creation, hash COMPILER-derived
    // through the SAME pipeline the validator re-verifies, never hand-pinned). The face names a BOX shape because a
    // portal facet needs a surface that maps onto a walkable aperture (WorldFaceApertureKind) — the aperture refusal
    // is its own law below, so every other law here must clear it to discriminate on what it is actually testing.
    private static WorldCreation BuildDoorCreation() => BuildDoorCreation(faceShapeType: AvatarPrimitive.Box, faceNamesShape: true);

    private static WorldCreation BuildDoorCreation(AvatarPrimitive faceShapeType, bool faceNamesShape) {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: faceShapeType,
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
            Behavior: new CreationBehaviorDocument(Locomotion: null, Faces: [new CreationFaceDocument(Name: DoorFace, ShapeId: (faceNamesShape ? 0 : null), DefaultSource: null)])
        );
        var canonical = CreationCanonicalizer.Canonicalize(document: document, source: "door");

        return new WorldCreation(Id: "door", Document: canonical.Document, Hash: canonical.Hash);
    }

    // A document declaring ONE valid destination/reference pair and ONE placement whose "door" face carries a
    // portal facet with the candidate arrival/counterpart — everything else is Fixtures.BuildDocument's own minimal
    // skeleton, so a refusal below can only ever be about the ONE authored fact under test.
    private static WorldDefinition BuildPortalDocument(WorldPortalArrival arrival, string? counterpart) {
        var creation = BuildDoorCreation();
        var portal = new WorldPlacementPortal(Destination: DestinationName, Travel: null, Arrival: arrival, Counterpart: counterpart);
        var placement = new WorldPlacement(
            Id: DoorPlacementId,
            CreationId: creation.Id,
            Position: Vector3.Zero,
            YawDegrees: 0f,
            Scale: 1f,
            FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None(), Portal: portal)]
        );

        return Fixtures.BuildDocument() with {
            Creations = [creation],
            Placements = [placement],
            References = [new WorldReference(Name: WorldSafeName.Parse(candidate: ReferenceName), Document: "worlds/dest.world.json")],
            Destinations = [new WorldDestination(Name: WorldSafeName.Parse(candidate: DestinationName), Reference: ReferenceName, Durability: WorldDestinationDurability.Ephemeral)],
        };
    }

    private static bool TryValidate(WorldPortalArrival arrival, string? counterpart) {
        var bytes = WorldDefinitionSerialization.Serialize(definition: BuildPortalDocument(arrival: arrival, counterpart: counterpart));

        try {
            _ = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);

            return true;
        } catch (InvalidDataException) {
            return false;
        }
    }

    [Fact]
    public void MappedArrival_WithoutCounterpart_RefusesByName() {
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: BuildPortalDocument(arrival: WorldPortalArrival.Mapped, counterpart: null))));

        Assert.Contains(expectedSubstring: "arrival 'mapped' requires", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MappedArrival_WithoutCounterpart_RefusesByName_ControlParsesClean() {
        Laws.RefusalWithControl(
            lawId: "portal-arrival.mapped-without-counterpart",
            deniedOutcome: static () => TryValidate(arrival: WorldPortalArrival.Mapped, counterpart: null),
            controlOutcome: static () => TryValidate(arrival: WorldPortalArrival.Mapped, counterpart: $"{DoorPlacementId}/{DoorFace}"));
    }

    [Fact]
    public void Counterpart_WithoutMappedArrival_RefusesByName() {
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: BuildPortalDocument(arrival: WorldPortalArrival.Spawn, counterpart: $"{DoorPlacementId}/{DoorFace}"))));

        Assert.Contains(expectedSubstring: "counterpart", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "arrival is not 'mapped'", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void Counterpart_WithoutMappedArrival_RefusesByName_ControlParsesClean() {
        Laws.RefusalWithControl(
            lawId: "portal-arrival.counterpart-without-mapped",
            deniedOutcome: static () => TryValidate(arrival: WorldPortalArrival.Spawn, counterpart: $"{DoorPlacementId}/{DoorFace}"),
            controlOutcome: static () => TryValidate(arrival: WorldPortalArrival.Spawn, counterpart: null));
    }

    // A creation with TWO declared faces, so a placement can legally override both — the substrate the
    // one-portal-face-per-placement wall below discriminates on. Second face name deliberately ordinary.
    private static WorldDefinition BuildTwoFaceDocument(bool secondFaceCarriesPortal) {
        var creation = BuildDoorCreation();
        var behavior = creation.Document.Behavior! with {
            Faces = [.. creation.Document.Behavior!.Faces!, new CreationFaceDocument(Name: "back", ShapeId: null, DefaultSource: null)],
        };
        var canonical = CreationCanonicalizer.Canonicalize(document: (creation.Document with { Behavior = behavior }), source: "door");
        var twoFaced = new WorldCreation(Id: creation.Id, Document: canonical.Document, Hash: canonical.Hash);
        var portal = new WorldPlacementPortal(Destination: DestinationName, Travel: null, Arrival: WorldPortalArrival.Spawn, Counterpart: null);
        var placement = new WorldPlacement(
            Id: DoorPlacementId,
            CreationId: twoFaced.Id,
            Position: Vector3.Zero,
            YawDegrees: 0f,
            Scale: 1f,
            FaceSources: [
                new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None(), Portal: portal),
                new WorldPlacementFace(Face: "back", Source: new WorldScreenSource.None(), Portal: (secondFaceCarriesPortal ? portal : null)),
            ]
        );

        return Fixtures.BuildDocument() with {
            Creations = [twoFaced],
            Placements = [placement],
            References = [new WorldReference(Name: WorldSafeName.Parse(candidate: ReferenceName), Document: "worlds/dest.world.json")],
            Destinations = [new WorldDestination(Name: WorldSafeName.Parse(candidate: DestinationName), Reference: ReferenceName, Durability: WorldDestinationDurability.Ephemeral)],
        };
    }

    [Fact]
    public void SecondPortalFaceOnOnePlacement_RefusesByName() {
        // The wall: a party straddling two portal faces of ONE placement would split into two transfers (the
        // coalesced group key carries placement+face), breaking whole-party atomicity — refused until per-member
        // source frames land. The refusal names the placement and the face already carrying the portal.
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: BuildTwoFaceDocument(secondFaceCarriesPortal: true), reason: out var reason, neighbours: null), userMessage: "a second portal face was expected to refuse");
        Assert.Contains(expectedSubstring: "second portal face", actualString: reason, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: DoorPlacementId, actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void SecondPortalFaceOnOnePlacement_ControlWithOnePortalFaceValidates() {
        Assert.True(condition: WorldDefinitionValidator.TryValidate(definition: BuildTwoFaceDocument(secondFaceCarriesPortal: false), reason: out var reason, neighbours: null), userMessage: reason);
    }

    [Fact]
    public void MalformedCounterpart_RefusesNamingWhatWasWritten() {
        const string malformed = "not-a-placement-slash-face";
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: BuildPortalDocument(arrival: WorldPortalArrival.Mapped, counterpart: malformed))));

        // "Refuses by name" — the malformed string itself must appear in the refusal, or an author is left diffing
        // against a schema to find out what they typed wrong.
        Assert.Contains(expectedSubstring: malformed, actualString: exception.Message, comparisonType: StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "malformed", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedCounterpart_RefusesByName_ControlParsesClean() {
        Laws.RefusalWithControl(
            lawId: "portal-arrival.malformed-counterpart",
            deniedOutcome: static () => TryValidate(arrival: WorldPortalArrival.Mapped, counterpart: "not-a-placement-slash-face"),
            controlOutcome: static () => TryValidate(arrival: WorldPortalArrival.Mapped, counterpart: $"{DoorPlacementId}/{DoorFace}"));
    }

    [Fact]
    public void WellFormedMappedPortal_ValidatesClean() {
        // The control every refusal law above leans on, proven directly: a mapped arrival with a well-formed
        // counterpart, on an otherwise-minimal document, boots — cross-document existence is NOT checked here (the
        // destination document is never resolved at boot), only the SHAPE and the arrival/counterpart pairing.
        _ = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: BuildPortalDocument(arrival: WorldPortalArrival.Mapped, counterpart: $"{DoorPlacementId}/{DoorFace}")));
    }

    // ---- WorldPortalCounterpart: the TRANSFER-TIME resolution primitive ApplyTransfer's own abort reuses ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-slash-at-all")]
    [InlineData("/leading-slash-empty-placement")]
    [InlineData("trailing-slash-empty-face/")]
    public void TryParse_MalformedShapes_Refuse(string? counterpart) {
        Assert.False(condition: WorldPortalCounterpart.TryParse(counterpart: counterpart, placementId: out _, face: out _));
    }

    [Fact]
    public void TryParse_WellFormed_SplitsAtFirstSlash() {
        Assert.True(condition: WorldPortalCounterpart.TryParse(counterpart: "placement-a/face/with/slashes", placementId: out var placementId, face: out var face));
        Assert.Equal(expected: "placement-a", actual: placementId);
        Assert.Equal(expected: "face/with/slashes", actual: face);
    }

    [Fact]
    public void TryResolve_MissingPlacement_RefusesNamingIt() {
        var definition = Fixtures.BuildDocument() with {
            Placements = [new WorldPlacement(Id: "real-placement", CreationId: "real-placement", Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None())])],
        };

        var resolved = WorldPortalCounterpart.TryResolve(definition: definition, counterpart: $"no-such-placement/{DoorFace}", placement: out var placement, face: out var face, reason: out var reason);

        Assert.False(condition: resolved);
        Assert.Null(@object: placement);
        Assert.Null(@object: face);
        Assert.Contains(expectedSubstring: "no-such-placement", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_MissingFace_RefusesNamingIt() {
        var definition = Fixtures.BuildDocument() with {
            Placements = [new WorldPlacement(Id: DoorPlacementId, CreationId: DoorPlacementId, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None())])],
        };

        var resolved = WorldPortalCounterpart.TryResolve(definition: definition, counterpart: $"{DoorPlacementId}/no-such-face", placement: out var placement, face: out var face, reason: out var reason);

        Assert.False(condition: resolved);
        Assert.Null(@object: placement);
        Assert.Null(@object: face);
        Assert.Contains(expectedSubstring: "no-such-face", actualString: reason, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_RealPlacementAndFace_Succeeds_ControlForTheTwoRefusalsAbove() {
        var definition = Fixtures.BuildDocument() with {
            Placements = [new WorldPlacement(Id: DoorPlacementId, CreationId: DoorPlacementId, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f, FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None())])],
        };

        var resolved = WorldPortalCounterpart.TryResolve(definition: definition, counterpart: $"{DoorPlacementId}/{DoorFace}", placement: out var placement, face: out var face, reason: out var reason);

        Assert.True(condition: resolved);
        Assert.NotNull(@object: placement);
        Assert.Equal(expected: DoorPlacementId, actual: placement!.Id);
        Assert.NotNull(@object: face);
        Assert.Equal(expected: DoorFace, actual: face!.Face);
        Assert.Equal(expected: string.Empty, actual: reason);
    }
}
