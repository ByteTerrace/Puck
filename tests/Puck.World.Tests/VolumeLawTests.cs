using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a creation's <c>volumes[]</c> (<see cref="VolumeDocument"/>) is admitted only as a known kind whose
/// parent names a declared shape, with a positive box, in-range steps, and hex colours — each refused by name; a
/// live registration packs one <see cref="SdfVolume"/> per authored volume riding the parent shape's dynamic slot
/// (or the root's), scaled by the look scale; and a static placement bakes the placement frame into the volume with
/// no slot. A volume never reaches the program: the emitted tape carries no instruction for it.
/// </summary>
public sealed class VolumeLawTests {
    private static readonly Vector3 NozzleOffset = new(x: 0.1f, y: 0.2f, z: 0.3f);

    private static ShapeDocument Nozzle() => new(
        Id: 0,
        Name: "nozzle",
        Type: SdfSolidPrimitive.Cylinder,
        Position: new Vector3(x: 0f, y: 1f, z: 0f),
        Rotation: Quaternion.Identity,
        Scale: new Vector3(x: 0.1f, y: 0.2f, z: 0.1f),
        Material: 0,
        Blend: SdfBlendOp.Union,
        Smooth: 0f,
        Group: 0
    );
    private static VolumeDocument Flow(string kind = VolumeDocument.FlowKind, string? parent = "nozzle", int? steps = null, float halfY = 0.4f, string? core = null) => new(
        Kind: kind,
        Position: NozzleOffset,
        Rotation: Quaternion.Identity,
        HalfExtent: new Vector3(x: 0.2f, y: halfY, z: 0.2f),
        Parent: parent,
        Steps: steps,
        Ramp: [new(0f, core ?? "#FFFFFF")],
        IntensityLane: 1
    );
    private static CreationDocument Document(params VolumeDocument[] volumes) => new(
        Schema: CreationDocument.CurrentSchema,
        Name: "jet",
        Palette: null,
        Shapes: [Nozzle()],
        Frames: null,
        Volumes: volumes
    );
    private static void AssertRefusesNaming(CreationDocument document, string needle) {
        var violations = CreationCanonicalizer.Validate(document: document);

        Assert.NotEmpty(collection: violations);
        Assert.Contains(
            collection: violations,
            filter: violation => violation.Path.Contains(comparisonType: StringComparison.Ordinal, value: needle)
        );
    }

    [Fact]
    public void AFlowRidingADeclaredShapeIsAdmitted() =>
        Assert.Empty(collection: CreationCanonicalizer.Validate(document: Document(Flow())));

    [Fact]
    public void AFlowRidingTheRootIsAdmitted() =>
        Assert.Empty(collection: CreationCanonicalizer.Validate(document: Document(Flow(parent: null))));

    [Fact]
    public void ACloudPreservesItsDensityControlsAndScalesItsNoiseCells() {
        var cloud = Flow(kind: VolumeDocument.CloudKind, parent: null) with {
            Width = 2f, Coverage = 0.7f, Softness = 0.12f, IntensityLane = null,
        };
        Assert.Empty(CreationCanonicalizer.Validate(Document(cloud)));
        var volume = cloud.ToVolume(-1, Vector3.Zero, Quaternion.Identity, 3f);
        volume.Validate(0);
        Assert.Equal(SdfVolumeKind.Cloud, volume.Kind);
        Assert.Equal(6f, volume.Width);
        Assert.Equal(0.7f, volume.Coverage);
        Assert.Equal(0.12f, volume.Softness);
        Assert.Equal(NozzleOffset * 3f, volume.Position);
    }

    [Theory]
    [InlineData(-0.1f, 0.2f)]
    [InlineData(1.1f, 0.2f)]
    [InlineData(0.5f, 0f)]
    [InlineData(0.5f, 1.1f)]
    public void InvalidCloudDensityControlsAreRefused(float coverage, float softness) =>
        AssertRefusesNaming(Document(Flow(kind: VolumeDocument.CloudKind) with {
            Coverage = coverage, Softness = softness,
        }), "volumes[0]");

    [Fact]
    public void DensityFamilyControlsCannotSilentlyDisappear() {
        AssertRefusesNaming(Document(Flow() with { Coverage = 0.5f }), ".coverage");
        AssertRefusesNaming(Document(Flow(kind: VolumeDocument.CloudKind) with { Axis = 1f }), ".axis");
    }

    [Fact]
    public void SixteenJetsSurviveAnimatedEmissionAndDisabledVolumesUseNoSlot() {
        var creation = CreationDomainParentLawTests.Prototype(document: Document([
            Flow() with { Enabled = false },
            .. Enumerable.Range(0, 16).Select(index => Flow() with { Seed = (uint)index }),
        ]));
        var definition = CreationDomainParentLawTests.Definition(creation: creation, scale: 1f);
        var client = CreationDomainParentLawTests.Client(definition: definition);
        var pool = CreationDomainParentLawTests.Pool(creation: creation, scale: 1f);
        var transforms = new DynamicTransform[WorldStampPool.DynamicSlotCount];
        CreationDomainParentLawTests.Advance(client: client, pool: pool, transforms: transforms, tick: 1UL);
        Assert.Equal(16, pool.Volumes.Count);
        Assert.Equal(Enumerable.Range(0, 16).Select(index => (uint)index), pool.Volumes.Select(volume => volume.Seed));
    }

