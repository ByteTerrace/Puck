using System.Numerics;

using Xunit;

using Puck.World.Authoring;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the one per-revision face derivation (<see cref="WorldFaceCatalog"/>) the portal trigger,
/// the arrival isometry, and rendering all read; the trigger policy over it
/// (<see cref="WorldFacePortalPolicy"/>); and the refusals a door owes at validation. The derivation is SIMULATION
/// state — a crossing decision is taken from it — so these laws also pin that it stays in the deterministic
/// fixed-point domain.
/// </summary>
public sealed class WorldFaceCatalogLawTests {
    private const string DestinationName = "dest";
    private const string DoorFace = "screen";
    private const string DoorPlacementId = "door";
    private const string ReferenceName = "dest-ref";

    // The face's own shape sits OFF the placement root and is scaled anisotropically, so a derivation that read the
    // placement root (or a canonical size table) instead of the named shape is visibly wrong rather than
    // coincidentally right.
    private static Vector3 ShapeOffset { get; } = new(x: 0.75f, y: 0.25f, z: -0.5f);
    private static Vector3 ShapeScale { get; } = new(x: 3f, y: 2f, z: 0.15f);
    // A rotation about the face's own RIGHT axis — pitch, the axis a world-up-pinned frame cannot represent.
    private static Quaternion Pitched { get; } = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: (MathF.PI / 6f));

    private static WorldPrototype BuildDoorCreation(SdfSolidPrimitive faceShape = SdfSolidPrimitive.Box, bool faceNamesShape = true, bool animated = false, bool pitched = false) {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: faceShape,
            Position: ShapeOffset,
            Rotation: (pitched ? Pitched : Quaternion.Identity),
            Scale: ShapeScale,
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: "door",
            Palette: null,
            Shapes: [shape],
            Frames: (animated ? [new FrameDocument(Name: "idle", Transforms: [new FrameTransformDocument(Id: 0, Position: ShapeOffset, Rotation: Quaternion.Identity, Scale: ShapeScale)])] : null),
            Behavior: new CreationBehaviorDocument(Locomotion: null, Faces: [new CreationFaceDocument(DefaultSource: null, Name: DoorFace, ShapeId: (faceNamesShape ? 0 : null))])
        );
        // The literals above describe the geometry this suite wants in ENGINE terms; converting them to the author
        // frame here (the inverse CreationFrame applies at the engine door) keeps every assertion below unchanged.
        var canonical = CreationCanonicalizer.Canonicalize(document: CreationFrame.ToAuthor(document: document), source: "door");

        return new WorldPrototype(Id: "door", Document: canonical.Document, HashRaw: canonical.Hash);
    }
    private static WorldDefinition BuildDoorDocument(
        float yawDegrees = 0f,
        float scale = 1f,
        SdfSolidPrimitive faceShape = SdfSolidPrimitive.Box,
        bool faceNamesShape = true,
        bool animated = false,
        bool carriesPortal = true,
        bool pitched = false,
        WorldPlacementAttach? attach = null,
        WorldScreenSource? source = null
    ) {
        var creation = BuildDoorCreation(animated: animated, faceNamesShape: faceNamesShape, faceShape: faceShape, pitched: pitched);
        var portal = new WorldPlacementPortal(Destination: DestinationName, Travel: null, Arrival: WorldPortalArrival.Spawn, Counterpart: null);
        var placement = new WorldPlacement(
            Id: DoorPlacementId,
            PrototypeId: creation.Id,
            Position: new Vector3(x: 4f, y: 1.5f, z: -7f),
            YawDegrees: yawDegrees,
            Scale: scale,
            FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: (source ?? new WorldScreenSource.None()), Portal: (carriesPortal ? portal : null))],
            Attach: attach
        );

        return Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            PlacementRowsRaw = [placement],
            References = [new WorldReference(Name: WorldSafeName.Parse(candidate: ReferenceName), Document: "worlds/dest.world.json")],
            Destinations = [new WorldDestination(Name: WorldSafeName.Parse(candidate: DestinationName), Reference: ReferenceName, Durability: WorldDestinationDurability.Ephemeral)],
        };
    }
    private static WorldFaceRow DoorRow(WorldDefinition definition) {
        Assert.True(condition: WorldFaceCatalog.For(definition: definition).TryFind(placementId: DoorPlacementId, faceName: DoorFace, out var row));

        return row;
    }
    private static bool Validates(WorldDefinition definition) => WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out _);

    // ---- The frame ----

    [Fact]
    public void TheFrameSitsOnTheNamedShape_NotOnThePlacementRoot() {
        var row = DoorRow(definition: BuildDoorDocument());
        var root = FixedVector3.FromVector3(value: new Vector3(x: 4f, y: 1.5f, z: -7f));

        Assert.NotEqual(expected: root, actual: row.Frame.Origin);
        Assert.Equal(expected: (root + FixedVector3.FromVector3(value: ShapeOffset)), actual: row.Frame.Origin);
    }
    [Fact]
    public void TheFrameIsSizedFromTheAuthoredScale_PlacementScaleIncluded() {
        var single = DoorRow(definition: BuildDoorDocument());
        var doubled = DoorRow(definition: BuildDoorDocument(scale: 2f));

        Assert.Equal(expected: FixedQ4816.FromDouble(value: ShapeScale.X), actual: single.Frame.HalfWidth);
        Assert.Equal(expected: FixedQ4816.FromDouble(value: ShapeScale.Y), actual: single.Frame.HalfHeight);
        Assert.Equal(expected: FixedQ4816.FromDouble(value: ShapeScale.Z), actual: single.Frame.HalfDepth);
        Assert.Equal(expected: FixedQ4816.FromDouble(value: (ShapeScale.X * 2.0)), actual: doubled.Frame.HalfWidth);
        Assert.Equal(expected: FixedQ4816.FromDouble(value: (ShapeScale.Y * 2.0)), actual: doubled.Frame.HalfHeight);
    }
    [Fact]
    public void RotatingThePlacementMovesTheWholeFrame() {
        // THE sensitivity instrument: an instrument that cannot detect the rotation proves nothing about the ones
        // that consume it. Origin, basis, and normal must all move — a frame that only rotated its basis would leave
        // an off-centre door's origin behind.
        var level = DoorRow(definition: BuildDoorDocument()).Frame;
        var turned = DoorRow(definition: BuildDoorDocument(yawDegrees: 30f)).Frame;

        Assert.NotEqual(expected: level.Origin, actual: turned.Origin);
        Assert.NotEqual(expected: level.Normal, actual: turned.Normal);
        Assert.NotEqual(expected: level.Right, actual: turned.Right);
        Assert.Equal(expected: level.Up, actual: turned.Up);
        Assert.Equal(expected: level.HalfWidth, actual: turned.HalfWidth);

        // A yaw of 30 degrees turns the frame's heading by 30 degrees, to within fixed-point rounding of the
        // conversion and the SinCos it feeds.
        var turn = ((double)(turned.PlanarYawRadians - level.PlanarYawRadians));

        Assert.True(condition: (Math.Abs(value: (turn - (Math.PI / 6.0))) < 0.001), userMessage: $"the frame heading turned by {turn} radians, not pi/6");
    }
    [Fact]
    public void TheTriadIsOrthonormal_SoTheWalkedSlabAndTheDrawnScreenAreOnePlane() {
        // Client.WorldScreenStamper rebuilds the drawn slab's normal as Cross(Right, Up); the trigger uses the
        // frame's own Normal. If Up were pinned to world +Y while Right and Normal came from the shape's rotation,
        // a pitched face would put those two planes at the pitch angle apart, with nothing to notice it. A pitched
        // face is the discriminating case: on a level one the two agree even under the defect.
        foreach (var pitched in new[] { false, true }) {
            var frame = DoorRow(definition: BuildDoorDocument(yawDegrees: 25f, pitched: pitched)).Frame;
            var rendered = FixedVector3.Cross(left: frame.Right, right: frame.Up).Normalize();
            var tolerance = FixedQ4816.FromDouble(value: 0.002);

            Assert.True(condition: (FixedQ4816.Abs(value: (rendered.X - frame.Normal.X)) <= tolerance), userMessage: $"pitched={pitched}: rendered normal x {((double)rendered.X)} != frame normal x {((double)frame.Normal.X)}");
            Assert.True(condition: (FixedQ4816.Abs(value: (rendered.Y - frame.Normal.Y)) <= tolerance), userMessage: $"pitched={pitched}: rendered normal y {((double)rendered.Y)} != frame normal y {((double)frame.Normal.Y)}");
            Assert.True(condition: (FixedQ4816.Abs(value: (rendered.Z - frame.Normal.Z)) <= tolerance), userMessage: $"pitched={pitched}: rendered normal z {((double)rendered.Z)} != frame normal z {((double)frame.Normal.Z)}");
            Assert.True(condition: (FixedQ4816.Abs(value: FixedVector3.Dot(left: frame.Right, right: frame.Up)) <= tolerance), userMessage: $"pitched={pitched}: right and up are not perpendicular");
        }
    }
    [Fact]
    public void APitchedFaceIsNotYawOnly_AndALevelOneIs() {
        // The instrument the refusal below leans on: if this could not tell the two apart, the refusal would be
        // either unreachable or unconditional.
        Assert.True(condition: DoorRow(definition: BuildDoorDocument(yawDegrees: 25f, carriesPortal: false)).Frame.IsYawOnly);
        Assert.False(condition: DoorRow(definition: BuildDoorDocument(yawDegrees: 25f, carriesPortal: false, pitched: true)).Frame.IsYawOnly);
    }
    [Fact]
    public void APortalOnAPitchedFace_RefusesByName() {
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: BuildDoorDocument(pitched: true), reason: out var reason, neighbours: null));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "pitch or roll");
    }
    [Fact]
    public void APortalOnAPitchedFace_RefusesByName_YawOnlyControlValidates() {
        Laws.RefusalWithControl(
            lawId: "face-frame.portal-on-pitched-face",
            deniedOutcome: static () => Validates(definition: BuildDoorDocument(yawDegrees: 25f, pitched: true)),
            controlOutcome: static () => Validates(definition: BuildDoorDocument(yawDegrees: 25f)));
    }
    [Fact]
    public void APitchedFaceWithoutAPortalStillDraws() {
        Assert.True(condition: Validates(definition: BuildDoorDocument(pitched: true, carriesPortal: false)));
    }
    [Fact]
    public void TheFrameIsBitIdenticalAcrossTwoIndependentDerivations() {
        // Same document CONTENT, two distinct instances, so the per-revision cache cannot answer the second from the
        // first. Every frame constant is fixed point by construction; nothing here rounds differently per run.
        var first = DoorRow(definition: BuildDoorDocument(yawDegrees: 37.5f));
        var second = DoorRow(definition: BuildDoorDocument(yawDegrees: 37.5f));

        Assert.Equal(expected: first.Frame, actual: second.Frame);
    }
    [Fact]
    public void TheDerivationIsReusedForOneDefinitionInstance() {
        var definition = BuildDoorDocument();

        Assert.Same(expected: WorldFaceCatalog.For(definition: definition), actual: WorldFaceCatalog.For(definition: definition));
    }
    // ---- The trigger policy ----

    [Fact]
    public void TheBandIsNeverThinnerThanOneStepOfDeclaredTravel() {
        var definition = BuildDoorDocument();
        var row = DoorRow(definition: definition);
        var floor = WorldFacePortalPolicy.CrossingFloor(definition: definition);

        Assert.True(condition: WorldFacePortalPolicy.TryAperture(aperture: out var aperture, crossingFloor: floor, row: in row));

        var band = Assert.IsType<WorldFaceAperture.Box>(@object: aperture);

        Assert.True(condition: (band.Depth >= floor), userMessage: "the band is thinner than the derived crossing floor");
        Assert.True(condition: (band.Depth >= row.Frame.HalfDepth), userMessage: "the band is thinner than the door itself");
        Assert.True(condition: (floor > FixedQ4816.Zero), userMessage: "a stepping world must derive a positive crossing floor");
    }
    [Fact]
    public void TheSpeedCeilingRisesWithWhatTheDocumentDeclares() {
        var definition = BuildDoorDocument();
        var ceiling = WorldFacePortalPolicy.SpeedCeiling(definition: definition);
        // Clear of every other term the ceiling maximizes over (this fixture's terminal fall speed dominates its
        // walk speed), so the raise is what moves the answer rather than being masked by a larger sibling.
        var faster = (definition with { MotionRaw = (definition.Motion with { MoveSpeed = (((float)((double)ceiling)) * 4f) }) });

        Assert.True(condition: (WorldFacePortalPolicy.SpeedCeiling(definition: faster) > ceiling));
        Assert.True(condition: (WorldFacePortalPolicy.CrossingFloor(definition: faster) > WorldFacePortalPolicy.CrossingFloor(definition: definition)));
    }
    [Fact]
    public void ARestingWorldDerivesNoCrossingFloor() {
        // Rate zero is a legal, distinct rate: nothing advances, so no step of travel exists to floor a band with.
        var definition = (BuildDoorDocument() with { Simulation = new WorldSimulationDefaults(RateHz: 0) });

        Assert.Equal(expected: FixedQ4816.Zero, actual: WorldFacePortalPolicy.CrossingFloor(definition: definition));
    }
    // ---- Refusals ----

    [Fact]
    public void APortalOnAnAttachedPlacement_RefusesByName() {
        var attached = BuildDoorDocument(attach: new WorldPlacementAttach(BodyIndex: 0, LocalOffset: Vector3.Zero));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: attached, neighbours: null, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "ATTACHES");
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "portal");
    }
    [Fact]
    public void APortalOnAnAttachedPlacement_RefusesByName_ControlValidates() {
        Laws.RefusalWithControl(
            lawId: "face-frame.portal-on-attached",
            deniedOutcome: static () => Validates(definition: BuildDoorDocument(attach: new WorldPlacementAttach(BodyIndex: 0, LocalOffset: Vector3.Zero))),
            controlOutcome: static () => Validates(definition: BuildDoorDocument()));
    }
    [Fact]
    public void AnAttachedPlacementWithoutAPortal_IsStillAllowed() {
        // The refusal is about the DOOR, not about attaching: a face may still ride a body and show a feed.
        Assert.True(condition: Validates(definition: BuildDoorDocument(carriesPortal: false, attach: new WorldPlacementAttach(BodyIndex: 0, LocalOffset: Vector3.Zero))));
    }
    [Fact]
    public void APortalOnAnAnimatedPlacement_RefusesByName() {
        var animated = BuildDoorDocument(animated: true);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: animated, neighbours: null, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "ANIMATED");
    }
    [Fact]
    public void APortalOnAnAnimatedPlacement_RefusesByName_ControlValidates() {
        Laws.RefusalWithControl(
            lawId: "face-frame.portal-on-animated",
            deniedOutcome: static () => Validates(definition: BuildDoorDocument(animated: true)),
            controlOutcome: static () => Validates(definition: BuildDoorDocument()));
    }
    [Fact]
    public void APortalOnAShapelessFace_RefusesByName() {
        var shapeless = BuildDoorDocument(faceNamesShape: false);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: shapeless, neighbours: null, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "aperture");
    }
    [Fact]
    public void APortalOnAShapelessFace_RefusesByName_ControlValidates() {
        Laws.RefusalWithControl(
            lawId: "face-frame.portal-without-a-shape",
            deniedOutcome: static () => Validates(definition: BuildDoorDocument(faceNamesShape: false)),
            controlOutcome: static () => Validates(definition: BuildDoorDocument()));
    }
    [InlineData(SdfSolidPrimitive.Sphere)]
    [InlineData(SdfSolidPrimitive.Cylinder)]
    [InlineData(SdfSolidPrimitive.Plane)]
    [Theory]
    public void APortalOnAShapeKindWithNoApertureMapping_RefusesByName(SdfSolidPrimitive faceShape) {
        var unmapped = BuildDoorDocument(faceShape: faceShape);

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: unmapped, neighbours: null, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "aperture");
        Assert.Contains(expectedSubstring: faceShape.ToString(), actualString: reason, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void APortalOnAShapeKindWithNoApertureMapping_RefusesByName_ControlValidates() {
        Laws.RefusalWithControl(
            lawId: "face-frame.portal-on-unmapped-shape",
            deniedOutcome: static () => Validates(definition: BuildDoorDocument(faceShape: SdfSolidPrimitive.Sphere)),
            controlOutcome: static () => Validates(definition: BuildDoorDocument(faceShape: SdfSolidPrimitive.Box)));
    }
    [Fact]
    public void AFaceWithNoApertureStillDrawsWithoutAPortal() {
        // Geometry is never taken away by a refusal: the shape kinds that cannot be walked through still derive a
        // frame and still show a feed.
        Assert.True(condition: Validates(definition: BuildDoorDocument(faceShape: SdfSolidPrimitive.Sphere, carriesPortal: false)));
        Assert.Null(@object: DoorRow(definition: BuildDoorDocument(faceShape: SdfSolidPrimitive.Sphere, carriesPortal: false)).Aperture);
    }

    // ---- The slot budget ----

    private static WorldDefinition BuildBudgetDocument(int liveFaces, int reservedSlots) {
        var creation = BuildDoorCreation();
        var placements = new WorldPlacement[liveFaces];

        for (var index = 0; (index < liveFaces); index++) {
            placements[index] = new WorldPlacement(
                Id: $"{DoorPlacementId}-{index}",
                PrototypeId: creation.Id,
                Position: new Vector3(x: (index * 8f), y: 1.5f, z: -7f),
                YawDegrees: 0f,
                Scale: 1f,
                FaceSources: [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.TestPattern(Height: 64, Width: 64))]
            );
        }

        return Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            PlacementRowsRaw = placements,
            AuthoringRaw = (Fixtures.StandardAuthoring with { DerivedFaceScreens = reservedSlots }),
        };
    }

    [Fact]
    public void MoreLiveFacesThanReservedSlots_RefusesByName() {
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: BuildBudgetDocument(liveFaces: 3, reservedSlots: 2), reason: out var reason, neighbours: null));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "derivedFaceScreens");
    }
    [Fact]
    public void MoreLiveFacesThanReservedSlots_RefusesByName_AtBudgetControlBoots() {
        Laws.RefusalWithControl(
            lawId: "face-frame.static-slot-budget",
            deniedOutcome: static () => Validates(definition: BuildBudgetDocument(liveFaces: 3, reservedSlots: 2)),
            controlOutcome: static () => Validates(definition: BuildBudgetDocument(liveFaces: 2, reservedSlots: 2)));
    }
    [Fact]
    public void AFaceShowingNothingClaimsNoSlot() {
        // The reason play's dark cabinet stops competing with its doors: a source that renders nothing asks for no
        // screen, so an at-capacity band still seats every live face.
        var dark = BuildBudgetDocument(liveFaces: 3, reservedSlots: 2);
        var placements = new List<WorldPlacement>(collection: dark.Placements);

        placements[0] = (placements[0] with { FaceSources = [new WorldPlacementFace(Face: DoorFace, Source: new WorldScreenSource.None())] });

        var catalog = WorldFaceCatalog.For(definition: (dark with { PlacementRowsRaw = placements }));

        Assert.Equal(expected: 2, actual: catalog.ClaimingFaceCount);
        Assert.True(condition: Validates(definition: (dark with { PlacementRowsRaw = placements })));
        Assert.Equal(expected: -1, actual: catalog.Rows[0].ScreenIndex);
        Assert.False(condition: catalog.Rows[0].SlotStarved);
    }
    [Fact]
    public void SlotExhaustionDarkensTheNewestClaimant_ButNeverRemovesItsGeometry() {
        // The dynamic half of the budget: a band that cannot seat everything must darken, loudly and by name, and
        // the darkened face must still carry the frame its door is derived from.
        var catalog = WorldFaceCatalog.For(definition: BuildBudgetDocument(liveFaces: 3, reservedSlots: 2));
        var starved = catalog.Rows[^1];

        Assert.True(condition: starved.SlotStarved);
        Assert.Equal(expected: -1, actual: starved.ScreenIndex);
        Assert.NotEqual(expected: default, actual: starved.Frame);
        Assert.Equal(expected: SdfSolidPrimitive.Box, actual: starved.Aperture?.Primitive);
        Assert.Contains(collection: catalog.Notices, filter: notice => (notice.Contains(value: starved.PlacementId, comparisonType: StringComparison.Ordinal) && notice.Contains(comparisonType: StringComparison.Ordinal, value: "DARKENED")));
        Assert.Equal(expected: WorldPlacementPolicy.DerivedFaceBase, actual: catalog.Rows[0].ScreenIndex);
        Assert.Equal(expected: (WorldPlacementPolicy.DerivedFaceBase + 1), actual: catalog.Rows[1].ScreenIndex);
    }
}