    [Fact]
    public void ADisabledStaticCloudEmitsNoVolume() {
        var creation = CreationDomainParentLawTests.Prototype(Document(Flow(kind: VolumeDocument.CloudKind) with { Enabled = false }));
        var placement = new WorldPlacement(Id: "cloud", PrototypeId: creation.Id, Position: Vector3.Zero, YawDegrees: 0f, Scale: 1f);
        var definition = Fixtures.BuildDocument() with { CreationsRaw = [creation], PlacementRowsRaw = [placement] };
        var volumes = new List<SdfVolume>();
        WorldPlacementStamper.EmitStatic(new SdfProgramBuilder(), definition, [creation], [placement], volumes: volumes);
        Assert.Empty(volumes);
    }

    [Fact]
    public void AnUnknownKindIsRefusedByName() =>
        AssertRefusesNaming(document: Document(Flow(kind: "smoke")), needle: "volumes[0].kind");

    [Fact]
    public void AParentNamingNoShapeIsRefusedByName() =>
        AssertRefusesNaming(document: Document(Flow(parent: "missing")), needle: "volumes[0].parent");

    [Fact]
    public void StepsPastTheCeilingAreRefusedByName() =>
        AssertRefusesNaming(document: Document(Flow(steps: (SdfVolume.MaxSteps + 1))), needle: "volumes[0].steps");

    [Fact]
    public void AFlatBoxIsRefusedByName() =>
        AssertRefusesNaming(document: Document(Flow(halfY: 0f)), needle: "volumes[0].halfExtent");

    [Fact]
    public void ANonHexColourIsRefusedByName() =>
        AssertRefusesNaming(document: Document(Flow(core: "blue")), needle: "volumes[0].ramp.color");

    [Fact]
    public void MoreVolumesThanTheEngineCeilingAreRefusedByName() =>
        AssertRefusesNaming(
            document: Document([.. Enumerable.Repeat(element: Flow(), count: (SdfProgramBuilder.MaxVolumes + 1))]),
            needle: "volumes"
        );

    [Fact]
    public void ALiveRegistrationPacksOneVolumePerAuthoredEntryOnTheParentShapeSlot() {
        var creation = CreationDomainParentLawTests.Prototype(document: Document(Flow(), Flow(parent: null)));
        var definition = CreationDomainParentLawTests.Definition(creation: creation, scale: 2f);
        var client = CreationDomainParentLawTests.Client(definition: definition);
        var pool = CreationDomainParentLawTests.Pool(creation: creation, scale: 2f);
        var transforms = new DynamicTransform[WorldStampPool.DynamicSlotCount];

        CreationDomainParentLawTests.Advance(client: client, pool: pool, transforms: transforms, tick: 1UL);

        Assert.Equal(expected: 2, actual: pool.Volumes.Count);

        var onNozzle = pool.Volumes[0];
        var onRoot = pool.Volumes[1];

        // Slot 0 is the registration root; the first shape rides slot 1.
        Assert.Equal(expected: 1, actual: onNozzle.DynamicSlot);
        Assert.Equal(expected: 0, actual: onRoot.DynamicSlot);
        Assert.Equal(expected: (NozzleOffset * 2f), actual: onNozzle.Position);
        Assert.Equal(expected: new Vector3(x: 0.4f, y: 0.8f, z: 0.4f), actual: onNozzle.HalfExtent);
        Assert.Equal(expected: SdfVolumeKind.Flow, actual: onNozzle.Kind);
        Assert.Equal(1, onNozzle.IntensityLane);
        Assert.Equal(expected: VolumeDocument.DefaultSteps, actual: onNozzle.Steps);
    }

    [Fact]
    public void AStaticPlacementBakesThePlacementAndParentFramesWithNoSlot() {
        var creation = CreationDomainParentLawTests.Prototype(document: Document(Flow()));
        var placement = new WorldPlacement(Id: "jet", PrototypeId: creation.Id, Position: new Vector3(x: 10f, y: 0f, z: 0f), YawDegrees: 90f, Scale: 2f);
        var definition = (Fixtures.BuildDocument() with {
            CreationsRaw = [creation],
            PlacementRowsRaw = [placement],
        });
        var builder = new SdfProgramBuilder();
        var volumes = new List<SdfVolume>();

        WorldPlacementStamper.EmitStatic(
            builder: builder,
            creations: [creation],
            definition: definition,
            placements: [placement],
            volumes: volumes
        );

        var program = builder.Build(buildInstanceGrid: false);
        var volume = Assert.Single(collection: volumes);
        // The same resolved placement frame the stamper bakes every shape against, composed over the nozzle's
        // engine-frame pose (CreationFrame.ToEngine) — a parented volume's offset is shape-local.
        var frame = WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement);
        var yaw = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: (frame.YawDegrees * (MathF.PI / 180f)));
        var nozzle = creation.EngineDocument.Shapes![0];
        var expected = (frame.Position + Vector3.Transform(
            rotation: yaw,
            value: ((nozzle.Position.Value * 2f) + Vector3.Transform(
                rotation: nozzle.Rotation.Value,
                value: (NozzleOffset * 2f)
            ))
        ));

        Assert.Equal(expected: -1, actual: volume.DynamicSlot);
        Assert.InRange(actual: Vector3.Distance(value1: expected, value2: volume.Position), low: 0f, high: 1e-4f);
        Assert.Equal(expected: new Vector3(x: 0.4f, y: 0.8f, z: 0.4f), actual: volume.HalfExtent);
        // The tape carries the nozzle alone — a volume emits no instruction.
        Assert.Equal(expected: 1, actual: program.Instructions.Count(predicate: static instruction => (instruction.Op == SdfOp.ShapeBlend)));
    }
}
